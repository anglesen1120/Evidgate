// The seams every `evidgate events send` spec injects. Nothing here waits on a real clock or
// touches the network: `fetch` is a stub, `sleep` advances a virtual clock (`now` and
// `random` are fixed) and `stdin` replaces `process.stdin` for `--from -`. No fixture carries
// a token literal: the dev token comes from the shared dev-exchange helper, and the specs
// prove it reaches neither the spool nor a writer.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { mkdtempSync } from 'node:fs';
import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { main } from '../main.js';
import { ProducerSpool } from '../spool.js';

import {
  capture,
  jsonResponse,
  stubFetch as sharedStubFetch,
  type Captured,
  type StubCall,
  type StubHarness,
} from './cliSpec.testing.js';
import { devExchangeAnswer, devToken } from './devExchange.testing.js';

import type { ExchangeDeps } from '../auth/exchange.js';

/** The run every case spools for (openapi `RunId`, `format: uuid`). */
export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The producer whose directory the specs drain (a safe spool segment). */
export const producer = 'pw-shard-0';
/** A second producer, to prove the intake groups by the envelope's own `producer_id`. */
export const otherProducer = 'pw-shard-1';
/** A loopback API URL, which the dev-token path requires. */
export const apiUrl = 'http://127.0.0.1:8080';
/** The default run-directory cap, used by the specs that do not lower it. */
export const defaultMaxBytes = 268_435_456;

/** The seams `main` forwards plus the send's own (all optional, as in the command). */
export interface TestDeps extends ExchangeDeps {
  readonly now?: () => number;
  readonly random?: () => number;
  readonly stdin?: AsyncIterable<Uint8Array>;
}

export { capture, jsonResponse };
export type { Captured, StubCall };

/**
 * A virtual clock: `sleep` advances the same clock `now` reads, so an outage of ten
 * virtual minutes costs no wall time and the deadline is exercised deterministically.
 */
export function virtualClock(): {
  readonly now: () => number;
  readonly sleep: (ms: number) => Promise<void>;
  readonly sleeps: number[];
} {
  let current = Date.parse('2026-01-01T00:00:00.000Z');
  const sleeps: number[] = [];
  return {
    now: () => current,
    sleep: (ms: number) => {
      sleeps.push(ms);
      current += ms;
      return Promise.resolve();
    },
    sleeps,
  };
}

/**
 * A `fetch` stub that parses each body and answers from `handler` (never the network).
 * `EVIDGATE_DEV_TOKEN` is an ingest credential, so a run exchanges it at `POST /v1/ci/token`
 * before its first batch: the stub answers that call itself, handing `mintedToken` back, and
 * keeps it in `exchanges`, so `calls` holds only the send's own requests.
 */
export function stubFetch(handler: (call: StubCall) => Response | Promise<Response>): StubHarness {
  return sharedStubFetch((call) => handler(call), { exchange: (call) => devExchangeAnswer(call) });
}

/** The 200 the events route returns, with one ack per event (order = request order). */
export function acked(
  call: StubCall,
  statuses?: ReadonlyArray<'accepted' | 'duplicate'>,
): Response {
  const acks = call.events.map((event, index) => ({
    index,
    event_id: String(event['event_id']),
    ack: statuses?.[index] ?? 'accepted',
  }));
  const accepted = acks.filter((ack) => ack.ack === 'accepted').length;
  return jsonResponse(
    200,
    { acks, accepted, duplicates: acks.length - accepted },
    { 'x-request-id': 'req-ok' },
  );
}

/** A 00 §9.2 error envelope (the only shape `serverErrorFromResponse` trusts). */
export function errorResponse(status: number, code: string, requestId: string): Response {
  return jsonResponse(status, {
    error: {
      code,
      message: `the server refused the batch (${code})`,
      request_id: requestId,
      retryable: status >= 500,
    },
  });
}

