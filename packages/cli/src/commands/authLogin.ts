// 04.2.1-T1.1: `evidgate auth login` — exchanges an OIDC JWT or ingest token for a
// short-lived `ci_producer` token and writes it only to the `0600` cache file (or to
// `$GITHUB_ENV` behind `::add-mask::` with `--github-env`). The token is never printed
// (T78, AC6). `--github-env` is a per-command flag, never a global option (04.2.1-T2
// hand-over item 2).
import { randomUUID } from 'node:crypto';
import { appendFile } from 'node:fs/promises';

import {
  ExchangeAuthError,
  exchangeToken,
  type ExchangeDeps,
  type ExchangedToken,
} from '../auth/exchange.js';
import { deleteCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import type { ResolvedConfig } from '../config.js';
import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';

import type { CommandDefinition } from './index.js';

/** The `$GITHUB_ENV` variable the token is exported as (04.2.1 §5). */
const githubEnvVariable = 'EVIDGATE_CI_TOKEN';

export const authLoginCommand: CommandDefinition = {
  name: 'auth login',
  summary: 'exchange a CI credential for a short-lived token',
  requireToken: true,
  options: { 'github-env': { type: 'boolean' } },
  run: async ({ config, io, env, values, deps, maskSecret }) => {
    const exchanged = await exchangeOrPurge(config, deps);
    await writeCachedToken(config, { token: exchanged.token, expiresAt: exchanged.expiresAt });
    if (values['github-env'] === true) {
      await exportToGithubEnv(env, exchanged.token, maskSecret);
    }
    if (config.json) {
      const payload = {
        ok: true,
        expires_at: exchanged.expiresAt,
        project_id: exchanged.projectId,
      };
      io.stdout(`${JSON.stringify(payload)}\n`);
    } else {
      io.stdout(`ok expires_at=${exchanged.expiresAt} project_id=${exchanged.projectId}\n`);
    }
    return ExitCode.Ok;
  },
};

/**
 * Exchanges the credential, purging the cache entry first when the server answers
 * 401/403 so the next run re-authenticates cleanly (04.2.1 §6).
 */
async function exchangeOrPurge(
  config: ResolvedConfig,
  deps: ExchangeDeps,
): Promise<ExchangedToken> {
  try {
    return await exchangeToken(config, deps);
  } catch (error) {
    if (error instanceof ExchangeAuthError) {
      await deleteCachedToken(config);
    }
    throw error;
  }
}

/**
 * Appends `EVIDGATE_CI_TOKEN` to `$GITHUB_ENV` as a heredoc, after emitting the
 * `::add-mask::` line to the RAW stdout so the runner masks the value in later logs
 * (T78). A missing `GITHUB_ENV` is `missing_config`; a token with a line break is
 * refused before anything is written.
 */
async function exportToGithubEnv(
  env: Readonly<Record<string, string | undefined>>,
  token: string,
  maskSecret: (value: string) => void,
): Promise<void> {
  const path = env['GITHUB_ENV'];
  if (path === undefined || path === '') {
    throw new CliError('missing configuration: GITHUB_ENV', { code: 'missing_config' });
  }
  if (/[\r\n]/.test(token)) {
    throw new CliError('the exchanged token contains a line break', { code: 'exchange_failed' });
  }
  maskSecret(token);
  const delimiter = `ghadelimiter_${randomUUID()}`;
  const block = `${githubEnvVariable}<<${delimiter}\n${token}\n${delimiter}\n`;
  // A failed append is not a token-cache operation, so it is left to `main`'s
  // catch-all (`internal_error`) rather than mislabelled `token_cache_error`.
  await appendFile(path, block, { encoding: 'utf8' });
}
