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
  /** hub path, e.g. "https://realtime.example.com/realtime/hub" (WS-only upstream) */
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
  logLevel?: LogLevel;
}

export interface RealtimeHubDisposable {
  stop(): Promise<void>;
}

export class RealtimeHubClient {
  private connection: HubConnection | null = null;
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
    connection.onreconnecting((err) =>
      console.warn("realtime-client: reconnecting after", err?.message ?? String(err)));
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
