// The single token entry point every command uses. The dev path does no exchange, no cache
// and no network. An OIDC / actions-id-token / ingest source reuses the cached `ci_producer`
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
  if (source === 'dev') {
    // Dev path: no exchange, no cache, no network. The token is registered so it can never
    // reach a writer even if a later error message embeds it.
    const token = resolveDevToken(config);
    registerSecret(token);
    return { token, source: 'dev' };
  }
  if (source !== undefined) {
    const cached = await readCachedToken(config);
    const nowMs = deps.now === undefined ? Date.now() : deps.now();
    if (cached !== undefined && isFresh(cached, nowMs)) {
      registerSecret(cached.token);
      return { token: cached.token, source, expiresAt: cached.expiresAt };
    }
    const exchanged = await exchangeAndPurge(config, deps);
    registerSecret(exchanged.token);
    await writeCachedToken(config, { token: exchanged.token, expiresAt: exchanged.expiresAt });
    return { token: exchanged.token, source, expiresAt: exchanged.expiresAt };
  }
  throw new CliError(`missing configuration: ${tokenSourceNames}`, { code: 'missing_config' });
}