/** A valid v4-shaped `event_id`, distinct per sequence. */
export function eventIdFor(sequence: number): string {
  return `00000000-0000-4000-8000-${sequence.toString(16).padStart(12, '0')}`;
}

export interface EnvelopeOverrides {
  readonly producerId?: string;
  readonly eventId?: string;
  readonly runId?: string;
  readonly type?: string;
  readonly pad?: number;
}

/** One compact 00 §9.4 envelope (a single line, no newline). */
export function envelopeText(sequence: number, o: EnvelopeOverrides = {}): string {
  const envelope: Record<string, unknown> = {
    schema_version: 1,
    event_id: o.eventId ?? eventIdFor(sequence),
    producer_id: o.producerId ?? producer,
    sequence,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: o.type ?? 'run.log',
    occurred_at: '2026-01-01T00:00:00Z',
  };
  if (o.runId !== undefined) {
    envelope['run_id'] = o.runId;
  }
  if (o.pad !== undefined) {
    envelope['ext'] = { schema: 'x', pad: 'y'.repeat(o.pad) };
  }
  return JSON.stringify(envelope);
}

/** The `events send` argv for one run, plus `extra`. */
export function sendArgv(extra: ReadonlyArray<string> = []): string[] {
  return ['events', 'send', '--run', runId, ...extra];
}

/** Cache dirs the dev exchange writes into; each is removed with the test that made it. */
const tokenCacheDirs: string[] = [];

/**
 * The dev run caches its exchanged `ci_producer` token, so every case needs a private
 * `EVIDGATE_CACHE_DIR`: without one the cache would land in the developer's `$HOME`.
 */
function isolatedCacheDir(): string {
  const directory = mkdtempSync(join(tmpdir(), 'evidgate-events-cache-'));
  tokenCacheDirs.push(directory);
  return directory;
}

/** The environment every case starts from: dev token, loopback API, spool root. */
export function baseEnv(
  spoolDir: string,
  extra: Readonly<Record<string, string>> = {},
): Record<string, string> {
  return {
    EVIDGATE_API_URL: apiUrl,
    EVIDGATE_PROJECT: 'proj-1',
    EVIDGATE_ENV: 'dev',
    EVIDGATE_DEV_TOKEN: devToken,
    EVIDGATE_CACHE_DIR: isolatedCacheDir(),
    EVIDGATE_SPOOL_DIR: spoolDir,
    ...extra,
  };
}

export function invoke(
  argv: ReadonlyArray<string>,
  env: Readonly<Record<string, string | undefined>>,
  out: Captured,
  deps: TestDeps,
): Promise<number> {
  return main(argv, env, out.io, deps);
}

/** Opens the producer spool for assertions (never takes the lock: it only reads). */
export function openSpool(spoolDir: string, producerId = producer) {
  return ProducerSpool.open({ spoolDir, runId, producerId, maxBytes: defaultMaxBytes });
}

/** Every regular file under `directory`, recursively. */
export async function listFiles(directory: string): Promise<string[]> {
  const entries = await readdir(directory, { withFileTypes: true, recursive: true });
  return entries
    .filter((entry) => entry.isFile())
    .map((entry) => join(entry.parentPath, entry.name));
}

/** The run-local directories the cases made; `cleanupSpecState` removes them all. */
const directories: string[] = [];

/** A run-local directory, removed with the case that made it. */
export async function tempDir(): Promise<string> {
  const directory = await mkdtemp(join(tmpdir(), 'evidgate-events-send-'));
  directories.push(directory);
  return directory;
}

/**
 * Removes every directory a case created — spool roots and the dev exchange's private cache
 * dirs — so no case can observe another's files.
 */
export async function cleanupSpecState(): Promise<void> {
  for (const directory of directories.splice(0)) {
    await rm(directory, { recursive: true, force: true });
  }
  for (const directory of tokenCacheDirs.splice(0)) {
    await rm(directory, { recursive: true, force: true });
  }
}
