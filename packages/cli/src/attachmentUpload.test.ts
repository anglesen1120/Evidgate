// 04.3.3-T2.2: `createAttachmentUploader()` under a scripted store (04.3.3 §2 AC5–AC7,
// §8, §9). Every HTTP call is answered in-process through the fake `fetch` seam; the only
// real I/O is a temp directory holding the attachment files, the token cache and the spool.
// No case waits on a real clock, and nothing touches the network.
//
// What the spec pins, one bullet per acceptance row of the task:
// - AC: the happy path returns the artifact id and the stored size, and the presigned PUT
//   carries no API bearer — the signature is the only credential on the wire (S3);
// - AC: an attachment outside the allow-listed directories, or over its kind's cap, is
//   refused BEFORE any request (not even a token exchange), with a filtered reason;
// - AC: an unresolvable `EVIDGATE_*` surface is the CLI's own `missing_config` at creation;
// - AC: a 401 purges the cached CI token (contracts/cli.md §7) and resolves nothing;
// - AC: the descriptor the guard pinned is closed on the failing path as well as the
//   succeeding one (`/dev/fd` + `fstat`, skipped on win32 where that view does not exist);
// - AC5 failure path: the caller's drain `AbortSignal` (04.3.3-T2.2, NFR-04-1) is read at this
//   module's own checkpoints: an already-aborted call opens nothing and requests nothing, and
//   an abort after the digest mints nothing and puts nothing — `upload_aborted`, code only;
//   a PUT already in flight is not cancelled, it is abandoned at the next checkpoint;
// - boundary: no production source of this package imports the Playwright adapter, so the
//   dependency arrow stays cli ← adapter and never cli → adapter.
//
// The secrets rule (04.2.3 §8): bearer values are assembled at runtime, the presigned URL
// is a loopback origin with a dummy signature, and the journal is never asserted to hold a
// credential — only the artifact id.
import { createHash } from 'node:crypto';
import { fstatSync } from 'node:fs';
import { mkdtemp, open as openRaw, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  createAttachmentUploader,
  type AttachmentUploadInput,
  type AttachmentUploader,
  type AttachmentUploaderOptions,
} from './attachmentUpload.js';
import { readCachedToken, writeCachedToken } from './auth/tokenCache.js';
import { resolveConfig } from './config.js';
import { CliError } from './errors.js';
import { resetSecrets } from './redact.js';
import { UploadFailedError } from './upload.js';
import { artifactKindMaxBytes, type ArtifactKind } from './uploadPaths.js';

