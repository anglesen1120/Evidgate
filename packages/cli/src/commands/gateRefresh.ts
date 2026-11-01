import { getToken, type GetTokenDeps } from '../auth/getToken.js';
import type { ResolvedConfig } from '../config.js';
import { CliError } from '../errors.js';

interface RefreshRequest {
  readonly config: ResolvedConfig;
  readonly deps: GetTokenDeps;
}

/**
 * Mints the token the replay needs, bounded by the poll's own deadline: a `--wait` may not be
 * extended by an exchange whose first byte never arrives. The abandoned exchange rejects into a
 * race that already settled, and its lower budget aborts the request it opened.
 */
export async function refreshedToken(
  request: RefreshRequest,
  deadlineAt: number | undefined,
  now: () => number,
): Promise<{ readonly token: string }> {
  const deps: GetTokenDeps =
    deadlineAt === undefined ? request.deps : { ...request.deps, deadlineAt };
  const exchange = getToken(request.config, deps);
  if (deadlineAt === undefined) {
    return exchange;
  }
  const remaining = deadlineAt - now();
  if (remaining <= 0) {
    exchange.catch(() => undefined);
    throw refreshOutlivedWait();
  }
  let timer: NodeJS.Timeout | undefined;
  // Executor form: `Promise.withResolvers` is ES2024 and this workspace targets ES2022.
  const expired = new Promise<never>((_resolve, reject) => {
    timer = setTimeout(() => {
      reject(refreshOutlivedWait());
    }, remaining);
  });
  try {
    return await Promise.race([exchange, expired]);
  } finally {
    clearTimeout(timer);
  }
}

/** The wait, not the original 401, ended a refresh that outlived its deadline. */
export function refreshOutlivedWait(): CliError {
  return new CliError('the token refresh outlived the --wait deadline', {
    code: 'request_failed',
  });
}
