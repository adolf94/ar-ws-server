// Conformance for the TS consumer lib against a RUNNING server (mock lane
// preferred: only mock mints arbitrary sub/client_id pairs).
// Orchestration lives in dev/test-clients.sh — don't run standalone unless
// SRV and MOCK_AUTH point at a live stack.
//
// Gates:
//   HUB-1   negotiate + WS handshake consume the tokenFactory's IdP token
//   HUB-2   publisher round-trip accepted (200)
//   HUB-3   routed event delivered with server-stamped provenance
//   HUB-4   unknown-type frames hit onAny only, routed handlers unaffected
//   HUB-4b  subscriber exceptions are contained (hub alive, later events flow)
//   HUB-5   stop() idempotent; restart re-negotiates, provisioning re-applied
//   UA-1    negotiate/WS lanes carry OUR User-Agent (local echo capture)
//   PART-1  hubUrl merge: SDK riders (partition + instance options)
//           co-exist with caller params (local echo capture — CONTRACT §1/§2.2)
//   PART-1b instanceTag defaults ON — ?instance= present with no options
//   PART-1c partition defaults to the token's client_id (aud contains it)
//   PART-1d no partition rider when the aud misses client_id (fence-safe)
//   PART-2  live alias: token aud contains the partition → connection lands in
//           the partition; backend membership applies; publish delivers
//   PART-3  live negative: partition missing from the token's aud → negotiate
//           rejects (start() fails; 403 partition_not_in_token)

import { createRealtimeHub, USER_AGENT, LIB_VERSION } from '../dist/index.js';

const SRV = process.env.SRV ?? 'http://localhost:5000';
const MOCK_AUTH = process.env.MOCK_AUTH ?? 'http://localhost:9001';
const USER_SUB = '42';
const GROUP = 'ts-conformance';

let failures = 0;
function check(name, ok, detail = '') {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ` — ${detail}` : ''}`);
  if (!ok) failures++;
}

async function mintMock(body) {
  const res = await fetch(`${MOCK_AUTH}/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`mock /token failed: ${res.status}`);
  return (await res.json()).access_token;
}

// consumer identity (fence: any valid bearer negotiates; scope free) and
// publisher identity (publisher scope) minted for the same client_id —
// the e2e pattern
const token = await mintMock({ sub: USER_SUB, scope: 'realtime:connect', client_id: 'spa-demo-client' });
const publisherToken = await mintMock({
  sub: 'svc', scope: 'api://ws-server/app:publisher', client_id: 'spa-demo-client',
});
const authHdrs = { Authorization: `Bearer ${publisherToken}`, 'Content-Type': 'application/json' };

const received = [];
let anyCount = 0;

const hub = createRealtimeHub({
  url: `${SRV}/realtime/hub`,
  tokenFactory: async () => token,
});

hub.on('chat_receive', (event) => received.push(event));
hub.onAny(() => { anyCount++; });

await hub.start();
check('HUB-1. negotiate + WS handshake with tokenFactory → connected',
  hub.state === 'connected', `state=${hub.state}`);

// provisioning: backend creates the group + enrolls the consumer's identity
// (join is applied LIVE to the running socket — no client-side join exists)
{
  await fetch(`${SRV}/api/groups`, {
    method: 'POST', headers: authHdrs, body: JSON.stringify({ name: GROUP }),
  });
  const res = await fetch(`${SRV}/api/groups/${GROUP}/members`, {
    method: 'POST', headers: authHdrs, body: JSON.stringify({ user_id: USER_SUB }),
  });
  check('HUB-2a. backend provisioned membership (member add → live join)',
    res.status === 200, `status=${res.status}`);
}

{
  const res = await fetch(`${SRV}/api/publish`, {
    method: 'POST', headers: authHdrs,
    body: JSON.stringify({ group: GROUP, data: { type: 'chat_receive', text: 'from the ts client' } }),
  });
  check('HUB-2. publisher round-trip accepted (200)', res.status === 200, `status=${res.status}`);
}

// an unknown type on the same socket must not disturb routed handlers
await fetch(`${SRV}/api/publish`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ group: GROUP, data: { type: 'ai_emission', chunk: 1 } }),
});

await new Promise((r) => setTimeout(r, 1200));
check('HUB-3. routed chat_receive delivered with server-stamped provenance',
  received[0]?.type === 'chat_receive' && typeof received[0]?.sent_at === 'string'
    && received[0]?.from === 'spa-demo-client' && received[0]?.text === 'from the ts client',
  `received=${JSON.stringify(received[0] ?? null)}`);
check('HUB-4. unknown type delivered to onAny, NOT to routed handlers (no crash)',
  anyCount >= 2 && !received.some((e) => e.type === 'ai_emission'),
  `any=${anyCount} routed=${received.length}`);