/** Assembled at runtime so no token-shaped literal is stored in the spec (T78). */
const devToken = ['ev', 'gate', 'attach', 'dev'].join('-');
const ingestToken = ['ev', 'gate', 'attach', 'ingest'].join('-');
const cachedBearer = ['ev', 'gate', 'attach', 'cached'].join('-');
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
const producerId = 'shard-2';
const executionKey = 'b7'.repeat(32);
const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
/** A loopback presigned URL: the signature in the query is a dummy, never a real one. */
const presignedUrl = `http://127.0.0.1:9/pending/bucket/${artifactId}?X-Amz-Signature=deadbeef`;
/** The forbidden dependency edge: the CLI must never import the reporter's package. */
const forbiddenAdapterImport =
  /(?:\bfrom\s*|\bimport\s*\(\s*|\brequire\s*\(\s*)['"][^'"]*(?:adapter-playwright|playwright-reporter)[^'"]*['"]/u;

interface Call {
  readonly method: string;
  readonly url: string;
  readonly headers: Headers;
  /** The bytes the request put on the wire (the JSON body for the API calls). */
  body: Buffer;
  /** One entry per body chunk as it crossed the wire; proves the PUT is streamed. */
  chunks: number[];
  json: unknown;
}

type Responder = (call: Call) => Response | Promise<Response>;

interface Harness {
  readonly fetch: typeof fetch;
  readonly calls: Call[];
}

/**
 * A scripted store. A non-PUT body is buffered (it is JSON); a PUT body is consumed chunk by
 * chunk so the request always completes — `keepBody: false` drops those bytes instead of
 * retaining them, which is what the cap-boundary case wants for a ten-megabyte file.
 */
function harness(respond: Responder, options: { readonly keepBody?: boolean } = {}): Harness {
  const calls: Call[] = [];
  const keepBody = options.keepBody ?? true;

  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const method = request.method.toUpperCase();
    const call: Call = {
      method,
      url: request.url,
      headers: new Headers(request.headers),
      body: Buffer.alloc(0),
      chunks: [],
      json: undefined,
    };
    calls.push(call);
    const parts: Buffer[] = [];
    if (method === 'PUT' && request.body !== null) {
      for await (const chunk of request.body as unknown as AsyncIterable<Uint8Array>) {
        call.chunks.push(chunk.byteLength);
        if (keepBody) {
          parts.push(Buffer.from(chunk));
        }
      }
    } else {
      parts.push(Buffer.from(await request.clone().arrayBuffer()));
    }
    call.body = Buffer.concat(parts);
    if (method !== 'PUT' && call.body.length > 0) {
      call.json = JSON.parse(call.body.toString('utf8'));
    }
    return respond(call);
  };

  return { fetch: fetchImpl, calls };
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

/** The `upload-url` 201 body: the signed size and checksum are the true ones for `bytes`. */
function uploadUrlBody(input: {
  readonly sha256: string;
  readonly bytes: number;
  readonly artifact?: string;
}): Record<string, unknown> {
  const digest = Buffer.from(input.sha256.slice('sha256:'.length), 'hex').toString('base64');
  return {
    artifact_id: input.artifact ?? artifactId,
    object_key: `pending/org/${input.artifact ?? artifactId}`,
    upload_url: presignedUrl,
    required_headers: {
      'Content-Length': String(input.bytes),
      'x-amz-checksum-sha256': digest,
    },
    expires_at: '2026-10-24T23:59:59.000Z',
  };
}

/** The `complete` 200 body for a committed ready artifact. */
function readyBody(bytes: number): Record<string, unknown> {
  return {
    artifact_id: artifactId,
    state: 'ready',
    bytes,
    scan_state: 'pending',
    failure_code: null,
  };
}

/** `sha256:<hex>` of a file, read in bounded chunks so no case buffers it whole. */
async function digestOfFile(path: string): Promise<{ sha256: string; bytes: number }> {
  const hash = createHash('sha256');
  let bytes = 0;
  const handle = await openRaw(path, 'r');
  try {
    const buffer = Buffer.alloc(64 * 1024);
    for (;;) {
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, null);
      if (bytesRead === 0) {
        break;
      }
      hash.update(buffer.subarray(0, bytesRead));
      bytes += bytesRead;
    }
  } finally {
    await handle.close();
  }
  return { sha256: `sha256:${hash.digest('hex')}`, bytes };
}

/** A file of exactly `bytes` bytes without writing them (the cap boundary needs no data). */
async function sparseFile(path: string, bytes: number): Promise<void> {
  const handle = await openRaw(path, 'w');
  try {
    await handle.truncate(bytes);
  } finally {
    await handle.close();
  }
}

/** The static cap of one kind; the spec refuses to guess when a kind has none. */
function capOf(kind: ArtifactKind): number {
  const cap = artifactKindMaxBytes[kind];
  if (cap === undefined) {
    throw new Error(`the spec needs a static cap for the ${kind} kind`);
  }
  return cap;
}

/** How many of this process's open descriptors point at `path` (POSIX only). */
async function openDescriptors(path: string): Promise<number> {
  const target = await stat(path);
  const entries = await readdir('/dev/fd');
  let count = 0;
  for (const entry of entries) {
    const fd = Number(entry);
    if (!Number.isInteger(fd) || fd < 0) {
      continue;
    }
    try {
      const stats = fstatSync(fd);
      if (stats.dev === target.dev && stats.ino === target.ino) {
        count += 1;
      }
    } catch {
      // The descriptor closed between readdir and fstat: nothing to count.
    }
  }
  return count;
}

/** Every non-spec `.ts` source of this package, so the boundary scan sees production code. */
async function productionSources(directory: string): Promise<string[]> {
  const found: string[] = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      found.push(...(await productionSources(path)));
      continue;
    }
    if (entry.name.endsWith('.ts') && !entry.name.endsWith('.test.ts')) {
      found.push(path);
    }
  }
  return found;
}

