// The failure paths of `evidgate manifests upload`: the 401 that purges the cached CI token
// and the 403 that must keep it, redaction of both credentials, and the replies that become
// `request_failed`/`transport` without echoing the body or URL.
import { beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';
import { resetSecrets } from '../redact.js';

import {
  devStubFetch,
  devToken,
  exchangeBody,
  isDevExchange,
  mintedToken,
} from './devExchange.testing.js';
import {
  capture,
  exchangePath,
  ingestUploadEnv,
  jsonResponse,
  manifestId,
  project,
  stubFetch,
  useManifestSpec,
} from './manifestsUpload.testing.js';

describe('manifests upload', () => {
  const { tempDir, manifestFile, devEnv } = useManifestSpec();

  beforeEach(() => {
    resetSecrets();
  });

  /** An ingest source: `getToken()` would exchange at `/v1/ci/token` when the cache is cold. */
  async function ingestEnv(extra: Record<string, string> = {}) {
    return ingestUploadEnv(await tempDir(), extra);
  }

  // 04.2.2-T1.1 / hand-over from 04.2.1-T1 #23: a 401 from the project-scoped command
  // purges the cached CI token; a 403 (scope mismatch) leaves it in place.
  it('unauthorized_purges_cache', async () => {
    const env = await ingestEnv();
    const config = resolveConfig({ env });
    const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
    await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });

    const file = await manifestFile('{"schema":"manifest.v1"}');
    const { fetchImpl, calls } = stubFetch(() =>
      jsonResponse(401, {
        error: {
          code: 'token_revoked',
          message: 'the token was revoked',
          request_id: 'req-1',
          retryable: false,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error token_revoked: ');
    // The cached token was reused (no exchange), so the upload is the only call.
    expect(calls).toHaveLength(1);
    await expect(readCachedToken(config)).resolves.toBeUndefined();

    // 403 is a scope mismatch, not a dead token: the cache survives.
    const forbiddenEnv = await ingestEnv();
    const forbiddenConfig = resolveConfig({ env: forbiddenEnv });
    await writeCachedToken(forbiddenConfig, { token: 'egt_cached_token', expiresAt });
    const forbiddenFile = await manifestFile('{"schema":"manifest.v1"}');
    const forbiddenStub = stubFetch(() =>
      jsonResponse(403, {
        error: {
          code: 'project_scope_mismatch',
          message: 'the token is scoped to another project',
          request_id: 'req-1',
          retryable: false,
        },
      }),
    );
    const forbiddenCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', forbiddenFile], forbiddenEnv, forbiddenCapture.io, {
        fetch: forbiddenStub.fetchImpl,
      }),
    ).resolves.toBe(3);
    expect(forbiddenCapture.stderr.join('')).toContain('evidgate: error project_scope_mismatch: ');
    // Again the upload is the only request (the cached token is fresh), so the cache it
    // keeps proves the 403 was not treated as a dead token.
    expect(forbiddenStub.calls).toHaveLength(1);
    await expect(readCachedToken(forbiddenConfig)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });
  });

  // A server message that embeds the bearer or the dev credential is redacted; the two are
  // different strings, so neither can hide behind a shared value.
  it('token_never_printed', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(500, {
        error: {
          code: 'server_error',
          message: `boom ${devToken} ${mintedToken}`,
          request_id: 'req-1',
          retryable: true,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('[REDACTED]');
    expect(text).not.toContain(devToken);
    expect(text).not.toContain(mintedToken);
  });

  // 04.2.2-T1.1 / §5: a non-envelope non-2xx and a 2xx with an invalid body are both
  // `request_failed`, and the body is never echoed.
  it('bad_response_bodies_request_failed', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();

    const gatewayStub = devStubFetch(
      () =>
        new Response('<html>bad gateway</html>', {
          status: 502,
          headers: { 'content-type': 'text/html' },
        }),
    );
    const gatewayCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', file], env, gatewayCapture.io, {
        fetch: gatewayStub.fetchImpl,
      }),
    ).resolves.toBe(3);
    const gatewayText = gatewayCapture.stderr.join('');
    expect(gatewayText).toContain('evidgate: error request_failed: ');
    expect(gatewayText).toContain('HTTP 502');
    expect(gatewayText).not.toContain('bad gateway');

    const invalidStub = devStubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: 'nope' }),
    );
    const invalidCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', file], env, invalidCapture.io, {
        fetch: invalidStub.fetchImpl,
      }),
    ).resolves.toBe(3);
    const invalidText = invalidCapture.stderr.join('');
    expect(invalidText).toContain('evidgate: error request_failed: ');
    expect(invalidText).not.toContain('nope');
  });

  // 04.2.2-T1.1 / §5: a transport failure is `transport` and never echoes the URL.
  it('transport_error_exit3', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    // The exchange succeeds, so the transport failure under test is the upload's own.
    const seen: string[] = [];
    const fetchImpl: typeof fetch = (input, init) => {
      const request = input instanceof Request ? input : new Request(input, init);
      seen.push(new URL(request.url).pathname);
      return isDevExchange(request)
        ? Promise.resolve(jsonResponse(200, exchangeBody(mintedToken)))
        : Promise.reject(new TypeError('fetch failed http://secret'));
    };
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error transport: ');
    expect(text).not.toContain('http://secret');
    // The exchange succeeded; every attempt after it is the upload, whose transport
    // failure is retried before the command exits.
    expect(seen[0]).toBe(exchangePath);
    expect(seen.length).toBeGreaterThan(1);
    for (const path of seen.slice(1)) {
      expect(path).toBe(`/v1/projects/${project}/manifests`);
    }
  });
});
