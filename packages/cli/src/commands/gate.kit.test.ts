// Shared harness for the `evidgate gate` specs: a captured `Io`, a recording `fetch` stub, a
// virtual clock and temporary cache directories. The helpers are exercised here, so a broken
// seam fails in this file rather than as a puzzling assertion in a command spec.
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { main } from '../main.js';
import type { Io } from '../redact.js';

/** The run the gate asks about (openapi `RunId`, `format: uuid`). */
export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** A valid `project_id`, as a token-exchange answer carries it. */
export const projectId = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';
/** The project slug the command acts on. */
export const project = 'proj-1';
/** The dev bearer the `EVIDGATE_DEV_TOKEN` path returns (never on disk, never printed). */
export const devToken = 'dev-token-value';

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
}

/** A `fetch` stub that records every request and answers from `handler`. */
export function stubFetch(handler: (request: Request, index: number) => Response): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  const calls: StubCall[] = [];
  const fetchImpl: typeof fetch = (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push({ request });
    return Promise.resolve(handler(request, calls.length - 1));
  };
  return { fetchImpl, calls };
}

/** A virtual clock: sleeps are recorded and advance the same `now` the command reads. */
export interface Clock {
  readonly sleeps: number[];
  readonly now: () => number;
  readonly sleep: (milliseconds: number) => Promise<void>;
}

export function fakeClock(): Clock {
  let time = 0;
  const sleeps: number[] = [];
  return {
    sleeps,
    now: () => time,
    sleep: (milliseconds: number) => {
      sleeps.push(milliseconds);
      time += milliseconds;
      return Promise.resolve();
    },
  };
}

/** The seams a spec may override; the clock is virtual and the jitter is off by default. */
export interface Seams {
  readonly clock?: Clock;
  readonly random?: () => number;
}

export interface Invocation {
  readonly code: number;
  readonly clock: Clock;
}

/** `main()` with the stub transport and the virtual clock; no real time, no jitter. */
export async function invoke(
  argv: readonly string[],
  env: Readonly<Record<string, string | undefined>>,
  out: Captured,
  fetchImpl: typeof fetch,
  seams: Seams = {},
): Promise<Invocation> {
  const clock = seams.clock ?? fakeClock();
  const code = await main(argv, env, out.io, {
    fetch: fetchImpl,
    sleep: clock.sleep,
    now: clock.now,
    random: seams.random ?? (() => 0),
  });
  return { code, clock };
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

/** A 200 with the given decision body. */
export function decided(body: unknown): Response {
  return jsonResponse(200, body);
}

/**
 * The error envelope. `retryable` is part of the guarded shape, so it is always written:
 * `false` (a permanent answer) unless a test asks for a transient one.
 */
export function refusal(
  status: number,
  code: string,
  extra: { readonly message?: string; readonly retryable?: boolean } = {},
): Response {
  return jsonResponse(status, {
    error: {
      code,
      message: extra.message ?? 'the decision was refused',
      request_id: 'req-7',
      retryable: extra.retryable ?? false,
    },
  });
}

/** The `/v1/ci/token` answer that hands out `token`. */
export function exchangeBody(token: string): unknown {
  return { token, expires_at: '2030-01-01T00:00:00Z', project_id: projectId, permission_set: [] };
}

export function gateArgv(flags: ReadonlyArray<string>): string[] {
  return ['gate', ...flags];
}

export function sum(sleeps: readonly number[]): number {
  return sleeps.reduce((total, value) => total + value, 0);
}

export interface CacheDirs {
  readonly next: () => Promise<string>;
  readonly reset: () => Promise<void>;
}

/** Cache directories removed together, so a spec frees them in one `afterEach`. */
export function cacheDirs(): CacheDirs {
  const dirs: string[] = [];
  return {
    next: async () => {
      const dir = await mkdtemp(join(tmpdir(), 'evidgate-gate-'));
      dirs.push(dir);
      return dir;
    },
    reset: async () => {
      await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
    },
  };
}

/** The dev token path: no exchange, no cache write, no network beyond the stub. */
export async function devEnv(
  dirs: CacheDirs,
  extra: Record<string, string> = {},
): Promise<Record<string, string>> {
  return {
    EVIDGATE_API_URL: 'http://127.0.0.1:8080',
    EVIDGATE_PROJECT: project,
    EVIDGATE_ENV: 'dev',
    EVIDGATE_DEV_TOKEN: devToken,
    EVIDGATE_CACHE_DIR: await dirs.next(),
    ...extra,
  };
}

/**
 * An OIDC source: `getToken` cannot treat the far-future expiry as fresh on the virtual
 * clock, so every call exchanges and the stub can hand out a different token each time.
 */
export async function oidcEnv(
  dirs: CacheDirs,
  extra: Record<string, string> = {},
): Promise<Record<string, string>> {
  return {
    EVIDGATE_API_URL: 'https://api.evidgate.test',
    EVIDGATE_PROJECT: project,
    EVIDGATE_OIDC_TOKEN: 'jwt-value',
    EVIDGATE_CACHE_DIR: await dirs.next(),
    ...extra,
  };
}

/** An ingest source: the only one besides a fresh Actions ID token that may mint twice. */
export async function ingestEnv(
  dirs: CacheDirs,
  extra: Record<string, string> = {},
): Promise<Record<string, string>> {
  return {
    EVIDGATE_API_URL: 'https://api.evidgate.test',
    EVIDGATE_PROJECT: project,
    EVIDGATE_INGEST_TOKEN: 'ingest-value',
    EVIDGATE_CACHE_DIR: await dirs.next(),
    ...extra,
  };
}

describe('fake clock', () => {
  it('advances now by exactly what it slept and records every pause', async () => {
    const clock = fakeClock();
    expect(clock.now()).toBe(0);

    await clock.sleep(1_000);
    await clock.sleep(250);

    expect(clock.sleeps).toStrictEqual([1_000, 250]);
    expect(clock.now()).toBe(1_250);
  });

  it('does not move when a zero pause is requested', async () => {
    const clock = fakeClock();
    await clock.sleep(0);
    expect(clock.sleeps).toStrictEqual([0]);
    expect(clock.now()).toBe(0);
  });
});
