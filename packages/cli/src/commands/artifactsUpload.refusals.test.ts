// The server's own refusals of `evidgate artifacts upload` (contracts/cli.md §5, §7, §9):
// the code and request id of a 413 and a 422 surface verbatim and are never retried, a
// rejected body is reported by its failure code, a 401 purges the cached token, and a
// presigned URL inside a server message loses its query. The fixtures are in
// `artifactsUpload.testing.ts`.
import { createHash } from 'node:crypto';
import { chmod, mkdir } from 'node:fs/promises';
import { join } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';

import {
  artifactId,
  capture,
  jsonResponse,
  project,
  stubFetch,
  useArtifactsUploadSpec,
} from './artifactsUpload.testing.js';
import { devToken, mintedToken } from './devExchange.testing.js';

describe('artifacts upload', () => {
  const spec = useArtifactsUploadSpec();
  const { evidence, devEnv, argv } = spec;
  let root: string;

  beforeEach(() => {
    root = spec.rootDir();
  });

  // §5/§9: the server's code and request id surface verbatim; a 413 is never retried.
  it('surfaces_a_server_413_verbatim', async () => {
    const file = await evidence();
    const store = stubFetch(() =>
      jsonResponse(413, {
        error: {
          code: 'payload_too_large',
          message: 'the artifact is over the policy cap',
          request_id: 'req-413',
          retryable: false,
        },
      }),
    );
    const { io, stdout, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(3);

    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain('evidgate: error payload_too_large: ');
    expect(stderr.join('')).toContain('evidgate: request_id: req-413');
    expect(stderr.join('')).not.toContain(devToken);
    expect(stderr.join('')).not.toContain(mintedToken);
    expect(store.calls).toHaveLength(1);
    expect(store.exchanges).toHaveLength(1);
  });

  // AC4: an `upload-url` 422 (an unplanned `(execution_key, attempt_no)`, a disallowed
  // path or MIME) exits 3 with the server's own code and request id, makes exactly one
  // request and is never retried.
  it('surfaces_a_422_from_upload_url_verbatim', async () => {
    const file = await evidence();
    const store = stubFetch(() =>
      jsonResponse(422, {
        error: {
          code: 'unknown_execution_key',
          message: 'the execution is not planned for this run',
          request_id: 'req-422',
          retryable: false,
        },
      }),
    );
    const { io, stdout, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(3);

    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain('evidgate: error unknown_execution_key: ');
    expect(stderr.join('')).toContain('evidgate: request_id: req-422');
    expect(store.calls).toHaveLength(1);
    expect(store.exchanges).toHaveLength(1);
    expect(store.calls[0]?.url.endsWith('/v1/artifacts/upload-url')).toBe(true);
  });

  it('surfaces_a_body_digest_refusal_as_upload_checksum_mismatch', async () => {
    const file = await evidence();
    const store = stubFetch((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return jsonResponse(200, {
          artifact_id: artifactId,
          state: 'rejected',
          bytes: null,
          scan_state: null,
          failure_code: 'checksum_mismatch',
        });
      }
      return jsonResponse(201, {
        artifact_id: artifactId,
        object_key: 'pending/org/x',
        upload_url: 'http://127.0.0.1:9/pending/x?X-Amz-Signature=abc',
        required_headers: {
          'Content-Length': '11',
          'x-amz-checksum-sha256': createHash('sha256').update('trace-bytes').digest('base64'),
        },
        expires_at: '2026-10-24T23:59:59.000Z',
      });
    });
    const { io, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error artifact_rejected: ');
    expect(stderr.join('')).toContain(artifactId);
    expect(stderr.join('')).toContain('checksum_mismatch');
  });

  // §7: a 401 purges the cached token, and the server code still surfaces.
  it('purges_the_cached_token_on_401', async () => {
    const file = await evidence();
    const cacheDir = join(root, 'cache');
    await mkdir(cacheDir, { recursive: true });
    await chmod(cacheDir, 0o700);
    const env = {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: project,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
    };
    const config = resolveConfig({ env });
    await writeCachedToken(config, {
      token: 'cached-ci-token-value',
      expiresAt: '2099-01-01T00:00:00.000Z',
    });
    // The seeded entry is far in the future, which the cache refuses as untrusted, so the
    // stub's exchange mints the bearer and the 401 comes from the artifact route: that is
    // the refusal that must purge the cache.
    const store = stubFetch(() =>
      jsonResponse(401, {
        error: {
          code: 'token_expired',
          message: 'the CI token expired',
          request_id: 'req-401',
          retryable: false,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(main(argv(file), env, io, { fetch: store.fetchImpl })).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error token_expired: ');
    expect(stderr.join('')).toContain('run `evidgate auth login`');
    // Neither the dev credential nor the bearer it minted is ever printed.
    expect(stderr.join('')).not.toContain(devToken);
    expect(stderr.join('')).not.toContain(mintedToken);
    expect(store.calls).toHaveLength(1);
    expect(store.exchanges).toHaveLength(1);
    // The purge is best-effort and the server's error is the one that surfaced.
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  // §6 / §12: a presigned URL inside a server message loses its query — the signature is a
  // bearer credential and never appears on stderr.
  it('redacts_a_presigned_url_from_a_server_message', async () => {
    const file = await evidence();
    const store = stubFetch(() =>
      jsonResponse(409, {
        error: {
          code: 'idempotency_conflict',
          message:
            'the key was reused for a different payload: see http://127.0.0.1:9/pending/x?X-Amz-Signature=secret-sig&X-Amz-Credential=AKIA',
          request_id: 'req-409',
          retryable: false,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error idempotency_conflict: ');
    expect(text).not.toContain('secret-sig');
    expect(text).not.toContain('X-Amz-Signature');
    expect(text).toContain('http://127.0.0.1:9/pending/x?[REDACTED]');
    // The refused credential and the token it minted stay off the wire too.
    expect(text).not.toContain(devToken);
    expect(text).not.toContain(mintedToken);
  });
});
