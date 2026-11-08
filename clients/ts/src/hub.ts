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
  /** Coalesced-batch handling (CONTRACT §3.1): a publisher's single-flight
   * stream can deliver ONE outer envelope carrying several original events
   * (`{type, batch: [chunk…], batch_v: 1}`, server-stamped once).
   *   * `flatten` (default) — every batch item is dispatched through
   *     `on`/`onAny` INDIVIDUALLY, in order, exactly like a plain §3
   *     delivery; chunks that omit `from`/`sent_at` inherit the outer
   *     envelope's stamped provenance, so no consumer code changes are
   *     needed to receive coalesced streams.
   *   * `raw` — the outer envelope passes through as ONE event; you loop
   *     `envelope.batch` yourself.
   * Unknown `batch_v` values always pass through raw (forward compat:
   * "new envelope additions would bump it"). */
  coalescing?: "flatten" | "raw";
  /** Fires once per coalesced outer envelope (both modes) BEFORE flatten —
   * the provenance and `batch_v`/`batch` fields live here; use it for
   * flush boundaries, stream handles, or telemetry. */
  onBatch?: (envelope: RealtimeEvent) => unknown;
  /**
   * Stable instance self-label (CONTRACT §2.2) — CLIENT-SET by design, and
   * never security-relevant: the fence keeps identity inside the connection
   * JWT; a forged tag only addresses a sibling socket of the same sub.
   * Semantics: same tag → the server re-binds the SAME logical
   * connection_id across reconnects (stale sockets superseded); no tag →
   * fresh per-socket id on every reconnect.
   * DEFAULT `true` (v0.3.0): the SDK generates a UUID and persists it in
   * sessionStorage (`realtime-instance-tag`) in the browser — PER-TAB, so
   * every tab is its own logical instance (a localStorage-shared tag would
   * make two tabs supersede-abort each other's sockets on connect); Node
   * holds a per-process UUID — reconnects from the same tab/process keep
   * their id. `false` opts back into legacy per-socket ids; a string is
   * used verbatim (e.g. the SPA's own stored UUID). A pre-existing
   * `?instance=` in the hub url is honored as-is (never clobbered).
   */
  instanceTag?: string | boolean;
  /**
   * Connect-time partition rider (CONTRACT §1, additive 2026-11-08) —
   * appended to the hub URL as `?partition=` for EVERY negotiate (merged
   * into the query alongside `?instance=`, preserved across
   * re-negotiates).
   * DEFAULT: the token's own `client_id`. Appended explicitly ONLY when
   * the resolved token's aud contains it (mirroring the server's alias
   * gate — a rider the fence would reject must never go on the wire);
   * otherwise no rider is sent and the fence assigns the same partition
   * implicitly, so the connection lands identically either way.
   * Point the option at the RESOURCE audience your token already carries
   * (e.g. the backend's client, `finance-app-api`) to land the connection
   * in that partition so its group memberships apply to this socket. The
   * bus plane (hub identity/memberships) uses the partition; the REST
   * plane keeps the real token's client_id. Rejection surfaces as a
   * failed start(): 403 `partition_not_in_token` (aud miss) or
   * `partition_not_allowed` (reserved pseudo-partition, e.g. user_channel).
   */
  partition?: string;
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

    // partition DEFAULT (CONTRACT §1): the token's own client_id. It is
    // appended EXPLICITLY only when the resolved token's aud contains it —
    // mirroring the server's alias gate — because a rider the fence would
    // reject must never go on the wire. When omitted here, the fence
    // assigns the same partition implicitly, so the connection lands in
    // the same place either way; the rider just makes the identity
    // deterministic and visible in negotiate logs. The resolution turns
    // the tokenFactory once per start(); a reconnect keeps this rider
    // (fresh tokens ride the same URL — supply partition explicitly if
    // your tokens can rotate clients mid-session).
    let partition = this.options.partition;
    if (!partition) {
      try {
        const claims = decodeToken(await this.options.tokenFactory());
        const clientId = claims?.client_id, aud = claims?.aud;
        const audiences = (Array.isArray(aud) ? aud : [aud])
          .filter((a): a is string => typeof a === "string");
        if (typeof clientId === "string" && clientId.length > 0
            && audiences.includes(clientId)) partition = clientId;
      } catch {
        // tokenFactory failure here would double-signal at negotiate —
        // degrade to the implicit partition (no rider) instead.
      }
    }

    // negotiate IS the auth entry point (docs/INTEGRATION.md §2) — never skip.
    const builder = new HubConnectionBuilder()
      .withUrl(this.hubUrl(partition), {
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

  /** hub URL with the riders merged into the query (CONTRACT §1/§2.2):
   *  `partition` (explicit option, or the token's own client_id default —
   *  aud-gated) and `instance` (SDK-managed self-label) — set via
   *  searchParams so caller-supplied params (incl. pre-existing
   *  `?instance=`) survive instead of being clobbered. */
  private hubUrl(partition?: string): string {
    const url = new URL(this.options.url); // absolute hub URL is the documented shape
    if (partition && partition.length > 0) url.searchParams.set("partition", partition);
    const tag = resolveInstanceTag(this.options.instanceTag ?? true);
    if (tag) url.searchParams.set("instance", tag);
    return url.toString();
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

    // coalesced batch (CONTRACT §3.1): known version → replay; unknown version → raw
    const record = envelope as Record<string, unknown>;
    const batch = record["batch"];
    if (Array.isArray(batch) && record["batch_v"] === 1) {
      this.options.onBatch?.(envelope);
      if (this.options.coalescing !== "raw") {
        const from = record["from"], sentAt = record["sent_at"];
        for (const item of batch) {
          const chunk = this.envelope(item);
          if (!chunk) continue; // contract-less chunk: ignored (§3 receiver rule)
          const chunkRecord = chunk as Record<string, unknown>;
          // chunks ride VERBATIM (§3.1) — provenance is inner where present:
          if (chunkRecord["from"] === undefined && from !== undefined) chunkRecord["from"] = from;
          if (chunkRecord["sent_at"] === undefined && sentAt !== undefined) chunkRecord["sent_at"] = sentAt;
          this.deliver(chunk);
        }
        return; // the outer envelope's payload IS the batch — don't double-deliver
      }
    }

    this.deliver(envelope);
  }

  private deliver(envelope: RealtimeEvent): void {
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

/* ── instance tag resolution (CONTRACT §2.2, client-set self-label) ─────── */

/** claims of a bearer token — no library: base64url decode of the payload */
function decodeToken(token: string): Record<string, unknown> | null {
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  try {
    const json = new TextDecoder().decode(Uint8Array.from(
      atob(parts[1]!.replaceAll("-", "+").replaceAll("_", "/")),
      (c) => c.charCodeAt(0),
    ));
    return JSON.parse(json);
  } catch {
    return null;
  }
}

// sessionStorage (NOT localStorage): the tag must be PER-TAB — every tab is
// its own logical client instance. A localStorage-shared tag would make two
// tabs supersede-abort each other's sockets on every connect.
const INSTANCE_STORAGE_KEY = "realtime-instance-tag";

const hasSessionStorage = typeof sessionStorage !== "undefined";
const hasNetworkUuid =
  typeof crypto !== "undefined" && typeof crypto.randomUUID === "function";
const moduleTag: string | null =
  hasNetworkUuid ? crypto.randomUUID() : null; // Node fallback: stable per process

function resolveInstanceTag(option?: string | boolean): string | null {
  if (option === true) {
    if (hasSessionStorage) {
      try {
        const existing = sessionStorage.getItem(INSTANCE_STORAGE_KEY);
        if (existing) return existing;
        const fresh = hasNetworkUuid ? crypto.randomUUID() : null;
        if (fresh) {
          sessionStorage.setItem(INSTANCE_STORAGE_KEY, fresh);
          return fresh;
        }
      } catch {
        return moduleTag; // storage blocked → per-process fallback
      }
    }
    return moduleTag; // Node: per-process tag (survives reconnects, not restarts)
  }
  return typeof option === "string" && option.length > 0 ? option : null;
}
