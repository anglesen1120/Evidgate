// 04.2.1-T1.2: the single token entry point every later command uses (04.2.1 §5
// `auth/refresh.ts`). The dev path is unchanged (AC8: no exchange, no cache, no
// network). An OIDC / actions-id-token / ingest source reuses the cached `ci_producer`
// token while it is fresh (`exp - now > 60 s`), otherwise exchanges at
// `POST /v1/ci/token` and rewrites the cache; a 401/403 purges the entry so the next
// run re-authenticates cleanly (04.2.1 §6).
import {
  resolveTokenSource,
  tokenSourceNames,
  type ResolvedConfig,
  type TokenSource,
} from '../config.js';
import { CliError } from '../errors.js';
import { registerSecret } from '../redact.js';

import { resolveDevToken } from './devToken.js';
import {
  ExchangeAuthError,
  exchangeToken,
  type ExchangeDeps,
  type ExchangedToken,
} from './exchange.js';
import { deleteCachedToken, isFresh, readCachedToken, writeCachedToken } from './tokenCache.js';

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
    // M1 dev path (AC8): no exchange, no cache, no network. The token is registered so
    // it can never reach a writer even if a later error message embeds it (SEC6).
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
    let exchanged: ExchangedToken;
    try {
      exchanged = await exchangeToken(config, deps);
    } catch (error) {
      if (error instanceof ExchangeAuthError) {
        // A 401/403 means the cached token is dead: purge it so the next run re-auths.
        await deleteCachedToken(config);
      }
      throw error;
    }
    registerSecret(exchanged.token);
    await writeCachedToken(config, { token: exchanged.token, expiresAt: exchanged.expiresAt });
    return { token: exchanged.token, source, expiresAt: exchanged.expiresAt };
  }
  throw new CliError(`missing configuration: ${tokenSourceNames}`, { code: 'missing_config' });
}
