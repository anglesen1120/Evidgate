// The seams every `evidgate runs create` spec injects. Nothing here reaches a real network:
// `fetch` is a stub, the dev token is exchanged in front of the command's own requests, and
// every temp root is registered for removal after its spec. No fixture carries a token
// literal: the dev token comes from the shared dev-exchange helper.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach } from 'vitest';

import { main } from '../main.js';
import { resetSecrets } from '../redact.js';

import {
  capture,
  jsonResponse,
  stubFetch as sharedStubFetch,
  type Captured,
  type StubCall,
} from './cliSpec.testing.js';
import { devExchangeAnswer, devToken, isDevExchange } from './devExchange.testing.js';

export { capture, jsonResponse };
export type { Captured, StubCall };

/** The project slug the command scopes the create to. */
export const project = 'proj-1';
/** A valid `manifest_id` (openapi `format: uuid`). */
export const manifestId = '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f';
/** A second UUID, to prove the derived key moves with `manifest_id` (AC4). */
export const otherManifestId = '1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f';
/** A valid `manifest_hash` (`sha256:` + 64 hex); assembled so no digest literal is stored. */
export const manifestHash = `sha256:${'a'.repeat(64)}`;
/** A second digest for the AC4 difference checks. */
export const otherManifestHash = `sha256:${'b'.repeat(64)}`;
/** A digest one character short of the `sha256:<64 hex>` form. */
export const shortHash = `sha256:${'a'.repeat(63)}`;
/** An uppercase digest, refused because the server's form is lowercase. */
export const upperHash = `sha256:${'A'.repeat(64)}`;
/** The `run_id` the server returns (openapi `RunCreated`). */
export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The namespace of the documented derived-key pre-image (AC4). */
export const keyVersion = 'evidgate.run-key.v1';

/**
 * A `fetch` stub that records every request (URL, headers, raw body) and answers from `handler`.
 */
export function stubFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  return sharedStubFetch((call) => handler(call.request));
}

/** `main()` with the injected `fetch` stub — the only transport the specs use. */
export function invoke(
  argv: ReadonlyArray<string>,
  env: Readonly<Record<string, string | undefined>>,
  out: Captured,
  fetchImpl: typeof fetch,
): Promise<number> {
  return main(argv, env, out.io, { fetch: fetchImpl });
}

/** The canonical 201 body (openapi `RunCreated`), with per-case overrides. */
export function created(overrides: Record<string, unknown> = {}): Response {
  return jsonResponse(201, { run_id: runId, state: 'queued', unverified_ref: false, ...overrides });
}

/** The `project_id` the exchange echoes back (openapi `format: uuid`). */
export const exchangeProjectId = '5a0b1c2d-3e4f-4a5b-8c6d-7e8f9a0b1c2d';

/**
 * A stub whose handler answers the command's own request, never the token exchange. The minted
 * token expires 30 minutes out, inside the cache's 60 + 5 minute freshness window, so a second
 * invocation reuses the cached one instead of exchanging again.
 */
export function stubDevFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  return stubFetch(
    (request) =>
      devExchangeAnswer(request, {
        projectId: exchangeProjectId,
        expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(),
      }) ?? handler(request),
  );
}

/**
 * The command's own requests: everything but the `/v1/ci/token` exchange, so a count or an
 * index means the same thing it did before the dev source gained a token exchange.
 */
export function apiCalls(stub: { readonly calls: StubCall[] }): StubCall[] {
  return stub.calls.filter((call) => !isDevExchange(call.request));
}

/** The JSON body the stub recorded for `call`. */
export function sentBody(call: StubCall | undefined): Record<string, unknown> {
  return JSON.parse(call?.body.toString('utf8') ?? '') as Record<string, unknown>;
}

/** A bare `runs create` argv, for the cases that replace a required flag. */
export function runsCreateArgv(flags: ReadonlyArray<string>): string[] {
  return ['runs', 'create', ...flags];
}

/** `runs create` with both required flags, plus `extra` (the common case). */
export function createArgv(extra: ReadonlyArray<string> = []): string[] {
  return runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', manifestHash, ...extra]);
}

/**
 * Registers the per-test secret reset and temp-root cleanup the create specs need, and
 * returns the environment factories they drive the command with. Every temp root a spec
 * makes is removed after that spec.
 */
export function useRunsCreateSpec() {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function tempDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    dirs.push(dir);
    return dir;
  }

  /**
   * The dev path: the dev token is exchanged at `/v1/ci/token` like an ingest credential,
   * the returned producer token is the bearer, and it is cached (the Api accepts only
   * exchanged tokens).
   */
  async function devEnv(extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
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
  async function ingestEnv(extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
    return {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: project,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
      ...extra,
    };
  }

  return { tempDir, devEnv, ingestEnv };
}
