/**
 * User-Agent customization (owner requirement: the service sits behind
 * Cloudflare).
 *
 * Why it matters: Cloudflare bot mitigation can challenge non-browser
 * clients — a challenge page instead of the negotiate JSON or a dropped WS
 * upgrade is notoriously hard to diagnose. SignalR's stock Node UA
 * ("Microsoft SignalR/…") is exactly the shape such rules tend to sweat, so
 * every lane we control sends an honest, boring, recognizable client
 * identity:
 *
 *   - Node lanes (negotiate POST + WS handshake headers): we stick our
 *     identity into `options.headers`, which overrides SignalR's default —
 *     verified in @microsoft/signalr: HttpConnection merges `options.headers`
 *     LAST on negotiate (HttpConnection.ts:322) and passes them to the ws
 *     constructor (WebSocketTransport.ts:69).
 *   - Browser lanes: the browser owns the `User-Agent` header; the negotiate
 *     override still lands via `X-SignalR-User-Agent` (the header name
 *     SignalR selects when not in Node — Utils.getUserAgentHeader), which
 *     the server's CORS policy explicitly allows.
 *
 * HTTP lanes of this package (tooling) simply set `User-Agent` directly.
 */
export const LIB_NAME = "realtime-client-ts";
export const LIB_VERSION = "0.3.2";
export const USER_AGENT = `${LIB_NAME}/${LIB_VERSION}`;
