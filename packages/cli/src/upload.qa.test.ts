// QA probes for 04.2.4-T1 / 04.3.3-T1 upload surface (written by QA agent S56, not the author).
// Offline and deterministic: an in-process fetch fake, a virtual clock, no wall-clock asserts.
// The three defects QA filed (an upload-url body without `object_key`, conflicting case-variant
// signed headers, and a `..foo` directory refused as traversal) were fixed in the same task, so
// every case here is an ordinary assertion — no probe is an `it.fails`. Secrets are assembled at
// runtime.
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resolveConfig } from './config.js';
import { resetSecrets } from './redact.js';
import { isRetryable } from './sender.js';
import {
  checksumPattern,
  deriveUploadUrlIdempotencyKey,
  uploadFile,
  UploadFailedError,
  type UploadDeps,
  type UploadRequest,
} from './upload.js';
import { closeUploadFile, mediaTypeFor, openUploadFile, type UploadFile } from './uploadPaths.js';
import { resolveUploadDeadlineMs } from './uploadRetry.js';

const token = ['tk', 'qa', 'value'].join('-');
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
const presigned = `http://127.0.0.1:9/pending/bucket/${artifactId}?X-Amz-Signature=deadbeef`;
const config = resolveConfig({
  env: { EVIDGATE_API_URL: 'http://127.0.0.1:9', EVIDGATE_PROJECT: 'proj-1' },
});

interface StoreOptions {
  readonly headers: Record<string, string>;
  readonly bytes: number;
  readonly uploadUrl?: string;
  readonly omitObjectKey?: boolean;
  readonly objectKey?: string;
  readonly expiresAt?: string;
  readonly complete?: Record<string, unknown>;
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

/** Scripted store: `upload-url` answers per options, the PUT succeeds, `complete` answers. */
function store(opts: StoreOptions): UploadDeps {
  const clock = { now: 1_000 };
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const method = request.method.toUpperCase();
    if (method === 'PUT') {
      // Drain the streamed body so the client's read-side verification runs.
      if (request.body !== null) {
        await new Response(request.body).arrayBuffer();
      }
      return new Response(null, { status: 200 });
    }
    if (request.url.endsWith('/v1/artifacts/upload-url')) {
      const payload: Record<string, unknown> = {
        artifact_id: artifactId,
        upload_url: opts.uploadUrl ?? presigned,
        required_headers: opts.headers,
        expires_at: opts.expiresAt ?? '2026-10-24T23:59:59.000Z',
      };
      if (opts.omitObjectKey !== true) {
        payload.object_key = opts.objectKey ?? `pending/org/${artifactId}`;
      }
      return jsonResponse(201, payload);
    }
    return jsonResponse(
      200,
      opts.complete ?? {
        artifact_id: artifactId,
        state: 'ready',
        bytes: opts.bytes,
        scan_state: 'pending',
        failure_code: null,
      },
    );
  };
  return {
    fetch: fetchImpl,
    sleep: (ms) => {
      clock.now += ms;
      return Promise.resolve();
    },
    now: () => clock.now,
    random: () => 0.5,
  };
}

function digestBase64(sha256: string): string {
  return Buffer.from(sha256.slice('sha256:'.length), 'hex').toString('base64');
}

function sha256Of(content: string): string {
  return `sha256:${createHash('sha256').update(content).digest('hex')}`;
}

function uploadRequest(file: UploadFile, sha256: string, deps: UploadDeps): UploadRequest {
  return {
    config,
    token,
    runId,
    producerId: 'shard-1',
    executionKey: 'a3'.repeat(32),
    attemptNo: 1,
    kind: 'trace',
    mediaType: 'application/zip',
    file,
    sha256,
    journal: undefined,
    deadlineMs: 60_000,
    deps,
  };
}

describe('QA: isRetryable (sender) status and envelope matrix', () => {
  it.each([
    [408, true],
    [429, true],
    [500, true],
    [503, true],
    [400, false],
    [401, false],
    [422, false],
    [200, false],
  ])('status %i retryable=%s', (status, expected) => {
    expect(isRetryable(status, undefined)).toBe(expected);
  });

  it('a 5xx with envelope retryable:false is not retried', () => {
    const envelope = { error: { code: 'x', message: 'm', request_id: 'r-1', retryable: false } };
    expect(isRetryable(500, envelope)).toBe(false);
  });

  it('a 503 whose envelope does not forbid retry is retried', () => {
    const envelope = { error: { code: 'x', message: 'm', request_id: 'r-2', retryable: true } };
    expect(isRetryable(503, envelope)).toBe(true);
  });
});

