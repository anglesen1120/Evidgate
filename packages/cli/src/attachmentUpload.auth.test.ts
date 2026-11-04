// The cached CI token: a 401 means it is dead, so the cache is purged — and the caller must
// still see the server's refusal, not a swallowed one.
import { describe, expect, it } from 'vitest';

import {
  cachedBearer,
  harness,
  ingestToken,
  input,
  json,
  rejection,
  useAttachmentUploadSpec,
} from './attachmentUpload.testing.js';
import { readCachedToken, writeCachedToken } from './auth/tokenCache.js';
import { resolveConfig } from './config.js';
import { UploadFailedError } from './upload.js';

describe('createAttachmentUploader', () => {
  const { attachment, env, uploader } = useAttachmentUploadSpec();

  // contracts/cli.md §7: a 401 means the cached CI token is dead, so it is purged — and the
  // caller must still see the server's refusal, not a swallowed one.
  it('purges_the_cached_token_when_the_server_refuses_with_401', async () => {
    const { path } = await attachment('trace.zip');
    const ciEnv = env({
      EVIDGATE_ENV: 'ci',
      EVIDGATE_DEV_TOKEN: undefined,
      EVIDGATE_INGEST_TOKEN: ingestToken,
    });
    const config = resolveConfig({ env: ciEnv });
    await writeCachedToken(config, {
      token: cachedBearer,
      expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
    });
    expect(await readCachedToken(config)).toBeDefined();
    const store = harness(() =>
      json(401, {
        error: { code: 'token_expired', message: 'expired', request_id: 'req-7', retryable: false },
      }),
    );

    const error = await rejection(uploader(store, { env: ciEnv }).upload(input(path)));

    expect(error).toBeInstanceOf(UploadFailedError);
    expect((error as UploadFailedError).status).toBe(401);
    expect((error as UploadFailedError).code).toBe('token_expired');
    // The cache is gone, so the next attachment re-authenticates instead of replaying it.
    expect(await readCachedToken(config)).toBeUndefined();
    // The cached bearer was used (no exchange) and refused at the first API call.
    expect(store.calls).toHaveLength(1);
    expect(store.calls[0]?.headers.get('authorization')).toBe(`Bearer ${cachedBearer}`);
    expect(store.calls[0]?.url.endsWith('/v1/artifacts/upload-url')).toBe(true);
  });
});
