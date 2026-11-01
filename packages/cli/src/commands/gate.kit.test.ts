// Shared harness for the `evidgate gate` specs: a captured `Io`, a recording `fetch` stub, a
// virtual clock and temporary cache directories. The helpers are exercised here, so a broken
// seam fails in this file rather than as a puzzling assertion in a command spec.
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';

import { devStubFetch, devToken, mintedToken } from './devExchange.testing.js';
import type { GateOutcome } from './gateDecision.js';

export { withTokenExchange } from './devExchange.testing.js';

/** The run the gate asks about (openapi `RunId`, `format: uuid`). */
export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The project slug the command acts on. */
export const project = 'proj-1';

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
 * A `Decision` body for a run with no live evaluation: `status: pending` and every
 * evaluation member `null`, `missing` included (never `[]`).
 */
export function pending(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    status: 'pending',
    decision: null,
    evaluation_id: null,
    evidence_revision: null,
    completeness: null,
    outcome: null,
    missing: null,
    reasons: null,
    policy_hash: null,
    manifest_hash: null,
    evaluator_version: null,
    mode: null,
    url: null,
    ...overrides,
  };
}

/**
 * An evaluated `Decision` body; `overrides` carries the verdict and members under test. The
 * schema narrows `missing` to an array here, so the default is `[]`, never the pending `null`.
 */
/** A parsed pending decision: every evaluation member `null`, the shape `status: pending` gives. */
export const pendingOutcome: GateOutcome = {
  status: 'pending',
  decision: null,
  completeness: null,
  evaluationId: null,
  evidenceRevision: null,
  outcome: null,
  missing: null,
  reasons: null,
  policyHash: null,
  manifestHash: null,
  evaluatorVersion: null,
  mode: null,
  url: null,
};

export function evaluated(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return pending({ status: 'evaluated', missing: [], ...overrides });
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

/**
 * The dev token path: `EVIDGATE_DEV_TOKEN` is exchanged for a `ci_producer` token (the Api
 * accepts only exchanged ones), so `devStubFetch` answers that call; its far-future expiry is
 * never fresh, so each invocation exchanges again instead of reusing a cache entry.
 */
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
): Promise<Record<string, string> & { EVIDGATE_CACHE_DIR: string }> {
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

// The seam every dev-token spec relies on: `devStubFetch` answers the exchange with
// `mintedToken` and the command's own request carries that bearer, not the dev credential.
describe('dev token exchange', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  it('hands the command the minted token, never the dev credential', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => decided(evaluated({ decision: 'PASS' })));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    expect(stub.exchanges).toHaveLength(1);
    const exchange = stub.exchanges[0];
    expect(JSON.parse((await exchange?.request.clone().text()) ?? '{}')).toStrictEqual({
      ingest_token: devToken,
    });
    expect(stub.calls[0]?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(out.stdout.join('')).not.toContain(devToken);
    expect(out.stdout.join('')).not.toContain(mintedToken);
  });
});