describe('QA: deriveUploadUrlIdempotencyKey boundaries', () => {
  const base = {
    runId,
    producerId: 'shard-1',
    executionKey: 'a3'.repeat(32),
    attemptNo: 1,
    kind: 'trace' as const,
    fileSha256: `sha256:${'b'.repeat(64)}`,
    mintNo: 0,
  };

  it('is 64 lowercase hex and deterministic', () => {
    const key = deriveUploadUrlIdempotencyKey(base);
    expect(key).toMatch(/^[0-9a-f]{64}$/);
    expect(deriveUploadUrlIdempotencyKey(base)).toBe(key);
  });

  it('treats mintNo 0 as the AC1 key (no mint suffix)', () => {
    expect(deriveUploadUrlIdempotencyKey(base)).toBe(
      deriveUploadUrlIdempotencyKey({ ...base, mintNo: 0 }),
    );
  });

  it('a renewal (mintNo 1) yields a different key', () => {
    expect(deriveUploadUrlIdempotencyKey({ ...base, mintNo: 1 })).not.toBe(
      deriveUploadUrlIdempotencyKey(base),
    );
  });

  it('length-prefixing separates field boundaries (ab|c vs a|bc)', () => {
    const left = deriveUploadUrlIdempotencyKey({ ...base, producerId: 'bc', runId: 'a' });
    const right = deriveUploadUrlIdempotencyKey({ ...base, producerId: 'c', runId: 'ab' });
    expect(left).not.toBe(right);
  });

  it('differs when the digest differs', () => {
    expect(
      deriveUploadUrlIdempotencyKey({ ...base, fileSha256: `sha256:${'c'.repeat(64)}` }),
    ).not.toBe(deriveUploadUrlIdempotencyKey(base));
  });
});

describe('QA: checksum and media-type helpers', () => {
  it('checksumPattern rejects uppercase hex and short digests', () => {
    expect(checksumPattern.test(`sha256:${'a'.repeat(64)}`)).toBe(true);
    expect(checksumPattern.test(`sha256:${'A'.repeat(64)}`)).toBe(false);
    expect(checksumPattern.test(`sha256:${'a'.repeat(63)}`)).toBe(false);
  });

  it('mediaTypeFor is case-insensitive on the extension', () => {
    expect(mediaTypeFor('trace', 'run.ZIP')).toBe('application/zip');
  });

  it('mediaTypeFor returns undefined for an extension outside the static table', () => {
    expect(mediaTypeFor('trace', 'run.exe')).toBeUndefined();
  });
});

describe('QA: resolveUploadDeadlineMs bounds (env input)', () => {
  it('accepts the 24h ceiling exactly', () => {
    expect(resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: '24h' })).toBe(86_400_000);
  });

  it('refuses a value one second over the ceiling', () => {
    expect(() => resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: '86401s' })).toThrow();
  });

  it('refuses zero', () => {
    expect(() => resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: '0s' })).toThrow();
  });

  it('refuses a non-numeric string', () => {
    expect(() => resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: 'soon' })).toThrow();
  });
});

