// The fixtures the `artifacts upload` specs share: a valid run's ids, the captured-io
// record, the `fetch` stub that answers the dev-token exchange apart from the artifact
// calls, the JSON answer builder, the happy-path responder, the argv builder and the
// per-test temp root the file, the token cache and the spool live in. Every spec file calls
// `useArtifactsUploadSpec()` for itself, so each gets its own hooks, root and cleanup and
// the split files start from the same baseline. Every spec drives the command through
// `main()` with an injected transport, so none of these fixtures touch the network
// themselves.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach } from 'vitest';

import { resetSecrets } from '../redact.js';

import {
  capture,
  jsonResponse,
  stubFetch as sharedStubFetch,
  type Captured,
  type StubCall,
  type StubHarness,
} from './cliSpec.testing.js';
import { devToken, exchangeBody, isDevExchange, mintedToken } from './devExchange.testing.js';

/** The `project_id` a token-exchange answer carries (openapi `format: uuid`). */
const projectUuid = '5c8d9e0f-1a2b-4c3d-8e9f-0a1b2c3d4e5f';
/** A valid `run_id` (openapi `format: uuid`). */
export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** A valid `execution_key` (64 lowercase hex). */
export const executionKey = 'b4'.repeat(32);
/** A valid `artifact_id` (openapi `format: uuid`). */
export const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
/** The project slug the command scopes the upload to. */
export const project = 'proj-1';

export { capture, jsonResponse };
export type { Captured, StubCall };

export type Responder = (call: StubCall) => Response | Promise<Response>;

/**
 * A fetch stub that routes the calls of an upload and records them. A dev token is an ingest
 * credential now, so `POST /v1/ci/token` is answered here and kept in `exchanges`: `calls`
 * then holds exactly the artifact route's own requests, which is what each case asserts.
 */
export function stubFetch(respond: Responder): StubHarness {
  return sharedStubFetch((call) => respond(call), {
    exchange: (call) =>
      isDevExchange(call)
        ? jsonResponse(200, exchangeBody(mintedToken, { projectId: projectUuid }))
        : undefined,
  });
}

/**
 * Registers the per-test temp root, its cleanup hook and the `resetSecrets()` reset, and
 * returns the factories the specs drive the command with.
 */
export function useArtifactsUploadSpec() {
  const dirs: string[] = [];
  let root: string;

  beforeEach(async () => {
    resetSecrets();
    root = await mkdtemp(join(tmpdir(), 'evidgate-artifacts-'));
    dirs.push(root);
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  /** Writes a file under the run's output root and returns its path. */
  async function evidence(
    name = 'trace.zip',
    content: Buffer | string = 'trace-bytes',
  ): Promise<string> {
    const path = join(root, name);
    await mkdir(join(path, '..'), { recursive: true });
    await writeFile(path, content);
    return path;
  }

  function devEnv(extra: Record<string, string> = {}): Record<string, string> {
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      // The exchange writes its `ci_producer` token cache, so it is kept in the temp root.
      EVIDGATE_CACHE_DIR: root,
      ...extra,
    };
  }

  /** The happy-path responder: `upload-url` 201, PUT 200, complete 200 ready. */
  function happyStore(bytes = 11, sha256 = `sha256:${'a'.repeat(64)}`): Responder {
    const base = Buffer.from('trace-bytes');
    return (call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return jsonResponse(200, {
          artifact_id: artifactId,
          state: 'ready',
          bytes,
          scan_state: 'pending',
          failure_code: null,
        });
      }
      return jsonResponse(201, {
        artifact_id: artifactId,
        object_key: 'pending/org/x',
        upload_url: 'http://127.0.0.1:9/pending/x?X-Amz-Signature=abc',
        required_headers: {
          'Content-Length': String(base.byteLength),
          'x-amz-checksum-sha256': createHash('sha256').update(base).digest('base64'),
        },
        expires_at: '2026-10-24T23:59:59.000Z',
        sha256,
      });
    };
  }

  function argv(file: string, extra: string[] = []): string[] {
    return [
      'artifacts',
      'upload',
      '--run',
      runId,
      '--file',
      file,
      '--kind',
      'trace',
      '--execution-key',
      executionKey,
      '--dir',
      root,
      ...extra,
    ];
  }

  return { dirs, rootDir: () => root, evidence, devEnv, happyStore, argv };
}
