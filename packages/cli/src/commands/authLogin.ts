// 04.2.1-T1.1: `evidgate auth login` — exchanges an OIDC JWT or ingest token for a
// short-lived `ci_producer` token and writes it only to the `0600` cache file (or to
// `$GITHUB_ENV` behind `::add-mask::` with `--github-env`). The token is never printed
// (T78, AC6). `--github-env` is a per-command flag, never a global option (04.2.1-T2
// hand-over item 2).
import { randomUUID } from 'node:crypto';
import { appendFile } from 'node:fs/promises';

import { exchangeAndPurge } from '../auth/exchange.js';
import { writeCachedToken } from '../auth/tokenCache.js';
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
    // `--github-env` must fail before the exchange and the cache write, so an unset
    // GITHUB_ENV never leaves a token on disk (D3).
    const exportToGithub = values['github-env'] === true;
    const githubEnvPath = exportToGithub ? env['GITHUB_ENV'] : undefined;
    if (exportToGithub && (githubEnvPath === undefined || githubEnvPath === '')) {
      throw new CliError('missing configuration: GITHUB_ENV', { code: 'missing_config' });
    }
    const exchanged = await exchangeAndPurge(config, deps);
    await writeCachedToken(config, { token: exchanged.token, expiresAt: exchanged.expiresAt });
    if (githubEnvPath !== undefined) {
      await appendGithubEnv(githubEnvPath, exchanged.token, maskSecret);
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
 * Appends `EVIDGATE_CI_TOKEN` to `$GITHUB_ENV` as a heredoc, after emitting the
 * `::add-mask::` line to the RAW stdout so the runner masks the value in later logs
 * (T78). A token with a line break is refused before anything is written.
 */
async function appendGithubEnv(
  path: string,
  token: string,
  maskSecret: (value: string) => void,
): Promise<void> {
  // defence in depth: readSuccess already rejects whitespace
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
