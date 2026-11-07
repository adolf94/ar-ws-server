/**
 * Refreshes the e2e token cache (.idp-token-cache.json) WITHOUT running the
 * full e2e. Server/WS-only test runs then hit zero IdP round-trips.
 *
 *   node mint-token.mjs          — real mode (IDP_CLIENT_ID/SECRET env,
 *                                  AUTH_SERVICE_SCOPES optional)
 *   IDP_MODE=mock node mint…     — mock mode (no creds)
 */
import { writeFileSync } from 'node:fs';

const PUBLISHER_SCOPE = 'api://ws-server/app:publisher';
const CACHE_FILE = new URL('./.idp-token-cache.json', import.meta.url);
const decodeClaims = (token) => JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString());

async function mint() {
  if ((process.env.IDP_MODE ?? 'real') === 'mock') {
    const res = await fetch('http://localhost:9001/token', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sub: '42', scope: PUBLISHER_SCOPE, client_id: 'spa-demo-client' }),
    });
    return (await res.json()).access_token;
  }
  if (!process.env.IDP_CLIENT_ID) {
    throw new Error('real mode needs IDP_CLIENT_ID + IDP_CLIENT_SECRET (sourced from ../centrifugo/.env by run-spike.sh)');
  }
  const authority = (process.env.IDP_AUTHORITY ?? 'https://auth.adolfrey.com/api/').replace(/\/?$/, '');
  const disc = await (await fetch(`${authority}/.well-known/openid-configuration`)).json();
  const body = new URLSearchParams({
    grant_type: 'client_credentials',
    client_id: process.env.IDP_CLIENT_ID,
    client_secret: process.env.IDP_CLIENT_SECRET ?? '',
  });
  if (process.env.AUTH_SERVICE_SCOPES) body.set('scope', process.env.AUTH_SERVICE_SCOPES);
  const res = await fetch(disc.token_endpoint, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body });
  if (!res.ok) throw new Error(`token endpoint responded ${res.status}: ${await res.text()}`);
  return (await res.json()).access_token;
}

const token = await mint();
const claims = decodeClaims(token);
writeFileSync(CACHE_FILE, JSON.stringify({
  access_token: token, // e2e.mjs sends this directly — never printed
  claims: { sub: claims.sub, scope: claims.scope, client_id: claims.client_id, iss: claims.iss, exp: claims.exp },
  minted_at: new Date().toISOString(),
}, null, 2));
console.log(`token cached — sub=${claims.sub} scope="${claims.scope}" exp=${claims.exp} (${new Date(claims.exp * 1000).toISOString()})`);
