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
// cheap consistency gate: the live OpenAPI doc matches the frozen snapshot
const CONTRACT_FILE = new URL('../../docs/openapi-v1.json', import.meta.url).pathname;
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

// ── send to a specific user (Phase 2 unicast, owner spec) ───────────────────
// Own-token send: lands via user_channel|{sub}, from = caller's client_id.
received = null;
const sendRes = await fetch(`${SRV}/api/users/${encodeURIComponent(memberSub ?? '')}/send`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ data: { type: 'notify_receive', message_id: 'snd-1', text: 'direct to user' } }),
});
const sendBody = await sendRes.json().catch(() => ({}));
check('SEND-1. POST /api/users/{sub}/send → 200 ok', sendRes.status === 200 && sendBody.ok === true, `status=${sendRes.status}`);
await sleep(800);
check('SEND-2. unicast received (from = caller client_id, stamped)',
  received?.message_id === 'snd-1' && typeof received?.sent_at === 'string',
  `received=${JSON.stringify(received)}`);

if (MODE === 'mock') {
  // THE point of user_channel: sender from a DIFFERENT service partition
  // still reaches the user — sub is the only address.
  received = null;
  const otherToken = await mintMock({ sub: '42', scope: PUBLISHER_SCOPE, client_id: 'other-service' });
  const crossSendRes = await fetch(`${SRV}/api/users/${encodeURIComponent(memberSub ?? '')}/send`, {
    method: 'POST', headers: { Authorization: `Bearer ${otherToken}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ data: { type: 'notify_receive', message_id: 'snd-2', text: 'cross-service unicast' } }),
  });
  const crossSendBody = await crossSendRes.json().catch(() => ({}));
  check('SEND-3. cross-service unicast → 200 ok', crossSendRes.status === 200 && crossSendBody.ok === true, `status=${crossSendRes.status}`);
  await sleep(800);
  check('SEND-4. cross-service unicast received (from = sender client_id)',
    received?.message_id === 'snd-2' && received?.from === 'other-service',
    `received=${JSON.stringify(received)}`);
}

// ── send to a specific CONNECTION (scoped per owner, 2026-11-07) ────────────
// One live socket: ours (connected above). Own-socket send must deliver.
received = null;
const connSendRes = await fetch(`${SRV}/api/connections/${encodeURIComponent(connection.connectionId)}/send`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ data: { type: 'notify_receive', message_id: 'snd-5', text: 'direct to this socket' } }),
});
const connSendBody = await connSendRes.json().catch(() => ({}));
check('SEND-5. own-connection send → 200 ok', connSendRes.status === 200 && connSendBody.ok === true,
  `status=${connSendRes.status} conn=${connection.connectionId?.slice(0, 8)}…`);
await sleep(800);
check('SEND-6. per-socket delivery received', received?.message_id === 'snd-5',
  `received=${JSON.stringify(received)}`);

const ghostSend = await fetch(`${SRV}/api/connections/${encodeURIComponent('no-such-connection')}/send`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ data: { type: 'notify_receive', text: 'x' } }),
});
check('GATE-4. send to unknown connection → 404', ghostSend.status === 404, `status=${ghostSend.status}`);

if (MODE === 'mock') {
  // foreign-partition socket: mint a second connection under a DIFFERENT
  // client_id, then send at it with OUR token — must be refused (caller
  // scoped to its own client_id).
  received = null;
  const foreignToken = await mintMock({ sub: '43', scope: PUBLISHER_SCOPE, client_id: 'other-service' });
  const foreignConn = new HubConnectionBuilder()
    .withUrl(`${SRV}/realtime/hub`, { accessTokenFactory: () => foreignToken })
    .build();
  await foreignConn.start();
  const foreignSend = await fetch(`${SRV}/api/connections/${encodeURIComponent(foreignConn.connectionId)}/send`, {
    method: 'POST', headers: authHdrs,   // our spa-demo-client token
    body: JSON.stringify({ data: { type: 'notify_receive', message_id: 'snd-6', text: 'must be refused' } }),
  });
  check('GATE-4b. send to another client_id\'s connection → 403 (caller scoped)',
    foreignSend.status === 403, `status=${foreignSend.status}`);
  await foreignConn.stop();
}

const sendNoData = await fetch(`${SRV}/api/users/${encodeURIComponent(memberSub ?? '')}/send`, {
  method: 'POST', headers: { Authorization: `Bearer ${idpToken}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({}),
});
check('GATE-3. send with no data → 400', sendNoData.status === 400, `status=${sendNoData.status}`);

const sendAnon = await fetch(`${SRV}/api/users/${encodeURIComponent(memberSub ?? '')}/send`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ data: { type: 'notify_receive', text: 'x' } }),
});
check('GATE-3b. unauthenticated send → 401', sendAnon.status === 401, `status=${sendAnon.status}`);

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

