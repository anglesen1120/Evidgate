// 04.2.1-T2.2: the single token entry point every later command uses (04.2.1 §5
// `auth/refresh.ts`). At M1 only the dev path is implemented; an OIDC or ingest source
// is a recorded hand-over to 04.2.1-T1, which replaces the `exchange_unavailable`
// branch with the `POST /v1/ci/token` exchange. Never touches the network or the file
// system (AC8).
import {
  resolveTokenSource,
  tokenSourceNames,
  type ResolvedConfig,
  type TokenSource,
} from '../config.js';
import { CliError } from '../errors.js';
import { registerSecret } from '../redact.js';

import { resolveDevToken } from './devToken.js';

export interface TokenResult {
  readonly token: string;
  readonly source: TokenSource;
}

// `async` is kept (with no `await` yet) so 04.2.1-T1's exchange drops into this
// function without changing the signature every later command depends on.
// eslint-disable-next-line @typescript-eslint/require-await
export async function getToken(config: ResolvedConfig): Promise<TokenResult> {
  const source = resolveTokenSource(config);
  if (source === 'dev') {
    // M1 dev path (AC8): no exchange, no cache, no network. The token is registered so
    // it can never reach a writer even if a later error message embeds it (SEC6).
    const token = resolveDevToken(config);
    registerSecret(token);
    return { token, source: 'dev' };
  }
  if (source !== undefined) {
    throw new CliError('token exchange at POST /v1/ci/token ships in 04.2.1-T1', {
      code: 'exchange_unavailable',
    });
  }
  throw new CliError(`missing configuration: ${tokenSourceNames}`, { code: 'missing_config' });
}
