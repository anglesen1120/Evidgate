// The single token entry point every command uses. An OIDC / actions-id-token / ingest / dev source reuses the cached `ci_producer`
// token while it is fresh (`exp - now > 60 s`), otherwise exchanges at `POST /v1/ci/token`
// and rewrites the cache; a 401/403 purges the entry so the next run re-authenticates.
import {
  resolveTokenSource,
  tokenSourceNames,
  type ResolvedConfig,
  type TokenSource,
} from '../config.js';
import { CliError } from '../errors.js';
import { registerSecret } from '../redact.js';

import { resolveDevToken } from './devToken.js';
import { exchangeAndPurge, type ExchangeDeps } from './exchange.js';
import { isFresh, readCachedToken, writeCachedToken } from './tokenCache.js';

export interface TokenResult {
  readonly token: string;
  readonly source: TokenSource;
  readonly expiresAt?: string;
}

/** `getToken`'s injectable seams: the exchange deps plus a clock for the freshness check. */
export type GetTokenDeps = ExchangeDeps & { readonly now?: () => number };

export async function getToken(
  config: ResolvedConfig,
  deps: GetTokenDeps = {},
): Promise<TokenResult> {
  const source = resolveTokenSource(config);
  if (source === undefined) {
    throw new CliError(`missing configuration: ${tokenSourceNames}`, { code: 'missing_config' });
  }
  // The Api accepts only exchanged `ci_producer` tokens, so the dev token is a seeded ingest
  // token: after the env/loopback guards it takes the ingest exchange path below.
  const credentialConfig =
    source === 'dev' ? { ...config, ingestToken: resolveDevToken(config) } : config;
  const cached = await readCachedToken(credentialConfig);
  const nowMs = deps.now === undefined ? Date.now() : deps.now();
  if (cached !== undefined && isFresh(cached, nowMs)) {
    registerSecret(cached.token);
    return { token: cached.token, source, expiresAt: cached.expiresAt };
  }
  const exchanged = await exchangeAndPurge(credentialConfig, deps);
  registerSecret(exchanged.token);
  await writeCachedToken(credentialConfig, {
    token: exchanged.token,
    expiresAt: exchanged.expiresAt,
  });
  return { token: exchanged.token, source, expiresAt: exchanged.expiresAt };
}