// ── broadcast (plan §5.3): one envelope into several groups, all caller-
// partition-scoped; unknown groups are skipped + reported. ──────────────────
received = null;
const bcCreateRes = await fetch(`${SRV}/api/groups`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ name: 'demo2' }),
});
check('BC-0. create demo2 → ok', bcCreateRes.status === 201 || bcCreateRes.status === 200, `status=${bcCreateRes.status}`);

const bcRes = await fetch(`${SRV}/api/broadcast`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ groups: ['demo', 'demo2', 'ghost-group'], data: { type: 'chat_receive', message_id: 'bc-1', text: 'broadcast' } }),
});
const bcBody = await bcRes.json().catch(() => ({}));
check('BC-1. broadcast → 200, delivered groups reported',
  bcRes.status === 200 && Array.isArray(bcBody.groups_delivered) && bcBody.groups_delivered.sort().join(',') === 'demo,demo2',
  `status=${bcRes.status} delivered=${JSON.stringify(bcBody.groups_delivered)}`);
check('BC-2. unknown groups reported (own partition only)',
  Array.isArray(bcBody.unknown_groups) && bcBody.unknown_groups.join(',') === 'ghost-group',
  `unknown=${JSON.stringify(bcBody.unknown_groups)}`);
await sleep(800);
check('BC-3. broadcast delivered to the connection', received?.message_id === 'bc-1',
  `received=${JSON.stringify(received)}`);

const bcNoGroups = await fetch(`${SRV}/api/broadcast`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ groups: [], data: { type: 'chat_receive', text: 'x' } }),
});
check('GATE-5. broadcast with empty groups → 400', bcNoGroups.status === 400, `status=${bcNoGroups.status}`);

// ── GET /api/groups: listing + persisted member counts + live identities ────
const listRes = await fetch(`${SRV}/api/groups`, { headers: { Authorization: `Bearer ${idpToken}` } });
const listBody = await listRes.json().catch(() => ({}));
const demoRow = listBody?.groups?.find?.((g) => g.group === 'demo');
check('G-5. GET /api/groups → partition rows',
  listRes.status === 200 && typeof listBody.partition === 'string' &&
    demoRow && demoRow.members >= 1 && demoRow.online >= 1,
  `status=${listRes.status} partition=${listBody.partition ?? '?'} demo=${JSON.stringify(demoRow)}`);
check('G-6. listing shows only the caller\'s partition rows',
  listBody.groups.some((g) => g.group === 'demo2') && !listBody.groups.some((g) => g.group === 'ghost-group'),
  `groups=${JSON.stringify(listBody.groups?.map?.((g) => g.group))}`);

if (MODE === 'mock') {
  // privacy: another client's token sees NOTHING of our partition's groups
  const foreignListRes = await fetch(`${SRV}/api/groups`, {
    headers: { Authorization: `Bearer ${await mintMock({ sub: '43', scope: PUBLISHER_SCOPE, client_id: 'other-service' })}` },
  });
  const foreignListBody = await foreignListRes.json().catch(() => ({}));
  check('G-7. listing is partition-private (other client sees none of ours)',
    foreignListRes.status === 200 && Array.isArray(foreignListBody.groups) &&
      !foreignListBody.groups.some((g) => ['demo', 'demo2'].includes(g.group)),
    `other partition groups=${JSON.stringify(foreignListBody.groups?.map?.((g) => g.group))}`);
}

