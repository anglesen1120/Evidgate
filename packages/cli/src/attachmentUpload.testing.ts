// The seams every `createAttachmentUploader` spec injects: a `fetch` double that records each
// call and reads the presigned PUT body chunk by chunk, the signed answers the store sends
// back, the digest and descriptor probes, and the temp roots a spec uploads from. Nothing here
// reaches a real network or waits on a real clock.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only, and
// tsconfig.json excludes it from the emitting project. tsconfig.spec.json type-checks it.
import { createHash } from 'node:crypto';
import { fstatSync } from 'node:fs';
import { mkdtemp, open as openRaw, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach } from 'vitest';

import {
  createAttachmentUploader,
  type AttachmentUploadInput,
  type AttachmentUploader,
  type AttachmentUploaderOptions,
} from './attachmentUpload.js';
import { jsonResponse } from './commands/cliSpec.testing.js';
import { devExchangeAnswer, devToken } from './commands/devExchange.testing.js';
import { CliError } from './errors.js';
import { resetSecrets } from './redact.js';
import { artifactKindMaxBytes, type ArtifactKind } from './uploadPaths.js';

/** Assembled at runtime so no token-shaped literal is stored in the spec. */
export const ingestToken = ['ev', 'gate', 'attach', 'ingest'].join('-');
export const cachedBearer = ['ev', 'gate', 'attach', 'cached'].join('-');
export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
export const producerId = 'shard-2';
export const executionKey = 'b7'.repeat(32);
export const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
/** A loopback presigned URL: the signature in the query is a dummy, never a real one. */
export const presignedUrl = `http://127.0.0.1:9/pending/bucket/${artifactId}?X-Amz-Signature=deadbeef`;

export interface Call {
  readonly method: string;
  readonly url: string;
  readonly headers: Headers;
  /** The bytes the request put on the wire (the JSON body for the API calls). */
  body: Buffer;
  /** One entry per body chunk as it crossed the wire; proves the PUT is streamed. */
  chunks: number[];
  json: unknown;
}

export type Responder = (call: Call) => Response | Promise<Response>;

export interface Harness {
  readonly fetch: typeof fetch;
  readonly calls: Call[];
}

/**
 * A scripted store. A non-PUT body is buffered (it is JSON); a PUT body is consumed chunk by
 * chunk so the request always completes — `keepBody: false` drops those bytes instead of
 * retaining them, which is what the cap-boundary case wants for a ten-megabyte file. The shared
 * `stubFetch` buffers a PUT whole and keeps no chunk sizes, so it cannot show a streamed body.
 */
export function harness(
  respond: Responder,
  options: { readonly keepBody?: boolean } = {},
): Harness {
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
      const reader: ReadableStreamDefaultReader<Uint8Array> = request.body.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }
        call.chunks.push(value.byteLength);
        if (keepBody) {
          parts.push(Buffer.from(value));
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

/** The scripted JSON answer, shared with the command specs rather than copied. */
export const json = jsonResponse;

/**
 * Delegates to `respond`, answering the token exchange itself. A dev token is an ingest
 * credential, so the default surface exchanges it before its first artifact call; the minted
 * `ci_producer` token lives ten minutes, so the next upload (and the resumed shard) reuses the
 * cache instead of asking.
 */
export function afterExchange(respond: Responder): Responder {
  return (call) => {
    const expiresAt = new Date(Date.now() + 10 * 60_000).toISOString();
    return devExchangeAnswer(call, { projectId: runId, expiresAt }) ?? respond(call);
  };
}

/** The `upload-url` 201 body: the signed size and checksum are the true ones for `bytes`. */
export function uploadUrlBody(input: {
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
export function readyBody(bytes: number): Record<string, unknown> {
  return {
    artifact_id: artifactId,
    state: 'ready',
    bytes,
    scan_state: 'pending',
    failure_code: null,
  };
}

/** `sha256:<hex>` of a file, read in bounded chunks so no case buffers it whole. */
export async function digestOfFile(path: string): Promise<{ sha256: string; bytes: number }> {
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
export async function sparseFile(path: string, bytes: number): Promise<void> {
  const handle = await openRaw(path, 'w');
  try {
    await handle.truncate(bytes);
  } finally {
    await handle.close();
  }
}

/** The static cap of one kind; the spec refuses to guess when a kind has none. */
export function capOf(kind: ArtifactKind): number {
  const cap = artifactKindMaxBytes[kind];
  if (cap === undefined) {
    throw new Error(`the spec needs a static cap for the ${kind} kind`);
  }
  return cap;
}

/** How many of this process's open descriptors point at `path` (POSIX only). */
export async function openDescriptors(path: string): Promise<number> {
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
export async function productionSources(directory: string): Promise<string[]> {
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
export async function rejection(promise: Promise<unknown>): Promise<CliError> {
  try {
    await promise;
  } catch (error) {
    return error as CliError;
  }
  throw new Error('the call resolved, but it was expected to be refused');
}

/** The upload request every case starts from; each supplies the file and what it overrides. */
export function input(
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
export function acceptingStore(
  digest: { readonly sha256: string; readonly bytes: number },
  options: { readonly keepBody?: boolean } = {},
): Harness {
  return harness(
    afterExchange((call) => {
      if (call.method === 'PUT') {
        return new Response(null, { status: 200 });
      }
      if (call.url.endsWith('/complete')) {
        return json(200, readyBody(digest.bytes));
      }
      return json(201, uploadUrlBody(digest));
    }),
    options,
  );
}

/**
 * Registers the per-test secret reset and temp roots the upload specs upload from, and returns
 * the factories they drive the uploader with. Each test gets a fresh root and token cache;
 * every temp root a spec makes is removed after that test. `onRoot` publishes each test's
 * directory to a spec that needs a path beside the uploaded file (`root` is replaced per test,
 * so it cannot be returned by value).
 */
export function useAttachmentUploadSpec(onRoot?: (directory: string) => void) {
  const directories: string[] = [];
  let root = '';
  let cacheDir = '';

  beforeEach(async () => {
    resetSecrets();
    root = await mkdtemp(join(tmpdir(), 'evidgate-attach-'));
    cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-attach-cache-'));
    directories.push(root, cacheDir);
    if (onRoot !== undefined) onRoot(root);
  });

  afterEach(async () => {
    await Promise.all(
      directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })),
    );
  });

  /** Another temporary root beside the upload root, removed after this test. */
  async function tempRoot(prefix: string): Promise<string> {
    const directory = await mkdtemp(join(tmpdir(), prefix));
    directories.push(directory);
    return directory;
  }

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

  return { directories, tempRoot, env, uploader, attachment };
}
