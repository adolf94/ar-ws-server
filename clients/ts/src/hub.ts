import {
  HubConnection,
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from "@microsoft/signalr";
import { USER_AGENT } from "./agent.ts";
import { type RealtimeEvent, RealtimeHubError } from "./events.ts";

/**
 * Re-connect lifecycle hook surface, aligned with the server contract
 * (docs/CONTRACT.md): every reconnect re-negotiates, so the tokenFactory
 * must be able to hand a FRESH IdP token each time.
 */
export interface RealtimeHubOptions {
  /** hub path, e.g. "https://realtime.example.com/realtime/hub" (WS-only
   * upstream). Append `?instance=<stable-tag>` (a UUID kept in
   * localStorage / process env) to get a reconnect-stable logical
   * connection_id minted by the server (CONTRACT §2.2) — otherwise the id
   * is per-socket and changes on every reconnect. */
  url: string;
  /**
   * Returns an IdP access token; re-invoked by the SDK on every (re)connect.
   * In a browser, supply @adolf94/ar-auth-client's session token:
   *   tokenFactory: () => getToken()
   * The fence turns it into a short-lived connection JWT at negotiate —
   * NEVER set skipNegotiation and never cache the connection JWT yourself.
   */
  tokenFactory: () => Promise<string>;
  /** override the client UA (the Cloudflare lane); browsers keep theirs */
  userAgent?: string;
  /**
   * reconnect preset: `false` stops after a drop; an interval array feeds
   * withAutomaticReconnect; undefined = SignalR default (0s/3s/6s then stop).
   */
  reconnectIntervals?: number[] | false;
  /**
   * Fires after an auto-reconnect succeeds with the server-minted logical
   * connection_id (stable across reconnects for a tagged `?instance=`;
   * freshly minted per socket otherwise — CONTRACT §2.2). Roughly state
   * `connecting` -> `connected`. The FIRST successful start() does not
   * fire this (read `connectionId` after `await start()` instead — the
   * connection_registered envelope has landed by then).
   */
  onReconnected?: (connectionId: string | null) => void;
  logLevel?: LogLevel;
}

export interface RealtimeHubDisposable {
  stop(): Promise<void>;
}

export class RealtimeHubClient {
  private connection: HubConnection | null = null;
  /** server-minted logical id (CONTRACT §2.2/§5) — captured from the
   * connection_registered envelope; null until the envelope lands. */
  private logicalId: string | null = null;
  /** set when a reconnect begins; cleared once the fresh
   * connection_registered envelope lands → onReconnected fires exactly once */
  private awaitingReconnectedId = false;
  private readonly handlers = new Map<string, Set<(event: RealtimeEvent) => unknown>>();
  private catchAll: ((event: RealtimeEvent) => unknown) | null = null;

  public constructor(private readonly options: RealtimeHubOptions) {}

  /**
   * Subscriptions should exist BEFORE start() to avoid missing early events
   * (there is no store-and-forward on the bus — docs/CONTRACT.md §3).
   */
  public on(type: string, handler: (event: RealtimeEvent) => unknown): this {
    const set = this.handlers.get(type) ?? new Set();
    set.add(handler);
    this.handlers.set(type, set);
    return this;
  }

  /** fires for every envelope, before type-routed handlers */
  public onAny(handler: (event: RealtimeEvent) => unknown): this {
    this.catchAll = handler;
    return this;
  }

  public get state(): "new" | "connecting" | "connected" | "stopped" {
    if (!this.connection) return "new";
    const raw = this.connection.state;
    if (raw === "Connected") return "connected";
    if (raw === "Connecting" || raw === "Reconnecting") return "connecting";
    return "stopped";
  }

  /**
   * Server-minted logical connection id (CONTRACT §2.2) — the address other
   * services use for `POST /api/connections/{id}/send` and disconnect-by-id.
   * Set once the connection_registered envelope lands after connect; null
   * until then. UNLIKE the underlying socket id it survives reconnects —
   * same instance tag, same id (supply `?instance=<tag>` in the hub url to
   * opt in; untagged sockets get a fresh per-socket id each reconnect).
   */
  public get connectionId(): string | null {
    return this.logicalId;
  }

  public async start(): Promise<void> {
    if (this.connection) throw new RealtimeHubError("already started (or starting)");

    // negotiate IS the auth entry point (docs/INTEGRATION.md §2) — never skip.
    const builder = new HubConnectionBuilder()
      .withUrl(this.options.url, {
        accessTokenFactory: this.options.tokenFactory,
        transport: HttpTransportType.WebSockets, // WS-only upstream (docs/TECH.md)
        // both names: Node lane merges options.headers over the stock UA on
        // negotiate AND forwards them to the ws constructor; browsers ignore
        // the forbidden User-Agent header and honor the X-override instead.
        headers: {
          "User-Agent": this.effectiveUserAgent(),
          "X-SignalR-User-Agent": this.effectiveUserAgent(),
        },
      });
    if (this.options.reconnectIntervals !== false) {
      builder.withAutomaticReconnect(
        this.options.reconnectIntervals ?? [0, 3000, 6000, 12000, 24000],
      );
    }
    const connection = builder
      .configureLogging(this.options.logLevel ?? LogLevel.Error)
      .build();
    connection.on("event", (envelope: unknown) => this.dispatch(envelope));
    connection.onreconnecting((err) => {
      // the minted id may change on reconnect (untagged) or stay (tagged) —
      // deliver the FRESH one when the connection_registered envelope lands
      this.awaitingReconnectedId = true;
      console.warn("realtime-client: reconnecting after", err?.message ?? String(err));
    });
    this.connection = connection;
    await connection.start();
  }

  /** idempotent */
  public async stop(): Promise<void> {
    const connection = this.connection;
    this.connection = null;
    if (connection) await connection.stop();
  }

  private effectiveUserAgent(): string {
    return this.options.userAgent ?? `Microsoft SignalR/10 (via ${USER_AGENT})`;
  }

  private envelope(raw: unknown): RealtimeEvent | null {
    if (typeof raw !== "object" || raw === null) return null;
    const record = raw as Record<string, unknown>;
    if (typeof record["type"] !== "string") return null; // ignore contract-less frames
    return raw as RealtimeEvent;
  }

  private dispatch(raw: unknown): void {
    const envelope = this.envelope(raw);
    if (!envelope) return; // receivers ignore unknown/no-type frames (contract §3)

    if (envelope.type === "connection_registered" && typeof (envelope as Record<string, unknown>)["connection_id"] === "string") {
      // server mint/bind notification (CONTRACT §5) — capture the logical id
      this.logicalId = (envelope as Record<string, unknown>)["connection_id"] as string;
      if (this.awaitingReconnectedId) {
        this.awaitingReconnectedId = false;
        this.options.onReconnected?.(this.logicalId);
      }
    }

    this.catchAll?.(envelope);
    const set = this.handlers.get(envelope.type);
    set?.forEach((handler) => {
      try {
        handler(envelope);
      } catch (handlerError) {
        // a subscriber bug must not kill the hub subscription
        console.error(`realtime-client: handler for ${envelope.type} failed`, handlerError);
      }
    });
  }
}

/** small factory (idiomatic with signalr one-liners) */
export function createRealtimeHub(options: RealtimeHubOptions): RealtimeHubClient {
  return new RealtimeHubClient(options);
}