describe('QA: uploadFile server-input and presigned-URL probes', () => {
  const dirs: string[] = [];
  let root: string;
  let opened: UploadFile | undefined;

  beforeEach(async () => {
    resetSecrets();
    root = await mkdtemp(join(tmpdir(), 'evidgate-qa-upload-'));
    dirs.push(root);
  });

  afterEach(async () => {
    if (opened !== undefined) {
      await closeUploadFile(opened);
      opened = undefined;
    }
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function openFixture(content: string): Promise<{ file: UploadFile; sha256: string }> {
    const path = join(root, 'trace.zip');
    await writeFile(path, content);
    const file = await openUploadFile({ file: path, kind: 'trace', dirs: [root], cwd: root });
    opened = file;
    return { file, sha256: sha256Of(content) };
  }

  it('accepts a payload whose signed Content-Length and checksum are exact (control)', async () => {
    const { file, sha256 } = await openFixture('payload');
    const headers = { 'Content-Length': '7', 'x-amz-checksum-sha256': digestBase64(sha256) };
    const outcome = await uploadFile(uploadRequest(file, sha256, store({ headers, bytes: 7 })));
    expect(outcome.bytes).toBe(7);
    expect(outcome.state).toBe('ready');
  });

  it('refuses a presigned URL that carries embedded credentials', async () => {
    const { file, sha256 } = await openFixture('payload');
    const headers = { 'Content-Length': '7', 'x-amz-checksum-sha256': digestBase64(sha256) };
    const creds = ['http://', 'user', ':', 'pw', '@127.0.0.1:9/pending/x'].join('');
    const deps = store({ headers, bytes: 7, uploadUrl: creds });
    await expect(uploadFile(uploadRequest(file, sha256, deps))).rejects.toBeInstanceOf(
      UploadFailedError,
    );
  });

  it('refuses a complete response whose bytes is a non-integer', async () => {
    const { file, sha256 } = await openFixture('payload');
    const headers = { 'Content-Length': '7', 'x-amz-checksum-sha256': digestBase64(sha256) };
    const deps = store({
      headers,
      bytes: 7,
      complete: { artifact_id: artifactId, state: 'ready', bytes: 7.5, scan_state: 'pending' },
    });
    await expect(uploadFile(uploadRequest(file, sha256, deps))).rejects.toBeInstanceOf(
      UploadFailedError,
    );
  });

  it('refuses a complete response with a non-terminal state', async () => {
    const { file, sha256 } = await openFixture('payload');
    const headers = { 'Content-Length': '7', 'x-amz-checksum-sha256': digestBase64(sha256) };
    const deps = store({
      headers,
      bytes: 7,
      complete: { artifact_id: artifactId, state: 'pending', bytes: 7 },
    });
    await expect(uploadFile(uploadRequest(file, sha256, deps))).rejects.toBeInstanceOf(
      UploadFailedError,
    );
  });

  // openapi `UploadUrlResponse`: `object_key` is required, so a body without one (or with an
  // empty one) is refused on the same `request_failed` path as a missing `artifact_id`.
  it('refuses an upload-url 201 that omits object_key', async () => {
    const { file, sha256 } = await openFixture('payload');
    const headers = { 'Content-Length': '7', 'x-amz-checksum-sha256': digestBase64(sha256) };
    const deps = store({ headers, bytes: 7, omitObjectKey: true });
    await expect(uploadFile(uploadRequest(file, sha256, deps))).rejects.toMatchObject({
      code: 'request_failed',
    });
  });

  it('refuses an upload-url 201 whose object_key is empty', async () => {
    const { file, sha256 } = await openFixture('payload');
    const headers = { 'Content-Length': '7', 'x-amz-checksum-sha256': digestBase64(sha256) };
    const deps = store({ headers, bytes: 7, objectKey: '' });
    await expect(uploadFile(uploadRequest(file, sha256, deps))).rejects.toMatchObject({
      code: 'request_failed',
    });
  });

  it('refuses an upload-url 201 whose expires_at is not a date', async () => {
    const { file, sha256 } = await openFixture('payload');
    const headers = { 'Content-Length': '7', 'x-amz-checksum-sha256': digestBase64(sha256) };
    const deps = store({ headers, bytes: 7, expiresAt: 'soon' });
    await expect(uploadFile(uploadRequest(file, sha256, deps))).rejects.toMatchObject({
      code: 'request_failed',
    });
  });

  // Two names that fold to the same header are one signed header: identical duplicates are a
  // harmless alias, two different values make the signed set ambiguous and are refused before
  // any byte is sent — and the message names the header, never a signed value.
  it('refuses conflicting case-variant signed Content-Length headers', async () => {
    const { file, sha256 } = await openFixture('payload');
    const headers = {
      'content-length': '999',
      'Content-Length': '7',
      'x-amz-checksum-sha256': digestBase64(sha256),
    };
    const deps = store({ headers, bytes: 7 });
    const thrown = await uploadFile(uploadRequest(file, sha256, deps)).then(
      () => undefined,
      (error: unknown) => error,
    );
    expect(thrown).toBeInstanceOf(UploadFailedError);
    const message = (thrown as Error).message;
    expect(message).toContain('content-length');
    expect(message).not.toContain('999');
  });

  it('collapses identical case-variant signed headers', async () => {
    const { file, sha256 } = await openFixture('payload');
    const headers = {
      'content-length': '7',
      'Content-Length': '7',
      'x-amz-checksum-sha256': digestBase64(sha256),
    };
    const outcome = await uploadFile(uploadRequest(file, sha256, store({ headers, bytes: 7 })));
    expect(outcome.state).toBe('ready');
  });
});

describe('QA: openUploadFile path guard', () => {
  const dirs: string[] = [];
  let root: string;
  let opened: UploadFile | undefined;

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), 'evidgate-qa-paths-'));
    dirs.push(root);
  });

  afterEach(async () => {
    if (opened !== undefined) {
      await closeUploadFile(opened);
      opened = undefined;
    }
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  // The containment check is segment-exact: a child named `..foo` is inside the root, while
  // the parent (`..`) and a sibling reached through it stay refused.
  it('accepts a file inside a directory whose name begins with two dots', async () => {
    const dir = join(root, '..foo');
    await mkdir(dir, { recursive: true });
    const path = join(dir, 'trace.zip');
    await writeFile(path, 'x');
    opened = await openUploadFile({ file: path, kind: 'trace', dirs: [root], cwd: root });
    expect(opened.size).toBe(1);
  });

  it('still refuses a parent-directory escape', async () => {
    const parent = await mkdtemp(join(tmpdir(), 'evidgate-qa-escape-'));
    dirs.push(parent);
    const inner = join(parent, 'root');
    await mkdir(inner, { recursive: true });
    await writeFile(join(parent, 'sibling.zip'), 'y');
    await expect(
      openUploadFile({
        file: join(inner, '..', 'sibling.zip'),
        kind: 'trace',
        dirs: [inner],
        cwd: inner,
      }),
    ).rejects.toThrow(/outside the allowed directories/);
  });

  it('refuses an empty --file value', async () => {
    await expect(
      openUploadFile({ file: '', kind: 'trace', dirs: [root], cwd: root }),
    ).rejects.toThrow(/is empty/);
  });

  it('refuses a control character in --file', async () => {
    await expect(
      openUploadFile({ file: 'a\u0007b.zip', kind: 'trace', dirs: [root], cwd: root }),
    ).rejects.toThrow(/control character/);
  });
});
