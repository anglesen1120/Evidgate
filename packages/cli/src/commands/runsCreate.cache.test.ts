// `evidgate runs create` and the cached CI token: a 401 purges it, a 403 keeps it, because a
// scope refusal says nothing about the token (contracts/cli.md §7).
import { describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';

import {
  capture,
  createArgv,
  invoke,
  jsonResponse,
  stubFetch,
  useRunsCreateSpec,
} from './runsCreate.testing.js';

describe('runs create', () => {
  const { ingestEnv } = useRunsCreateSpec();

  // A 401 from the project-scoped command purges the cached CI token, so the next
  // `getToken()` re-exchanges.
  it('unauthorized_purges_cache', async () => {
    const env = await ingestEnv();
    const config = resolveConfig({ env });
    const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
    await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });

    const stub = stubFetch(() =>
      jsonResponse(401, {
        error: {
          code: 'token_revoked',
          message: 'the token was revoked',
          request_id: 'req-1',
          retryable: false,
        },
      }),
    );
    const out = capture();

    await expect(invoke(createArgv(), env, out, stub.fetchImpl)).resolves.toBe(3);

    expect(out.stderr.join('')).toContain('evidgate: error token_revoked: ');
    // The cached token was reused (no exchange), so the create is the only call.
    expect(stub.calls).toHaveLength(1);
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  // contracts/cli.md §7: a 403 is a scope refusal, not a dead token, so the
  // cached token stays.
  it('forbidden_keeps_cache', async () => {
    const env = await ingestEnv();
    const config = resolveConfig({ env });
    const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
    await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });
    const stub = stubFetch(() =>
      jsonResponse(403, {
        error: {
          code: 'project_scope_mismatch',
          message: 'the token is scoped to another project',
          request_id: 'req-1',
          retryable: false,
        },
      }),
    );
    const out = capture();

    await expect(invoke(createArgv(), env, out, stub.fetchImpl)).resolves.toBe(3);

    expect(out.stderr.join('')).toContain('evidgate: error project_scope_mismatch: ');
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });
  });
});