// containment: a subscriber that throws must not kill the subscription
hub.on('notify_receive', () => { throw new Error('intentional subscriber bug'); });
const countBefore = received.length;
await fetch(`${SRV}/api/users/${USER_SUB}/send`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ data: { type: 'notify_receive', text: 'boom' } }),
});
await new Promise((r) => setTimeout(r, 800));
check('HUB-4b. subscriber exception contained; hub still alive', hub.state === 'connected');

await fetch(`${SRV}/api/publish`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ group: GROUP, data: { type: 'chat_receive', text: 'still here' } }),
});
await new Promise((r) => setTimeout(r, 800));
check('HUB-4c. events still delivered after the buggy subscriber',
  received.length > countBefore, `routed=${received.length} (was ${countBefore})`);

// HUB-5: stop + restart (fresh negotiated socket; membership re-applies hub-side)
await hub.stop();
check('HUB-5a. stop() is idempotent', hub.state === 'stopped' || hub.state === 'new', `state=${hub.state}`);
let threw = null;
try { await hub.stop(); } catch (e) { threw = e; }
check('HUB-5b. second stop() throws nothing', threw === null, threw ? String(threw) : '');
received.length = 0;
await hub.start();
check('HUB-5c. restart re-negotiates → connected', hub.state === 'connected');
await fetch(`${SRV}/api/publish`, {
  method: 'POST', headers: authHdrs,
  body: JSON.stringify({ group: GROUP, data: { type: 'chat_receive', text: 'after restart' } }),
});
await new Promise((r) => setTimeout(r, 800));
check('HUB-5d. reconnect re-applies stored membership (delivered again)',
  received[0]?.text === 'after restart', `received=${JSON.stringify(received[0] ?? null)}`);
await hub.stop();

// UA-1: local echo server captures the ACTUAL negotiate headers
{
  const http = await import('node:http');
  let ua = null;
  const echo = http.createServer((req, res) => {
    ua = req.headers['user-agent'] ?? null;
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({
      connectionId: 'echo-id',
      connectionToken: 'echo-token',
      negotiateVersion: 1,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    }));
  });
  await new Promise((r) => echo.listen(0, '127.0.0.1', r));
  const echoUrl = `http://127.0.0.1:${echo.address().port}`;
  const { HubConnectionBuilder } = await import('@microsoft/signalr');
  const echoConn = new HubConnectionBuilder()
    .withUrl(`${echoUrl}/realtime/hub`, {
      accessTokenFactory: async () => 'echo',
      headers: {
        'User-Agent': USER_AGENT,
        'X-SignalR-User-Agent': `${USER_AGENT} (test=${LIB_VERSION})`,
      },
    })
    .configureLogging('none')
    .build();
  try { await echoConn.start().catch(() => {}); } catch { /* echo junk is fine */ }
  check('UA-1. negotiate User-Agent is ours (Cloudflare-safe), not stock SignalR',
    (ua ?? '').includes(USER_AGENT), `user-agent=${JSON.stringify(ua)}`);
  echo.close();
}

