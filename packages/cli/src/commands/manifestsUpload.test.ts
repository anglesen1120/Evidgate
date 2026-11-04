// `evidgate manifests upload` on the binary: the raw-bytes upload, the `manifest_id`/
// `manifest_hash` output, the `manifest_invalid` details passthrough, the local size cap and
// the input errors that exit before any request (contracts/cli.md §5/§6).
import { createHash } from 'node:crypto';
import { join } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';
import { resetSecrets } from '../redact.js';

import { devStubFetch, devToken, mintedToken } from './devExchange.testing.js';
import {
  capture,
  ingestUploadEnv,
  jsonResponse,
  manifestHash,
  manifestId,
  project,
  stubFetch,
  useManifestSpec,
} from './manifestsUpload.testing.js';

/** The server's documented body cap (`createManifest`: "Body ≤ 256 KiB → 413"). */
const maxManifestBytes = 256 * 1024;

describe('manifests upload', () => {
  const { tempDir, manifestFile, devEnv } = useManifestSpec();

  beforeEach(() => {
    resetSecrets();
  });

  /** An ingest source: `getToken()` would exchange at `/v1/ci/token` when the cache is cold. */
  async function ingestEnv(extra: Record<string, string> = {}) {
    return ingestUploadEnv(await tempDir(), extra);
  }

  // 04.2.2-T1.1 / AC1: the file bytes travel unchanged (the server hashes the canonical
  // form, so a re-serialisation could lose the large integer's precision) and the two
  // ids are printed.
  it('prints_ids', async () => {
    const text =
      '{\n  "schema": "manifest.v1",\n  "n": 12345678901234567890,\n  "pad": "  x  "\n}\n';
    const file = await manifestFile(text);
    const env = await devEnv();
    const { fetchImpl, calls, exchanges } = devStubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);

    expect(stdout.join('')).toBe(`manifest_id=${manifestId} manifest_hash=${manifestHash}\n`);
    expect(stderr.join('')).toBe('');
    // The dev token is exchanged once, before the upload that carries its bearer.
    expect(exchanges).toHaveLength(1);
    expect(calls).toHaveLength(1);
    const call = calls[0];
    expect(call?.request.method).toBe('POST');
    // `createApiClient` puts the SDK's `/v1` server prefix on the base URL, so the
    // operation path is `<root>/v1/projects/{p}/manifests` (the env value carries no `/v1`).
    expect(new URL(call?.request.url ?? '').pathname).toBe(`/v1/projects/${project}/manifests`);
    // The upload runs on the exchanged token: the dev token only travels in the exchange
    // body, so a regression that sent it as the bearer fails here.
    expect(call?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(JSON.parse(exchanges[0]?.body.toString('utf8') ?? '')).toStrictEqual({
      ingest_token: devToken,
    });
    expect(call?.request.headers.get('content-type')).toBe('application/json');
    // 00 §9.1: the route requires an `Idempotency-Key` (openapi `required: true`); the
    // file's own digest makes a retry of the same manifest replay the stored 201.
    expect(call?.request.headers.get('idempotency-key')).toBe(
      createHash('sha256').update(text).digest('hex'),
    );
    expect(call?.body.toString('utf8')).toBe(text);
  });

  // 04.2.2-T1.1 / AC1: `--json` prints exactly one line with the two ids.
  it('json_output', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file, '--json'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);

    expect(stdout.join('')).toBe(
      `{"manifest_id":"${manifestId}","manifest_hash":"${manifestHash}"}\n`,
    );
    expect(stderr.join('')).toBe('');
  });

  // 04.2.2-T1.1 / AC1: a 422 `manifest_invalid` exits 3 and passes the details through.
  it('invalid_manifest_exit3', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(422, {
        error: {
          code: 'manifest_invalid',
          message: 'the manifest is invalid',
          request_id: 'req-1',
          retryable: false,
          details: [
            { index: 0, path: '/executions/0/case_revision_id', reason: 'unknown_case_revision' },
            { path: '/matrix', reason: 'matrix_not_covered' },
          ],
        },
      }),
    );
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error manifest_invalid: ');
    expect(text).toContain('evidgate: request_id: req-1');
    expect(text).toContain(
      'evidgate: detail: index=0 path=/executions/0/case_revision_id reason=unknown_case_revision',
    );
    expect(text).toContain('evidgate: detail: path=/matrix reason=matrix_not_covered');
    expect(stdout.join('')).toBe('');
  });

  // 04.2.2-T1.1 / §5: at most 50 detail lines, then a `(k more)` line; every field is
  // control-char escaped so a detail can never forge a `::` workflow command.
  it('details_capped_and_escaped', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const details = Array.from({ length: 60 }, (_value, index) => ({
      index,
      path: `/p/${index}`,
      reason: 'r',
    }));
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(422, {
        error: {
          code: 'manifest_invalid',
          message: 'the manifest is invalid',
          request_id: 'req-1',
          retryable: false,
          details,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const detailLines = stderr
      .join('')
      .split('\n')
      .filter((line) => line.startsWith('evidgate: detail: '));
    expect(detailLines).toHaveLength(51);
    expect(detailLines[50]).toBe('evidgate: detail: (10 more)');

    // A path that embeds a newline and a `::` command is escaped, not split.
    const escapedEnv = await devEnv();
    const escapedFile = await manifestFile('{"schema":"manifest.v1"}');
    const { fetchImpl: escapedFetch } = devStubFetch(() =>
      jsonResponse(422, {
        error: {
          code: 'manifest_invalid',
          message: 'the manifest is invalid',
          request_id: 'req-1',
          retryable: false,
          details: [{ path: '/x\n::error::x', reason: 'bad' }],
        },
      }),
    );
    const escapedCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', escapedFile], escapedEnv, escapedCapture.io, {
        fetch: escapedFetch,
      }),
    ).resolves.toBe(3);

    const escapedText = escapedCapture.stderr.join('');
    expect(escapedText).toContain('path=/x\\u000a::error::x');
    expect(escapedText).not.toContain('\n::error::x');
    for (const line of escapedText.split('\n')) {
      if (line.length > 0) {
        expect(line.startsWith('evidgate: ')).toBe(true);
      }
    }
  });

  // 04.2.2-T1.1 / §10 done-when: a file over the cap exits 3 before any request (no
  // token exchange either); exactly the cap is sent.
  it('oversize_no_request', async () => {
    const oversize = await manifestFile('a'.repeat(maxManifestBytes + 1));
    const env = await devEnv();
    const { fetchImpl, calls, exchanges } = devStubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', oversize], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error manifest_too_large: ');
    // The size check runs before the token, so the dev run makes no request at all.
    expect(calls).toHaveLength(0);
    expect(exchanges).toHaveLength(0);

    // An ingest source would exchange a token first; the size check still wins.
    const ingest = await ingestEnv();
    const ingestStub = stubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const ingestCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', oversize], ingest, ingestCapture.io, {
        fetch: ingestStub.fetchImpl,
      }),
    ).resolves.toBe(3);
    expect(ingestCapture.stderr.join('')).toContain('evidgate: error manifest_too_large: ');
    expect(ingestStub.calls).toHaveLength(0);

    // Exactly the cap is accepted and sent.
    const boundary = await manifestFile(`{"pad":"${'a'.repeat(maxManifestBytes - 10)}"}`);
    const boundaryStub = devStubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const boundaryCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', boundary], env, boundaryCapture.io, {
        fetch: boundaryStub.fetchImpl,
      }),
    ).resolves.toBe(0);
    expect(boundaryStub.calls).toHaveLength(1);
    expect(boundaryStub.exchanges).toHaveLength(1);
  });

  // 04.2.2-T1.1 / §5: an unreadable input (missing, a directory, empty) exits 3 with no
  // request at all.
  it('unreadable_no_request', async () => {
    const env = await devEnv();
    const missing = join(await tempDir(), 'nope.json');
    const directory = await tempDir();
    const empty = await manifestFile('');

    for (const path of [missing, directory, empty]) {
      const { fetchImpl, calls } = devStubFetch(() =>
        jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
      );
      const { io, stderr } = capture();
      await expect(
        main(['manifests', 'upload', '--file', path], env, io, { fetch: fetchImpl }),
      ).resolves.toBe(3);
      expect(stderr.join('')).toContain('evidgate: error manifest_unreadable: ');
      expect(calls).toHaveLength(0);
    }
  });

  // 04.2.2-T1.1 / §5: a missing `--file` is a usage error before any request.
  it('missing_file_flag_exit3', async () => {
    const env = await devEnv();
    const { fetchImpl, calls } = devStubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const { io, stderr } = capture();

    await expect(main(['manifests', 'upload'], env, io, { fetch: fetchImpl })).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error usage: ');
    expect(text).toContain('--file');
    // The command-level usage error carries the same hint as main.ts's parse errors.
    expect(text).toContain("evidgate: hint: run 'evidgate --help' for usage");
    expect(calls).toHaveLength(0);
  });
});
