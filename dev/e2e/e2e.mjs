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
  // a cached token is only usable for the CURRENT authority (mock vs real
  // have different issuers — never reuse one lane's token on the other)
  const authority = (process.env.AUTH_AUTHORITY ?? '').replace(/\/?$/, '');
  const issuerMatches = authority === '' || String(cache.claims?.iss ?? '').startsWith(authority);
  if (tokenUsable(cache.claims) && issuerMatches) {
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

// ── groups: POST /api/groups + member add (Phase 1, owner spec) ─────────────
const authHdrs = { Authorization: `Bearer ${idpToken}`, 'Content-Type': 'application/json' };
const memberSub = userClaims.sub; // member's client partition = bearer's client_id (owner spec)

const createRes = await fetch(`${SRV}/api/groups`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ name: 'demo' }),
});
let createBody = await createRes.json().catch(() => ({}));
check('G-1. POST /api/groups → created/auto client_id partition',
  (createRes.status === 201 || createRes.status === 200) && createBody.ok === true
    && typeof createBody.partition === 'string',
  `status=${createRes.status} group=${createBody.group} partition=${createBody.partition ?? '?'}`);

const addRes = await fetch(`${SRV}/api/groups/demo/members`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ user_id: memberSub }),
});
let addResBody = await addRes.json().catch(() => ({}));
check('G-2. member add (bearer scopes client_id) → live sockets joined',
  addRes.status === 200 && addResBody.ok === true && addResBody.connections_joined >= 1,
  `status=${addRes.status} joined=${addResBody.connections_joined ?? 0}`);

const delRes = await fetch(`${SRV}/api/groups/demo/members?user_id=${encodeURIComponent(memberSub)}`, {
  method: 'DELETE', headers: { Authorization: `Bearer ${idpToken}` },
});
// remove-then-re-add so the fan-out under test below keeps working
check('G-3. member remove → ok (evicts live sockets)', delRes.status === 200, `status=${delRes.status}`);
const reAddRes = await fetch(`${SRV}/api/groups/demo/members`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ user_id: memberSub }),
});
check('G-4. member re-add → ok', reAddRes.status === 200, `status=${reAddRes.status}`);

const noSuchGroup = await fetch(`${SRV}/api/groups/no-such-group/members`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ user_id: memberSub }),
});
check('GATE-2b. member add on unknown group → 404', noSuchGroup.status === 404, `status=${noSuchGroup.status}`);

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

// ── partition isolation (plan R4) ────────────────────────────────────────────
if (MODE === 'mock') {
  // cross-partition publish: a DIFFERENT client_id's token must reach
  // NOTHING on our connection even for an identical group name.
  received = null;
  const otherToken = await mintMock({ sub: '42', scope: PUBLISHER_SCOPE, client_id: 'other-service' });
  const crossRes = await fetch(`${SRV}/api/publish`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${otherToken}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ group: 'demo', data: { type: 'chat_receive', message_id: 'spk-2', text: 'cross-partition must not land' } }),
  });
  const crossBody = await crossRes.json().catch(() => ({}));
  check('PART-1. cross-partition publish → 200 + reply is opaque',
    crossRes.status === 200 && crossBody.ok === true && crossBody.partition !== undefined,
    `status=${crossRes.status} partition=${crossBody.partition ?? '?'}`);
  await sleep(800);
  check('PART-2. no leak: publication stayed inside its partition', received === null,
    received !== null ? `received=${JSON.stringify(received)}` : 'nothing received (correct)');

  // positive control: same-partition publish still arrives
  const ownRes = await fetch(`${SRV}/api/publish`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${idpToken}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ group: 'demo', data: { type: 'chat_receive', message_id: 'spk-3', text: 'same partition' } }),
  });
  const ownBody = await ownRes.json().catch(() => ({}));
  check('PART-3. same-partition publish → 200 ok', ownRes.status === 200 && ownBody.ok === true, `status=${ownRes.status}`);
  await sleep(800);
  check('PART-4. same-partition publication received', received?.message_id === 'spk-3',
    `received=${JSON.stringify(received)}`);
}

await connection.stop();

// ── 6. server gates (no IdP round-trips required) ────────────────────────────
const anon = await fetch(`${SRV}/api/publish`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ group: 'demo', data: { type: 'chat_receive', text: 'x' } }),
});
check('GATE-1a. unauthenticated publish → 401', anon.status === 401);

const noAuthNegotiate = await fetch(`${SRV}/realtime/hub/negotiate?negotiateVersion=1`, { method: 'POST' });
check('GATE-1b. negotiate with no token → 401', noAuthNegotiate.status === 401, `status=${noAuthNegotiate.status}`);

// WS gate: an unauthenticated/garbage WS upgrade must be rejected by the
// fence (this is what proves WS-2 isn't a false pass).
async function wsUpgradeShouldFail(url) {
  return new Promise((resolve) => {
    let done = false;
    const finish = (ok) => {
      if (done) return;
      done = true;
      try { ws.close(); } catch { /* already closing */ }
      resolve(ok);
    };
    const ws = new globalThis.WebSocket(url);
    ws.onerror = () => finish(true);   // handshake refused → error
    ws.onclose = () => finish(true);   // closed without opening → rejected
    ws.onopen = () => finish(false);   // CONNECTED — the gate failed
    setTimeout(() => finish(true), 3000); // timeout treated as rejection
  });
}
check('GATE-1c. WS upgrade without connection token → rejected', await wsUpgradeShouldFail(`${SRV}/realtime/hub`));
check('GATE-1d. WS upgrade with garbage connection token → rejected',
  await wsUpgradeShouldFail(`${SRV}/realtime/hub?access_token=garbage-JWT`));

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