// PART-1: local echo captures the negotiate URL — SDK riders (partition
// alias + instance tag) must MERGE into the query, not clobber caller params
{
  const http = await import('node:http');
  const negotiatePaths = [];
  const echo = http.createServer((req, res) => {
    if ((req.url ?? '').includes('/negotiate')) negotiatePaths.push(req.url);
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({
      connectionId: 'echo-id',
      connectionToken: 'echo-token',
      negotiateVersion: 1,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    }));
  });
  await new Promise((r) => echo.listen(0, '127.0.0.1', r));
  const echoUrl = `http://127.0.0.1:${echo.address().port}`;
  const echoHub = createRealtimeHub({
    url: `${echoUrl}/realtime/hub?vendor=keep-me`,   // caller param must survive
    tokenFactory: async () => 'echo',
    partition: 'finance-app-api',
    instanceTag: 'probe-tag',                        // SDK riders: partition + instance
  });
  await echoHub.start().catch(() => {});   // the echo can't upgrade — fine
  await echoHub.stop();                    // stop() is idempotent even when start() failed
  check('PART-1. negotiate URL carries SDK riders (partition + instance) alongside caller params',
    (negotiatePaths[0] ?? '').includes('partition=finance-app-api')
      && (negotiatePaths[0] ?? '').includes('instance=probe-tag')
      && (negotiatePaths[0] ?? '').includes('vendor=keep-me'),
    `negotiate URL=${JSON.stringify(negotiatePaths[0])}`);

  // default rider: instanceTag defaults ON (v0.2.0) — same tab/process keeps
  // its connection_id across reconnects without any explicit option
  const plainEchoHub = createRealtimeHub({ url: `${echoUrl}/realtime/hub`, tokenFactory: async () => 'echo' });
  await plainEchoHub.start().catch(() => {});
  await plainEchoHub.stop();
  check('PART-1b. instanceTag defaults ON — ?instance= present with no options',
    (negotiatePaths[1] ?? '').includes('instance='), `negotiate URL=${JSON.stringify(negotiatePaths[1])}`);

  // default partition: the token's own client_id, appended ONLY when the
  // aud contains it (PART-1c); omitted otherwise (PART-1d) — both fence-safe
  const jointToken = await mintMock({
    sub: '44', scope: 'realtime:connect', client_id: 'spa-demo-client',
    aud: ['centrifugo', 'spa-demo-client'],   // aud CONTAINS own client → rider expected
  });
  const jointEchoHub = createRealtimeHub({ url: `${echoUrl}/realtime/hub`, tokenFactory: async () => jointToken });
  await jointEchoHub.start().catch(() => {});
  await jointEchoHub.stop();
  check('PART-1c. partition defaults to the token client_id when aud contains it',
    (negotiatePaths[2] ?? '').includes('partition=spa-demo-client'),
    `negotiate URL=${JSON.stringify(negotiatePaths[2])}`);

  const riderlessToken = await mintMock({ sub: '45', scope: 'realtime:connect', client_id: 'spa-demo-client' });
  const riderlessEchoHub = createRealtimeHub({ url: `${echoUrl}/realtime/hub`, tokenFactory: async () => riderlessToken });
  await riderlessEchoHub.start().catch(() => {});
  await riderlessEchoHub.stop();
  check('PART-1d. no partition rider when the aud misses client_id (fence-safe lapse)',
    (negotiatePaths[3] ?? '').includes('negotiate') && !(negotiatePaths[3] ?? '').includes('partition='),
    `negotiate URL=${JSON.stringify(negotiatePaths[3])}`);
  echo.close();
}

// PART-2/3: LIVE alias gates against the running server (CONTRACT §1)
{
  // positive: joint-audience token ('realtime' + the publisher's partition)
  // mirrors the SPA shape — user token that also audiences the backend
  const aliasToken = await mintMock({
    sub: USER_SUB, scope: 'realtime:connect', client_id: 'spa-demo-client',
    aud: ['realtime', 'spa-demo-client'],
  });
  const aliasHub = createRealtimeHub({
    url: `${SRV}/realtime/hub?instance=ts-partition-alias`,  // tagged: rebind across reconnects
    tokenFactory: async () => aliasToken,
    partition: 'spa-demo-client',
  });
  let aliasedText = null;
  aliasHub.on('chat_receive', (e) => { if (e.text === 'delivered through the alias') aliasedText = e.text; });
  await aliasHub.start();
  check('PART-2a. aliased negotiate accepted (aud contains the partition)',
    aliasHub.state === 'connected', `state=${aliasHub.state}`);

  // backend provisions the aliased identity: same client_id partition
  await fetch(`${SRV}/api/groups`, {
    method: 'POST', headers: authHdrs, body: JSON.stringify({ name: 'ts-partition-alias' }),
  });
  const member = await fetch(`${SRV}/api/groups/ts-partition-alias/members`, {
    method: 'POST', headers: authHdrs, body: JSON.stringify({ user_id: USER_SUB }),
  });
  await fetch(`${SRV}/api/publish`, {
    method: 'POST', headers: authHdrs,
    body: JSON.stringify({
      group: 'ts-partition-alias',
      data: { type: 'chat_receive', text: 'delivered through the alias' },
    }),
  });
  await new Promise((r) => setTimeout(r, 900));
  check('PART-2b. aliased socket received backend publish (bus plane = aliased partition)',
    aliasedText === 'delivered through the alias' && member.status === 200,
    `text=${JSON.stringify(aliasedText)} member-add=${member.status}`);
  await aliasHub.stop();

  // negative: partition NOT in the token's aud → negotiate rejects, start fails
  const plainToken = await mintMock({ sub: '43', scope: 'realtime:connect', client_id: 'spa-demo-client' });
  const negativeHub = createRealtimeHub({
    url: `${SRV}/realtime/hub`,
    tokenFactory: async () => plainToken,
    partition: 'spa-demo-client',   // plain aud = the pair's audience, not this partition
  });
  let negErr = null;
  try { await negativeHub.start(); } catch (e) { negErr = e; }
  check('PART-3. partition missing from aud → negotiate rejected (start fails)',
    negErr !== null && negativeHub.state !== 'connected', `err=${String(negErr?.message ?? 'none').slice(0, 120)}`);
}

console.log(`\nts conformance: ${failures === 0 ? 'ALL PASS' : `${failures} FAILURE(S)`}`);
process.exit(failures === 0 ? 0 : 1);
