// The seams every `evidgate runs finalize` spec injects. Nothing here reaches a real network:
// `fetch` is a stub, the retry backoff neither waits nor jitters, and every temp root is
// registered for removal after its spec. No fixture carries a token literal: the dev token
// comes from the shared dev-exchange helper.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';

import { afterEach, beforeEach } from 'vitest';

import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';
import { defaultSpoolMaxBytes, ProducerSpool } from '../spool.js';

import { devExchangeAnswer, devToken, isDevExchange } from './devExchange.testing.js';

/** The project slug the command scopes the finalize to. */
export const project = 'proj-1';
/** The run the slot belongs to (openapi `RunId`, `format: uuid`). */
export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The manifest's deterministic producer id for shard 1 (`shard-<index>`). */
export const producerId = 'shard-1';
/** The 1-based manifest shard index of that producer. */
export const shard = 1;
/** A valid `manifest_hash` (`sha256:` + 64 lowercase hex); assembled, no digest literal. */
export const manifestHash = `sha256:${'a'.repeat(64)}`;
/** The namespace of the documented derived-key pre-image. */
export const keyVersion = 'evidgate.finalize-key.v1';

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

export interface StubCall {
  readonly request: Request;
  readonly body: Buffer;
}

/** A `fetch` stub that records every request (URL, headers, raw body) and answers from `handler`. */
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

/**
 * `main()` with the injected `fetch` stub — the only transport the specs use — and a
 * retry backoff that neither waits nor jitters, so a retried request stays instant.
 */
export function invoke(
  argv: ReadonlyArray<string>,
  env: Readonly<Record<string, string | undefined>>,
  out: Captured,
  fetchImpl: typeof fetch,
): Promise<number> {
  return main(argv, env, out.io, {
    fetch: fetchImpl,
    sleep: () => Promise.resolve(),
    random: () => 0,
  });
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

/** A 202 with no body (openapi `finalizeRun`: "Finalize accepted; reconcile async"). */
export function accepted(headers: Record<string, string> = {}): Response {
  return new Response(null, { status: 202, headers });
}

/** The 00 §9.2 error envelope the server answers a refused finalize with. */
export function refusal(status: number, code: string, requestId = 'req-3'): Response {
  return jsonResponse(status, {
    error: { code, message: 'the finalize was refused', request_id: requestId, retryable: false },
  });
}

/** The `project_id` the exchange echoes back (openapi `format: uuid`). */
export const exchangeProjectId = '5a0b1c2d-3e4f-4a5b-8c6d-7e8f9a0b1c2d';

/**
 * A stub whose handler answers the command's own request, never the token exchange: the dev
 * token is exchanged like an ingest credential, so the bearer the command sends is the token
 * the exchange hands out. The minted token expires 30 minutes out, inside the cache's 60 + 5
 * minute freshness window, and it is a different string from the credential sent in, so an
 * exchange that was skipped shows up as a `Bearer ${devToken}` mismatch.
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
  return JSON.parse(call?.body.toString('utf8') ?? '{}') as Record<string, unknown>;
}

/** A bare `runs finalize` argv; every case supplies the flags it needs. */
export function finalizeArgv(flags: ReadonlyArray<string>): string[] {
  return ['runs', 'finalize', ...flags];
}

/** The common `runs finalize` invocation: every required flag, plus `extra`. */
export function finalizeWith(extra: ReadonlyArray<string> = []): string[] {
  return finalizeArgv([
    '--run',
    runId,
    '--producer',
    producerId,
    '--shard',
    String(shard),
    '--manifest-hash',
    manifestHash,
    ...extra,
  ]);
}

/** A valid envelope line: the reader only requires `sequence` and an `event_id`. */
export function envelope(sequence: number): string {
  return JSON.stringify({ sequence, event_id: `evt-${sequence}`, type: 'test.finished' });
}

/** A pid that is certainly dead: a child process spawned and already waited for. */
export function exitedPid(): number {
  const child = spawnSync(process.execPath, ['-e', ''], { stdio: 'ignore' });
  if (child.pid === undefined) throw new Error('could not start the short-lived child');
  return child.pid;
}

/** The whole `{ pid, started_at }` record the reporter writes into `writer.lock`. */
export function writerLockText(pid: number): string {
  return `${JSON.stringify({ pid, started_at: '2026-01-01T00:00:00.000Z' })}\n`;
}

/** Every file under `directory` (recursive) with its bytes, for a byte-identity assertion. */
export async function snapshotTree(directory: string): Promise<Array<readonly [string, string]>> {
  const entries = await readdir(directory, { withFileTypes: true, recursive: true });
  const snapshot: Array<readonly [string, string]> = [];
  for (const entry of entries.filter((candidate) => candidate.isFile())) {
    const path = join(entry.parentPath, entry.name);
    snapshot.push([relative(directory, path), await readFile(path, 'utf8')]);
  }
  return snapshot.sort((a, b) => a[0].localeCompare(b[0]));
}

/**
 * Registers the per-test secret reset and temp-root cleanup the finalize specs need,
 * and returns the factories they drive the command with. Every temp root a spec makes
 * is removed after that spec.
 */
export function useFinalizeSpec() {
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
   * Writes a producer spool with `spooled` envelopes and acks the first `acked` of them,
   * so the cursor holds `{last_acked_sequence: acked, acked_count: acked}` (AC6).
   */
  async function writeSpool(o: { spooled: number; acked: number }): Promise<{
    readonly spoolDir: string;
    readonly producerDir: string;
  }> {
    const spoolDir = await tempDir();
    const spool = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId,
      maxBytes: defaultSpoolMaxBytes,
      createIfMissing: true,
    });
    if (o.spooled > 0) {
      const lines = Array.from({ length: o.spooled }, (_, index) => envelope(index + 1));
      await spool.append(lines);
    }
    if (o.acked > 0) {
      await spool.ack(o.acked, o.acked);
    }
    return { spoolDir, producerDir: join(spoolDir, runId, producerId) };
  }

  /**
   * The dev path: the dev token is exchanged at `/v1/ci/token` like an ingest credential,
   * the returned producer token is the bearer, and it is cached.
   */
  async function devEnv(spoolDir: string, extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_SPOOL_DIR: spoolDir,
      ...extra,
    };
  }

  /** An ingest source: `getToken()` would exchange at `/v1/ci/token` when the cache is cold. */
  async function ingestEnv(spoolDir: string, extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
    return {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: project,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
      EVIDGATE_SPOOL_DIR: spoolDir,
      ...extra,
    };
  }

  return { tempDir, writeSpool, devEnv, ingestEnv };
}
