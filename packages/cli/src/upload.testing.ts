// The scripted store and the per-test upload context the `uploadFile()` specs share: a fetch
// that records every call and streams the PUT body chunk by chunk, the signed answers the
// store sends back, and the temp-root file, journal and request factories the command's own
// path uses. Every spec file calls `useUploadSpec()` for itself, so each get its own hooks and
// state and the split files start from the same baseline.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only, and
// tsconfig.json excludes it from the emitting project. tsconfig.spec.json type-checks it.
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach } from 'vitest';

import { resolveConfig } from './config.js';
import { resetSecrets } from './redact.js';
import { hashUploadFile, type UploadDeps, type UploadRequest } from './upload.js';
import { UploadJournal, type UploadIdentity } from './uploadJournal.js';
import { closeUploadFile, openUploadFile, type UploadFile } from './uploadPaths.js';

/** Assembled at runtime so no token-shaped literal is stored in the spec. */
export const token = ['tk', 'upload', 'value'].join('-');
export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
export const producerId = 'shard-1';
export const executionKey = 'a3'.repeat(32);
export const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
export const secondArtifactId = '0a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d';
/** The presigned URL: a loopback origin with a signature in the query (never printed). */
const presignedUrl = `http://127.0.0.1:9/pending/bucket/${artifactId}?X-Amz-Signature=deadbeef&X-Amz-Credential=AKIA%2F20261024`;
const config = resolveConfig({
  env: { EVIDGATE_API_URL: 'http://127.0.0.1:9', EVIDGATE_PROJECT: 'proj-1' },
});

export interface Call {
  readonly method: string;
  readonly url: string;
  readonly headers: Headers;
  /** The bytes the request put on the wire (the JSON body for the API calls). */
  body: Buffer;
  /** One entry per body chunk as it crossed the wire; proves the PUT never buffers it. */
  chunks: number[];
  json: unknown;
}

export type Responder = (call: Call) => Response | Promise<Response>;

export interface Harness {
  readonly deps: UploadDeps;
  readonly calls: Call[];
  readonly sleeps: number[];
  readonly clock: { now: number };
}

/**
 * A scripted store. A non-PUT body is buffered (it is JSON); a PUT body is consumed chunk
 * by chunk, recording each chunk's size, so a spec can prove the file was streamed.
 */
export function harness(respond: Responder): Harness {
  const calls: Call[] = [];
  const sleeps: number[] = [];
  const clock = { now: 1_000 };

  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const method = request.method.toUpperCase();
    // The call is recorded BEFORE the body is consumed: a body that aborts mid-stream (the
    // file changed between passes) still counts as an attempt that reached the store.
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
      for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
        call.chunks.push(chunk.byteLength);
        parts.push(Buffer.from(chunk));
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

  return {
    calls,
    sleeps,
    clock,
    deps: {
      fetch: fetchImpl,
      sleep: (milliseconds) => {
        sleeps.push(milliseconds);
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
    },
  };
}

export function json(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** The `upload-url` 201 body: the signed size and checksum are the true ones for `bytes`. */
export function uploadUrlBody(input: {
  readonly bytes: Buffer;
  readonly sha256: string;
  readonly artifact?: string;
  readonly url?: string;
  readonly expiresAt?: string;
}): Record<string, unknown> {
  const digest = Buffer.from(input.sha256.slice('sha256:'.length), 'hex').toString('base64');
  return {
    artifact_id: input.artifact ?? artifactId,
    object_key: `pending/org/${input.artifact ?? artifactId}`,
    upload_url: input.url ?? presignedUrl,
    required_headers: {
      'Content-Length': String(input.bytes.byteLength),
      'x-amz-checksum-sha256': digest,
    },
    expires_at: input.expiresAt ?? '2026-10-24T23:59:59.000Z',
  };
}

/** The `complete` 200 body for a committed ready artifact. */
export function readyBody(bytes: number, artifact = artifactId): Record<string, unknown> {
  return {
    artifact_id: artifact,
    state: 'ready',
    bytes,
    scan_state: 'pending',
    failure_code: null,
  };
}

export function uploadUrlCall(calls: readonly Call[]): Call {
  const call = calls.find((entry) => entry.url.endsWith('/v1/artifacts/upload-url'));
  if (call === undefined) {
    throw new Error('no upload-url call was made');
  }
  return call;
}

/**
 * Registers the per-test temp root and its cleanup hooks, and returns the factories that
 * drive `uploadFile()` through it. `onRoot` publishes each test's directory to a spec that
 * needs a sibling path beside the uploaded file (`root` is replaced per test, so it cannot be
 * returned by value).
 */
export function useUploadSpec(onRoot?: (directory: string) => void) {
  const dirs: string[] = [];
  let root = '';
  let opened: UploadFile | undefined;

  beforeEach(async () => {
    resetSecrets();
    root = await mkdtemp(join(tmpdir(), 'evidgate-upload-'));
    dirs.push(root);
    if (onRoot !== undefined) onRoot(root);
  });

  afterEach(async () => {
    if (opened !== undefined) {
      await closeUploadFile(opened);
      opened = undefined;
    }
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  /** Writes one file under the root and opens it through the guard (the command's path). */
  async function file(
    name: string,
    content: Buffer | string = 'trace-bytes',
  ): Promise<{ readonly opened: UploadFile; readonly sha256: string; readonly path: string }> {
    const path = join(root, name);
    await writeFile(path, content);
    const handle = await openUploadFile({ file: path, kind: 'trace', dirs: [root], cwd: root });
    opened = handle;
    return { opened: handle, sha256: await hashUploadFile(handle), path };
  }

  /** A journal the upload can resume from, in a producer directory beside the file. */
  async function journal(): Promise<UploadJournal> {
    const directory = await mkdtemp(join(tmpdir(), 'evidgate-upload-journal-'));
    dirs.push(directory);
    let tail: Promise<unknown> = Promise.resolve();
    return UploadJournal.open({
      producerDirectory: directory,
      withLock: <T>(fn: () => Promise<T>): Promise<T> => {
        const next = tail.then(fn, fn);
        tail = next.catch(() => undefined);
        return next;
      },
    });
  }

  /** The journal identity an upload of this file derives (contracts/cli.md §12.5). */
  function journalIdentity(
    digest: string,
    size: number,
    overrides: Partial<UploadIdentity> = {},
  ): UploadIdentity {
    return {
      file_sha256: digest,
      size,
      kind: 'trace',
      execution_key: executionKey,
      attempt_no: 1,
      media_type: 'application/zip',
      ...overrides,
    };
  }

  function request(
    input: { readonly opened: UploadFile; readonly sha256: string },
    harnessed: { readonly deps: UploadDeps },
    resume: UploadJournal | undefined,
    overrides: Partial<UploadRequest> = {},
  ) {
    return {
      config,
      token,
      runId,
      producerId,
      executionKey,
      attemptNo: 1,
      kind: 'trace' as const,
      mediaType: 'application/zip',
      file: input.opened,
      sha256: input.sha256,
      journal: resume,
      deadlineMs: 1_800_000,
      deps: harnessed.deps,
      ...overrides,
    };
  }

  return { dirs, file, journal, journalIdentity, request };
}
