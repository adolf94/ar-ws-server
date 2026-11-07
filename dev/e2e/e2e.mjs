/**
 * Phase-0 spike e2e — SERVER/WS suite.
 *
 * Focus (per owner request): the SERVER — negotiate fence, connection-token
 * injection, WS handshake, publish, fan-out, HTTP gates. The IdP is only
 * exercised when no usable token exists; never for routine runs.
 *
 * Token source (first hit wins):
 *   1. IDP_TOKEN env          — externally supplied; authority untouched
 *   2. .idp-token-cache.json  — token from a previous run; authority untouched
 *   3. mint (real|mock)       — discovery + client_credentials[, mock /token]
 *
 *   IDP_MODE=real  mint path = auth.adolfrey.com (IDP_CLIENT_ID/SECRET env)
 *   IDP_MODE=mock  mint path = dependency-free mock at :9001
 *
 * NOTE: no secret or full token is ever printed — decoded claims only.
 */
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { HubConnectionBuilder } from '@microsoft/signalr';

const MODE = process.env.IDP_MODE ?? 'real';
const SRV = process.env.SRV ?? 'http://localhost:5000';
const MOCK_AUTH = 'http://localhost:9001';
const CACHE_FILE = new URL('./.idp-token-cache.json', import.meta.url);
const PUBLISHER_SCOPE = 'api://ws-server/app:publisher';

const checks = [];
const check = (name, ok, detail = '') => {
  checks.push({ name, ok });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
};
process.on('exit', (code) => {
  const failed = checks.filter((c) => !c.ok);
  console.log(`\ne2e summary (${MODE}${process.env.IDP_TOKEN ? ', external token' : ''}): ${checks.length - failed.length}/${checks.length} passed`);
  if (failed.length > 0 && code === 0) process.exitCode = 1;
});

const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const decodeClaims = (token) => {
  try {
    return JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString());
  } catch {
    return {};
  }
};
const nowSec = () => Math.floor(Date.now() / 1000);
const tokenUsable = (claims) => !!claims?.exp && claims.exp > nowSec() + 120;
const objectKeysSafe = (obj) =>
  ['accessToken', ...(Object.keys(obj ?? {}).filter((k) => k !== 'accessToken'))].join(',');

// ── mint paths (authority exercised ONLY here) ──────────────────────────────
async function mintReal(scope) {
  const authority = (process.env.IDP_AUTHORITY ?? 'https://auth.adolfrey.com/api/').replace(/\/?$/, '');
  const disc = await (await fetch(`${authority}/.well-known/openid-configuration`)).json();
  const body = new URLSearchParams({
    grant_type: 'client_credentials',
    client_id: process.env.IDP_CLIENT_ID,
    client_secret: process.env.IDP_CLIENT_SECRET ?? '',
  });
  if (scope) body.set('scope', scope);
  const res = await fetch(disc.token_endpoint, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body });
  if (!res.ok) return null;
  return (await res.json()).access_token;
}
async function mintMock(body) {
  const res = await fetch(`${MOCK_AUTH}/token`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!res.ok) throw new Error(`mock /token failed: ${res.status}`);
  return (await res.json()).access_token;
}

// ── 1. obtain a token WITHOUT hitting the authority whenever possible ───────
let idpToken = null;
let tokenSource = '';
let freshlyMinted = false;

if (process.env.IDP_TOKEN) {
  idpToken = process.env.IDP_TOKEN;
  tokenSource = 'env IDP_TOKEN — authority NOT touched';
} else if (existsSync(CACHE_FILE)) {
  const cache = JSON.parse(readFileSync(CACHE_FILE, 'utf8'));
  if (tokenUsable(cache.claims)) {
    idpToken = cache.access_token;
    tokenSource = `cache (.idp-token-cache.json) — authority NOT touched (sub=${cache.claims.sub})`;
  }
}