// ── presence + disconnect (Phase 2 tail) ────────────────────────────────────
const presRes = await fetch(`${SRV}/api/groups/demo/presence`, { headers: { Authorization: `Bearer ${idpToken}` } });
const presBody = await presRes.json().catch(() => ({}));
const ownRow = presBody?.presence?.find?.((p) => p.user_id === memberSub);
check('P-1. presence: our identity online (stamps + closed world)',
  presRes.status === 200 && presBody.group_partition === (listBody?.partition ?? null)
    && ownRow && ownRow.online === true && ownRow.connections >= 1
    && ownRow.client_id === userClaims.client_id,
  `status=${presRes.status} row=${JSON.stringify(ownRow)}`);

if (MODE === 'mock') {
  // foreign disconnect refused: our token cannot close another client's socket
  const foreignDiscToken = await mintMock({ sub: '99', scope: PUBLISHER_SCOPE, client_id: 'other-service' });
  const foreignDiscConn = new HubConnectionBuilder()
    .withUrl(`${SRV}/realtime/hub`, { accessTokenFactory: () => foreignDiscToken })
    .build();
  await foreignDiscConn.start();
  const foreignDiscRes = await fetch(`${SRV}/api/connections/${encodeURIComponent(foreignDiscConn.connectionId)}/disconnect`, {
    method: 'POST', headers: authHdrs,   // our spa-demo-client token
  });
  check('D-1. disconnect of another client_id\'s connection → 403',
    foreignDiscRes.status === 403, `status=${foreignDiscRes.status}`);
  await foreignDiscConn.stop();
}

// own disconnect: allowed, evicts the socket immediately
const discRes = await fetch(`${SRV}/api/connections/${encodeURIComponent(connection.connectionId)}/disconnect`, {
  method: 'POST', headers: authHdrs,
});
const discBody = await discRes.json().catch(() => ({}));
check('D-2. disconnect own connection → 200 ok', discRes.status === 200 && discBody.ok === true,
  `status=${discRes.status}`);

await sleep(800);
check('D-3. connection actually closed', connection.state === 'Disconnected',
  `state=${connection.state}`);

const presAfter = await fetch(`${SRV}/api/groups/demo/presence`, { headers: { Authorization: `Bearer ${idpToken}` } });
const presAfterBody = await presAfter.json().catch(() => ({}));
const ownRowAfter = presAfterBody?.presence?.find?.((p) => p.user_id === memberSub);
check('D-4. presence after disconnect: identity persists, online=false',
  ownRowAfter && ownRowAfter.online === false && ownRowAfter.connections === 0,
  `row=${JSON.stringify(ownRowAfter)}`);

const discGhost = await fetch(`${SRV}/api/connections/${encodeURIComponent('no-such-conn')}/disconnect`, {
  method: 'POST', headers: authHdrs,
});
check('D-5. disconnect of unknown connection → 404', discGhost.status === 404, `status=${discGhost.status}`);

await connection.stop().catch?.(() => {});
// ── 6. server gates (no IdP round-trips required) ───────────────────────────
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

// ── contract freeze: live OpenAPI matches the frozen snapshot ───────────────
const contractSnap = JSON.parse(readFileSync(CONTRACT_FILE, 'utf8'));
const liveDoc = await (await fetch(`${SRV}/openapi/v1.json`)).json();
const snapPaths = Object.keys(contractSnap.paths ?? {}).sort().join(',');
const livePaths = Object.keys(liveDoc.paths ?? {}).sort().join(',');
check('OC-1. live OpenAPI paths == frozen docs/openapi-v1.json',
  snapPaths === livePaths && snapPaths.includes('/api/publish')
    && snapPaths.includes('/api/users/{sub}/send') && snapPaths.includes('/healthz'),
  snapPaths === livePaths ? `${livePaths.split(',').length} paths` : `frozen=[${snapPaths}] live=[${livePaths}]`);

if (String(userClaims.scope ?? '').length === 0) {
  console.log('⚠ cached/env token carries NO scopes — publish scope checks may FAIL; refresh via dev/e2e/mint-token.mjs');
}