/** The error a promise rejected with; a spec must assert on it, not merely on rejection. */
async function rejection(promise: Promise<unknown>): Promise<CliError> {
  try {
    await promise;
  } catch (error) {
    return error as CliError;
  }
  throw new Error('the call resolved, but it was expected to be refused');
}

describe('createAttachmentUploader', () => {
  const directories: string[] = [];
  let root: string;
  let cacheDir: string;

  beforeEach(async () => {
    resetSecrets();
    root = await mkdtemp(join(tmpdir(), 'evidgate-attach-'));
    cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-attach-cache-'));
    directories.push(root, cacheDir);
  });

  afterEach(async () => {
    await Promise.all(
      directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })),
    );
  });

  /** The dev-token surface: loopback API, a project, a private cache dir, no spool. */
  function env(
    overrides: Record<string, string | undefined> = {},
  ): Record<string, string | undefined> {
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:9',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      EVIDGATE_CACHE_DIR: cacheDir,
      ...overrides,
    };
  }

  function uploader(
    store: { readonly fetch: typeof fetch },
    overrides: Partial<AttachmentUploaderOptions> = {},
  ): AttachmentUploader {
    return createAttachmentUploader({
      env: env(),
      dirs: [root],
      cwd: root,
      fetch: store.fetch,
      ...overrides,
    });
  }

  /** Writes one attachment under the allowed root and returns its path and bytes. */
  async function attachment(
    name: string,
    content: string = 'trace-bytes',
  ): Promise<{ readonly path: string; readonly content: Buffer }> {
    const path = join(root, name);
    const bytes = Buffer.from(content);
    await writeFile(path, bytes);
    return { path, content: bytes };
  }

  function input(
    file: string,
    overrides: Partial<AttachmentUploadInput> = {},
  ): AttachmentUploadInput {
    return {
      runId,
      producerId,
      executionKey,
      attemptNo: 1,
      kind: 'trace',
      mediaType: 'application/zip',
      file,
      ...overrides,
    };
  }

  /** A store that accepts one upload of exactly `digest`. */
  function acceptingStore(
    digest: { readonly sha256: string; readonly bytes: number },
    options: { readonly keepBody?: boolean } = {},
  ): Harness {
    return harness((call) => {
      if (call.method === 'PUT') {
        return new Response(null, { status: 200 });
      }
      if (call.url.endsWith('/complete')) {
        return json(200, readyBody(digest.bytes));
      }
      return json(201, uploadUrlBody(digest));
    }, options);
  }

  // The engine's `kindMaxBytes` surface is the guard's own table: a caller pre-screens
  // against the same numbers the refusal would use, so the two can never drift.
  it('exposes_the_static_kind_caps_of_the_guard', () => {
    const store = harness(() => json(200, {}));
    expect(uploader(store).kindMaxBytes).toBe(artifactKindMaxBytes);
    expect(uploader(store).kindMaxBytes.trace).toBe(capOf('trace'));
  });

  // AC5: one attachment in, one artifact id out — upload-url, the presigned PUT and
  // complete, in that order, with the API bearer never crossing to the store.
  it('uploads_one_attachment_and_returns_the_artifact_id', async () => {
    const { path, content } = await attachment('trace.zip');
    const digest = await digestOfFile(path);
    const store = acceptingStore(digest);

    const result = await uploader(store).upload(input(path));

    expect(result).toEqual({ artifactId, bytes: content.byteLength });
    expect(store.calls.map((call) => call.method)).toEqual(['POST', 'PUT', 'POST']);

    const [mint, put, complete] = store.calls;
    expect(mint === undefined ? '(no call)' : new URL(mint.url).pathname).toBe(
      '/v1/artifacts/upload-url',
    );
    expect(mint?.headers.get('authorization')).toBe(`Bearer ${devToken}`);
    expect(mint?.json).toEqual({
      run_id: runId,
      producer_id: producerId,
      execution_key: executionKey,
      attempt_no: 1,
      kind: 'trace',
      media_type: 'application/zip',
      bytes: content.byteLength,
      sha256: digest.sha256,
    });
    // The presigned URL is the destination, and its signature is the ONLY credential on
    // the wire: the bearer never leaves the API host (04.3.3 §8 S3).
    expect(put?.url).toBe(presignedUrl);
    expect(put?.headers.get('authorization')).toBeNull();
    expect(put?.headers.get('x-amz-checksum-sha256')).toBe(
      Buffer.from(digest.sha256.slice('sha256:'.length), 'hex').toString('base64'),
    );
    expect(put?.body.equals(content)).toBe(true);
    // Never buffered whole by the client: the body crossed in bounded chunks.
    expect(put?.chunks.length).toBeGreaterThan(0);
    expect(put?.chunks.every((size) => size <= 64 * 1024)).toBe(true);
    expect(complete?.url.endsWith(`/v1/artifacts/${artifactId}/complete`)).toBe(true);
    expect(complete?.headers.get('authorization')).toBe(`Bearer ${devToken}`);
  });

  // AC6: outside the allow-listed directories the guard refuses and NOTHING is requested —
  // the ingest surface proves the refusal precedes the token exchange (there is no cache
  // entry, so a token fetch would have to hit the network first).
  it('refuses_a_file_outside_the_allowed_directories_before_any_request', async () => {
    const outside = await mkdtemp(join(tmpdir(), 'evidgate-attach-outside-'));
    directories.push(outside);
    const path = join(outside, 'trace.zip');
    await writeFile(path, 'trace-bytes');
    const store = harness(() => json(200, {}));
    const ingest = uploader(store, {
      env: env({
        EVIDGATE_ENV: 'ci',
        EVIDGATE_DEV_TOKEN: undefined,
        EVIDGATE_INGEST_TOKEN: ingestToken,
      }),
    });

    const error = await rejection(ingest.upload(input(path)));

    expect(error).toBeInstanceOf(CliError);
    expect(error.code).toBe('upload_path_refused');
    expect(store.calls).toHaveLength(0);
    // The filtered reason: the file is named, the absolute temp path is not.
    expect(error.message).toContain('trace.zip');
    expect(error.message).not.toContain(outside);
  });

  // AC6 boundary pair: exactly at the cap passes the guard, one byte over is refused. Both
  // files are sparse, so neither case reads or writes ten megabytes of data.
  it('accepts_a_file_exactly_at_its_kind_cap', async () => {
    const cap = capOf('screenshot');
    const path = join(root, 'shot.png');
    await sparseFile(path, cap);
    const digest = await digestOfFile(path);
    const store = acceptingStore(digest, { keepBody: false });

    const result = await uploader(store).upload(
      input(path, { kind: 'screenshot', mediaType: 'image/png' }),
    );

    expect(result).toEqual({ artifactId, bytes: cap });
    expect(store.calls.map((call) => call.method)).toEqual(['POST', 'PUT', 'POST']);
  });

  it('refuses_a_file_one_byte_over_its_kind_cap_before_any_request', async () => {
    const cap = capOf('screenshot');
    const path = join(root, 'shot.png');
    await sparseFile(path, cap + 1);
    const store = harness(() => json(200, {}));

    const error = await rejection(
      uploader(store).upload(input(path, { kind: 'screenshot', mediaType: 'image/png' })),
    );

    expect(error.code).toBe('upload_too_large');
    expect(store.calls).toHaveLength(0);
  });

  // 04.3.3-T2.2 NFR-04-1 (F2/SEC-1): the drain signal is read BEFORE the file is opened. The
  // path is outside the allowed roots and does not exist, so `upload_aborted` (instead of
  // `upload_path_refused`/`upload_unreadable`) proves that nothing was opened and no request
  // was made: an abandoned attachment costs no descriptor and no token exchange.
  it('opens_nothing_and_requests_nothing_when_the_signal_is_already_aborted', async () => {
    const outside = await mkdtemp(join(tmpdir(), 'evidgate-attach-aborted-'));
    directories.push(outside);
    const path = join(outside, 'never-opened.zip');
    const controller = new AbortController();
    controller.abort();
    const store = harness(() => json(200, {}));

    const error = await rejection(
      uploader(store).upload(input(path), { signal: controller.signal }),
    );

    expect(error).toBeInstanceOf(CliError);
    expect(error.code).toBe('upload_aborted');
    expect(store.calls).toHaveLength(0);
    // The refusal carries the code and a fixed message: no path, no token (04 §6, S3).
    expect(error.message).not.toContain(outside);
  });

  // Same deadline, one checkpoint later: the file was opened and hashed, and the abort lands
  // before the mint. The digest is done, so nothing may be requested for an artifact — no
  // upload-url, no PUT, no complete — and the pinned descriptor is still released.
  it.skipIf(process.platform === 'win32')(
    'mints_nothing_and_puts_nothing_when_the_signal_aborts_after_the_digest',
    async () => {
      const { path } = await attachment('trace-aborted.zip');
      const controller = new AbortController();
      const ciEnv = env({
        EVIDGATE_ENV: 'ci',
        EVIDGATE_DEV_TOKEN: undefined,
        EVIDGATE_INGEST_TOKEN: ingestToken,
      });
      // The CI exchange is the first request of an upload, so aborting inside it is the drain
      // deadline firing after the digest and before any artifact exists.
      const store = harness(() => {
        controller.abort();
        return json(200, {
          token: cachedBearer,
          expires_at: new Date(Date.now() + 10 * 60_000).toISOString(),
          project_id: runId,
        });
      });

      const error = await rejection(
        uploader(store, { env: ciEnv }).upload(input(path), { signal: controller.signal }),
      );

      expect(error).toBeInstanceOf(CliError);
      expect(error.code).toBe('upload_aborted');
      expect(store.calls.map((call) => call.method)).toEqual(['POST']);
      expect(store.calls.some((call) => call.url.endsWith('/v1/artifacts/upload-url'))).toBe(false);
      expect(await openDescriptors(path)).toBe(0);
    },
  );

  // AC: a surface that cannot resolve is the CLI's own `missing_config`, raised at creation
  // so a run fails before its first test rather than after it.
  it('refuses_to_be_created_without_an_api_url_or_a_token_source', () => {
    const store = harness(() => json(200, {}));
    for (const unset of [
      env({ EVIDGATE_API_URL: undefined }),
      env({ EVIDGATE_DEV_TOKEN: undefined }),
    ]) {
      let thrown: unknown;
      try {
        createAttachmentUploader({ env: unset, dirs: [root], cwd: root, fetch: store.fetch });
      } catch (error) {
        thrown = error;
      }
      expect(thrown).toBeInstanceOf(CliError);
      expect((thrown as CliError).code).toBe('missing_config');
    }
    expect(store.calls).toHaveLength(0);
  });

  // Boundary of the same resolution: a malformed deadline is `invalid_config` at creation,
  // resolved once instead of per attachment.
  it('refuses_a_malformed_upload_deadline_at_creation', () => {
    const store = harness(() => json(200, {}));
    let thrown: unknown;
    try {
      createAttachmentUploader({
        env: env({ EVIDGATE_UPLOAD_DEADLINE: 'soon' }),
        dirs: [root],
        cwd: root,
        fetch: store.fetch,
      });
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(CliError);
    expect((thrown as CliError).code).toBe('invalid_config');
    // An explicit deadline is a caller decision: the malformed env value is not consulted.
    expect(() =>
      createAttachmentUploader({
        env: env({ EVIDGATE_UPLOAD_DEADLINE: 'soon' }),
        dirs: [root],
        cwd: root,
        fetch: store.fetch,
        deadlineMs: 1_000,
      }),
    ).not.toThrow();
  });

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

  // AC5 failure path: a 422 (unplanned attempt) is deterministic — one call, no PUT, no
  // complete — and the descriptor the guard pinned is released on the way out.
  it.skipIf(process.platform === 'win32')(
    'closes_the_file_descriptor_when_the_upload_is_refused',
    async () => {
      const { path } = await attachment('trace.zip');
      const store = harness(() =>
        json(422, {
          error: {
            code: 'unplanned_attempt',
            message: 'this attempt is not planned',
            request_id: 'req-9',
            retryable: false,
          },
        }),
      );
      expect(await openDescriptors(path)).toBe(0);

      const error = await rejection(uploader(store).upload(input(path)));

      expect(error).toBeInstanceOf(UploadFailedError);
      expect((error as UploadFailedError).status).toBe(422);
      expect((error as UploadFailedError).code).toBe('unplanned_attempt');
      expect(store.calls.map((call) => call.method)).toEqual(['POST']);
      expect(await openDescriptors(path)).toBe(0);
    },
  );

  // The success path must leave nothing open either: a shard uploads many attachments in
  // one process, so a leaked descriptor per file would exhaust the limit mid-run.
  it.skipIf(process.platform === 'win32')(
    'closes_the_file_descriptor_when_the_upload_succeeds',
    async () => {
      const { path } = await attachment('trace.zip');
      const store = acceptingStore(await digestOfFile(path));

      await uploader(store).upload(input(path));

      expect(await openDescriptors(path)).toBe(0);
    },
  );

  // 04.2.4 §12 / 04.3.3 §7: with a spool the journal is opened per `(run, producer)`, so a
  // restarted shard resumes its artifact rows instead of minting a second one — the second
  // call answers from the terminal record with no request at all.
  it('resumes_a_journaled_upload_without_a_second_artifact_row', async () => {
    const { path, content } = await attachment('trace.zip');
    const spoolDir = await mkdtemp(join(tmpdir(), 'evidgate-attach-spool-'));
    directories.push(spoolDir);
    const spoolEnv = env({ EVIDGATE_SPOOL_DIR: spoolDir });

    const first = acceptingStore(await digestOfFile(path));
    const uploaded = await uploader(first, { env: spoolEnv }).upload(input(path));
    expect(uploaded).toEqual({ artifactId, bytes: content.byteLength });
    expect(first.calls.map((call) => call.method)).toEqual(['POST', 'PUT', 'POST']);

    const restarted = harness(() => {
      throw new Error('a resumed upload must not make a request');
    });
    const resumed = await uploader(restarted, { env: spoolEnv }).upload(input(path));
    expect(resumed).toEqual({ artifactId, bytes: content.byteLength });
    expect(restarted.calls).toHaveLength(0);
  });

  // Boundary of the package graph (04.3.3 §7 "the reporter consumes the CLI"): production
  // sources of `@evidgate/cli` never import the Playwright adapter. Comments may name it;
  // an import specifier may not, or the dependency arrow would point both ways.
  it('keeps_the_production_sources_free_of_adapter_imports', async () => {
    const sourceRoot = fileURLToPath(new URL('.', import.meta.url));
    const sources = await productionSources(sourceRoot);
    expect(sources.length).toBeGreaterThan(10);

    const offenders: string[] = [];
    for (const file of sources) {
      const text = await readFile(file, 'utf8');
      if (forbiddenAdapterImport.test(text)) {
        offenders.push(relative(sourceRoot, file));
      }
    }

    expect(offenders).toEqual([]);
  });
});
