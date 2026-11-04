// The fixtures the `manifests upload` specs share: a valid answer's ids, the captured-io
// record, the `fetch` stub that keeps the raw request body, the JSON answer builder and the
// two token-source environments. Every spec drives the command through `main()` with an
// injected transport, so none of these fixtures touch the network themselves.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import type { Io } from '../redact.js';

import { devToken } from './devExchange.testing.js';

/** The project slug the command scopes the upload to. */
export const project = 'proj-1';
/** A valid `manifest_id` (openapi `format: uuid`). */
export const manifestId = '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f';
/** A valid `manifest_hash` (`sha256:` + 64 hex). */
export const manifestHash = `sha256:${'a'.repeat(64)}`;
/** The exchange route a dev run posts to before its own request. */
export const exchangePath = '/v1/ci/token';

/** The streams a run wrote to, plus the arrays behind them. */
export interface Captured {
  readonly io: Io;
  readonly stdout: string[];
  readonly stderr: string[];
}

export function capture(): Captured {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return {
    io: { stdout: (text) => stdout.push(text), stderr: (text) => stderr.push(text) },
    stdout,
    stderr,
  };
}

/** One recorded request: the `Request` and the raw bytes it carried. */
export interface StubCall {
  readonly request: Request;
  readonly body: Buffer;
}

/**
 * A `fetch` stub that records every request (URL, headers, raw body) and answers from
 * `handler`. It answers nothing by itself: a dev-token run needs `devStubFetch`, because
 * `getToken` exchanges the dev token before the command's own request.
 */
export function stubFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  const calls: StubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push({ request, body: Buffer.from(await request.clone().arrayBuffer()) });
    return handler(request);
  };
  return { fetchImpl, calls };
}

export function jsonResponse(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** The 201 answer a valid upload receives: both ids the command prints. */
export const createdManifest = () =>
  jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash });

/** An error envelope as the command reads it (contracts/cli.md §5). */
export function errorEnvelope(code: string, message: string, extra: Record<string, unknown> = {}) {
  return { error: { code, message, request_id: 'req-1', retryable: false, ...extra } };
}

/**
 * The dev path: the token is a seeded ingest credential, so every run exchanges it at
 * `POST /v1/ci/token` (and caches the result) before the upload.
 */
export function devUploadEnv(
  cacheDir: string,
  extra: Record<string, string> = {},
): Record<string, string> {
  return {
    EVIDGATE_API_URL: 'http://127.0.0.1:8080',
    EVIDGATE_PROJECT: project,
    EVIDGATE_ENV: 'dev',
    EVIDGATE_DEV_TOKEN: devToken,
    EVIDGATE_CACHE_DIR: cacheDir,
    ...extra,
  };
}

/** An ingest source: `getToken()` would exchange at `/v1/ci/token` when the cache is cold. */
export function ingestUploadEnv(
  cacheDir: string,
  extra: Record<string, string> = {},
): Record<string, string> {
  return {
    EVIDGATE_API_URL: 'https://api.evidgate.test',
    EVIDGATE_PROJECT: project,
    EVIDGATE_CACHE_DIR: cacheDir,
    EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
    ...extra,
  };
}
