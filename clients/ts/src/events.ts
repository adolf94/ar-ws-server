/**
 * The authoritative envelope shape (realtime docs/CONTRACT.md §3).
 * Every delivery is SignalR method "event"; identity lives in `data.type`,
 * with server-stamped provenance of the publisher (from = caller client_id).
 * Receivers IGNORE unknown types forward (route loosely, never crash).
 */
export interface RealtimeEvent<Type extends string = string> {
  type: Type;
  /** publisher provenance: the CALLING service's client_id (server-stamped) */
  from: string;
  /** RFC 3339 UTC (server-stamped) */
  sent_at: string;
  [key: string]: unknown;
}

/** Errors thrown for hub lifecycle problems (auth, connect, transport). */
export class RealtimeHubError extends Error {
  public constructor(
    message: string,
    public override readonly cause?: unknown,
  ) {
    super(message);
    this.name = "RealtimeHubError";
  }
}