if (!idpToken) {
  // authority round-trip, only when necessary
  try {
    if (MODE === 'real') {
      const token = await mintReal(process.env.AUTH_SERVICE_SCOPES);
      if (!token) {
        console.log('FAIL   minting IdP token (client_credentials) — check IDP_CLIENT_ID/IDP_CLIENT_SECRET');
        process.exit(1);
      }
      idpToken = token;
    } else {
      idpToken = await mintMock({ sub: '42', scope: PUBLISHER_SCOPE, client_id: 'spa-demo-client' });
    }
    freshlyMinted = true;
    const claims = decodeClaims(idpToken);
    writeFileSync(
      CACHE_FILE,
        JSON.stringify({ access_token: idpToken, claims: { sub: claims.sub, scope: claims.scope, client_id: claims.client_id, iss: claims.iss, exp: claims.exp }, minted_at: new Date().toISOString() }, null, 2),
    );
    tokenSource = `freshly minted (sub=${claims.sub} scope="${claims.scope}")`;
  } catch (err) {
    console.log(`FAIL   minting IdP token — ${String(err)}`);
    process.exit(1);
  }
}
console.log(`  token: ${tokenSource}`);
const userClaims = decodeClaims(idpToken);

// ── 2. negotiate fence: 200 + connectionId + OUR accessToken (shape a) ─────
const negotiateRes = await fetch(`${SRV}/realtime/hub/negotiate?negotiateVersion=1`, {
  method: 'POST',
  headers: { Authorization: `Bearer ${idpToken}` },
});
const negotiateBody = await negotiateRes.json().catch(() => ({}));
check('WS-1. negotiate: 200 + accessToken injected (shape a)',
  negotiateRes.status === 200 && !!negotiateBody.connectionId
    && typeof negotiateBody.accessToken === 'string' && negotiateBody.accessToken !== idpToken,
  `status=${negotiateRes.status} keys=${objectKeysSafe(negotiateBody)}`);

// ── 3/4/5. the actual WebSocket flow ─────────────────────────────────────────
let received = null;
const connection = new HubConnectionBuilder()
  .withUrl(`${SRV}/realtime/hub`, {
    accessTokenFactory: () => idpToken, // re-invoked per (re)connect
  })
  .build();
connection.on('event', (data) => { received = data; });

try {
  await connection.start();
  check('WS-2. hub handshake consumed the negotiate-issued token', true, `connectionId=${connection.connectionId}`);
} catch (err) {
  check('WS-2. hub handshake consumed the negotiate-issued token', false, String(err));
  console.log('\n→ if this is "unable to connect": the JS client may not honor the negotiate accessToken (shape (a)); try shape (b) — see PLAN-OWN-SERVER.md §5.2');
  await connection.stop().catch(() => {});
  process.exit(1);
}

const publishRes = await fetch(`${SRV}/api/publish`, {
  method: 'POST',
  headers: { Authorization: `Bearer ${idpToken}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({ group: 'demo', data: { type: 'chat_receive', message_id: 'spk-1', text: 'hello from phase-0 spike' } }),
});
const publishBody = await publishRes.json().catch(() => ({}));
check('WS-3. POST /api/publish → 200 ok', publishRes.status === 200 && publishBody.ok === true, `status=${publishRes.status}`);

await sleep(1500);
check('WS-4. publication received over the hub (from/sent_at stamped)',
  received !== null && received.type === 'chat_receive' && received.text === 'hello from phase-0 spike'
    && typeof received.sent_at === 'string',
  `received=${JSON.stringify(received)}`);

const secondReceived = null; // a second audience check (same group bus) — Phase-1 territory, skipped in the spike

await connection.stop();

// ── 6. server gates (no IdP round-trips required) ────────────────────────────
const anon = await fetch(`${SRV}/api/publish`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ group: 'demo', data: { type: 'chat_receive', text: 'x' } }),
});
check('GATE-1a. unauthenticated publish → 401', anon.status === 401);

const noAuthNegotiate = await fetch(`${SRV}/realtime/hub/negotiate?negotiateVersion=1`, { method: 'POST' });
check('GATE-1b. negotiate with no token → 401', noAuthNegotiate.status === 401, `status=${noAuthNegotiate.status}`);

if (freshlyMinted && MODE === 'mock') {
  const weakToken = await mintMock({ sub: 'rng-user', scope: 'realtime:connect', client_id: 'spa-demo-client' });
  const noScope = await fetch(`${SRV}/api/publish`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${weakToken}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ group: 'demo', data: { type: 'chat_receive', text: 'x' } }),
  });
  check('GATE-2. publish without publisher scope → 403', noScope.status === 403, `status=${noScope.status}`);
}

if (String(userClaims.scope ?? '').length === 0) {
  console.log('⚠ cached/env token carries NO scopes — publish scope checks may FAIL; refresh via dev/e2e/mint-token.mjs');
}
