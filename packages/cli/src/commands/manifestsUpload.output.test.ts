// Output rules for `evidgate manifests upload`: `--verbose` must not print the manifest
// content or either credential, `--json` keeps errors on stderr, and a 401 without a cache
// entry is not reported as a cache error.
import { beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';
import { resetSecrets } from '../redact.js';

import { devStubFetch, devToken, mintedToken } from './devExchange.testing.js';
import {
  capture,
  createdManifest as ok,
  errorEnvelope as envelope,
  jsonResponse,
  project,
  stubFetch,
  useManifestSpec,
} from './manifestsUpload.testing.js';

describe('manifests upload — boundaries', () => {
  const { tempDir, manifestFile, devEnv } = useManifestSpec('evidgate-cli-qa-');

  beforeEach(() => {
    resetSecrets();
  });

  // ---- output rules: --verbose, --json on failure, token cache ----

  it('verbose_never_prints_manifest_content_or_token', async () => {
    const secret = 'SUPER-SECRET-MANIFEST-BODY';
    const file = await manifestFile(`{"schema":"manifest.v1","note":"${secret}"}`);
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(ok);
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file, '--verbose'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);

    const text = stdout.join('') + stderr.join('');
    expect(text).not.toContain(secret);
    expect(text).not.toContain(devToken);
    expect(text).not.toContain(mintedToken);
    expect(stderr.join('')).toContain('dev_token=[REDACTED]');
  });

  it('json_flag_keeps_errors_on_stderr', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(422, envelope('manifest_invalid', 'bad')),
    );
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file, '--json'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain('evidgate: error manifest_invalid: bad\n');
  });

  it('401_without_a_cache_entry_is_not_a_cache_error', async () => {
    const cacheDir = await tempDir();
    const env = {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: project,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
    };
    const config = resolveConfig({ env });
    const file = await manifestFile('{"schema":"manifest.v1"}');
    // A cold ingest source exchanges first, so this 401 arrives at `/v1/ci/token`; purging
    // the entry that was never written must still not surface a cache error.
    const { fetchImpl, calls } = stubFetch(() =>
      jsonResponse(401, envelope('token_revoked', 'revoked')),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error token_revoked: revoked\n');
    expect(text).not.toContain('token_cache_error');
    // One request, the refused exchange: no upload was attempted, and the purge of the
    // absent entry stayed silent.
    expect(calls).toHaveLength(1);
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });
});
