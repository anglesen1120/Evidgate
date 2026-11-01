// QA (04.2.3 S54, written independently of the implementer): two clauses the existing
// suite leaves uncovered at command level. (1) A live `spool.lock` in a producer directory
// makes `events send` and `runs finalize` refuse with `spool_locked` (exit 3) and send
// nothing. (2) A connection that drops after the server accepted a batch retries the same
// bytes under the same Idempotency-Key, and the total is not double counted (04.2.3 §9).
import { gunzipSync } from 'node:zlib';
import { mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import type { ExchangeDeps } from '../auth/exchange.js';
import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';
import { ProducerSpool } from '../spool.js';

const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
const producer = 'pw-shard-0';
const devToken = ['dev', 'token', '0123456789'].join('-');
const apiUrl = 'http://127.0.0.1:8080';
const maxBytes = 268_435_456;
const manifestHash = `sha256:${'a'.repeat(64)}`;

/** The seams `main` forwards plus the send's own clock and jitter. */
interface TestDeps extends ExchangeDeps {
  readonly now?: () => number;
  readonly random?: () => number;
}

function invoke(
  argv: readonly string[],
  env: Readonly<Record<string, string | undefined>>,
  io: Io,
  deps: TestDeps,
): Promise<number> {
  return main(argv, env, io, deps);
}

interface Captured {
  readonly io: Io;
  readonly stdout: string[];
  readonly stderr: string[];
}

function capture(): Captured {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return {
    io: { stdout: (text) => stdout.push(text), stderr: (text) => stderr.push(text) },
    stdout,
    stderr,
  };
}

/** A virtual clock: `sleep` advances `now`, so retries cost no wall time. */
function virtualClock(): {
  readonly now: () => number;
  readonly sleep: (ms: number) => Promise<void>;
} {
  let current = Date.parse('2026-01-01T00:00:00.000Z');
  return {
    now: () => current,
    sleep: (ms: number) => {
      current += ms;
      return Promise.resolve();
    },
  };
}

interface RecordedCall {
  readonly key: string | null;
  readonly body: Buffer;
  readonly eventIds: string[];
}

/** The `project_id` the exchange echoes back (openapi `format: uuid`). */
const exchangeProjectId = '5a0b1c2d-3e4f-4a5b-8c6d-7e8f9a0b1c2d';

/**
 * The `/v1/ci/token` answer: the dev source exchanges its token like an ingest credential
 * before any project call. `expires_at` is half an hour past the suite's virtual clock start
 * (2026-01-01T00:00:00Z), so the entry reads fresh and no send re-exchanges mid-test.
 */
function devExchange(request: Request): Response | undefined {
  return new URL(request.url).pathname === '/v1/ci/token'
    ? new Response(
        JSON.stringify({
          token: devToken,
          expires_at: '2026-01-01T00:30:00.000Z',
          project_id: exchangeProjectId,
          permission_set: [],
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      )
    : undefined;
}

/**
 * A `fetch` stub that records each batch request and answers from `handler` (never the
 * network). The token exchange is answered here and not recorded: `calls` stays the list of
 * batch attempts the assertions count.
 */
function stubFetch(
  handler: (attempt: number, body: Buffer, eventIds: string[]) => Response | Promise<Response>,
): { readonly fetchImpl: typeof fetch; readonly calls: RecordedCall[] } {
  const calls: RecordedCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const exchange = devExchange(request);
    if (exchange !== undefined) {
      return exchange;
    }
    const wire = Buffer.from(await request.clone().arrayBuffer());
    // The batcher gzips a batch once compression pays; decode it before parsing, as eventsSend.test does.
    const body = request.headers.get('content-encoding') === 'gzip' ? gunzipSync(wire) : wire;
    const parsed = JSON.parse(body.toString('utf8')) as { events?: Array<{ event_id?: unknown }> };
    const eventIds = (parsed.events ?? []).map((event) => String(event.event_id));
    calls.push({ key: request.headers.get('idempotency-key'), body: wire, eventIds });
    return handler(calls.length, body, eventIds);
  };
  return { fetchImpl, calls };
}

/** A 200 that answers every event with `ack` (the events route's BatchAckResult shape). */
function ackAll(eventIds: string[], ack: 'accepted' | 'duplicate'): Response {
  const acks = eventIds.map((eventId, index) => ({ index, event_id: eventId, ack }));
  const accepted = ack === 'accepted' ? acks.length : 0;
  return new Response(JSON.stringify({ acks, accepted, duplicates: acks.length - accepted }), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

/** One compact 00 §9.4 envelope, a single line. */
function envelopeText(sequence: number): string {
  return JSON.stringify({
    schema_version: 1,
    event_id: `00000000-0000-4000-8000-${sequence.toString(16).padStart(12, '0')}`,
    producer_id: producer,
    sequence,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'run.log',
    occurred_at: '2026-01-01T00:00:00Z',
  });
}

/** Seeds a producer spool with `count` unacked envelopes; returns its producer directory. */
async function seed(spoolDir: string, count: number): Promise<string> {
  const spool = await ProducerSpool.open({
    spoolDir,
    runId,
    producerId: producer,
    maxBytes,
    createIfMissing: true,
  });
  await spool.append(Array.from({ length: count }, (_, index) => envelopeText(index + 1)));
  return join(spoolDir, runId, producer);
}

/** Writes a `spool.lock` naming this (live) process, the way a concurrent send would. */
async function holdLock(producerDir: string): Promise<void> {
  const content = `${JSON.stringify({ pid: process.pid, started_at: new Date().toISOString() })}\n`;
  await writeFile(join(producerDir, 'spool.lock'), content, { mode: 0o600 });
}

function baseEnv(spoolDir: string): Record<string, string> {
  return {
    EVIDGATE_API_URL: apiUrl,
    EVIDGATE_PROJECT: 'proj-1',
    EVIDGATE_ENV: 'dev',
    EVIDGATE_DEV_TOKEN: devToken,
    EVIDGATE_SPOOL_DIR: spoolDir,
    // The dev source exchanges and caches a producer token, so the cache stays in the
    // per-test temporary directory instead of the developer's real cache.
    EVIDGATE_CACHE_DIR: join(spoolDir, 'cache'),
  };
}

describe('QA S54: live spool.lock and dropped ack at command level', () => {
  const directories: string[] = [];

  async function tempDir(): Promise<string> {
    const directory = await mkdtemp(join(tmpdir(), 'evidgate-qa-s54-'));
    directories.push(directory);
    return directory;
  }

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    for (const directory of directories.splice(0)) {
      await rm(directory, { recursive: true, force: true });
    }
  });

  it('events send refuses spool_locked (exit 3) and sends nothing while a live process holds the lock', async () => {
    const spoolDir = await tempDir();
    const producerDir = await seed(spoolDir, 3);
    await holdLock(producerDir);
    const stub = stubFetch(() => ackAll([], 'accepted'));
    const out = capture();
    const clock = virtualClock();

    const code = await invoke(['events', 'send', '--run', runId], baseEnv(spoolDir), out.io, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(out.stderr.join('')).toContain('spool_locked');
    // The live holder's lock is left in place, not taken over.
    expect(await readFile(join(producerDir, 'spool.lock'), 'utf8')).toContain(String(process.pid));
    const spool = await ProducerSpool.open({ spoolDir, runId, producerId: producer, maxBytes });
    expect((await spool.cursor()).nextUnackedSequence).toBe(1);
  });

  it('runs finalize refuses spool_locked (exit 3) and posts nothing while a live process holds the lock', async () => {
    const spoolDir = await tempDir();
    const producerDir = await seed(spoolDir, 3);
    await holdLock(producerDir);
    const stub = stubFetch(() => new Response(null, { status: 202 }));
    const out = capture();

    const code = await main(
      [
        'runs',
        'finalize',
        '--run',
        runId,
        '--producer',
        producer,
        '--shard',
        '1',
        '--manifest-hash',
        manifestHash,
      ],
      baseEnv(spoolDir),
      out.io,
      { fetch: stub.fetchImpl },
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(out.stderr.join('')).toContain('spool_locked');
    // The spool is kept: a refused finalize must not purge anything.
    const files = await readdir(producerDir);
    expect(files).toContain('spool.lock');
    expect(files.some((name) => name.endsWith('.jsonl'))).toBe(true);
  });

  it('a connection dropped after the server accepted a batch retries the same bytes and key, and counts each event once', async () => {
    const spoolDir = await tempDir();
    await seed(spoolDir, 5);
    // Attempt 1: the server takes the batch but the connection dies before the ack arrives.
    // Attempt 2: the replay is answered `duplicate` for every event.
    const stub = stubFetch((attempt, _body, eventIds) => {
      if (attempt === 1) {
        throw new TypeError('fetch failed');
      }
      return ackAll(eventIds, 'duplicate');
    });
    const out = capture();
    const clock = virtualClock();

    const code = await invoke(['events', 'send', '--run', runId], baseEnv(spoolDir), out.io, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(0);
    expect(stub.calls).toHaveLength(2);
    const [first, second] = stub.calls;
    expect(first?.key).toBeTruthy();
    expect(second?.key).toBe(first?.key);
    expect(second?.body.equals(first?.body ?? Buffer.alloc(0))).toBe(true);
    expect(second?.eventIds).toHaveLength(5);
    // The final tally is the server's answer to the replay: nothing counted twice.
    expect(out.stdout.join('')).toContain('accepted=0 duplicate=5');
    const spool = await ProducerSpool.open({ spoolDir, runId, producerId: producer, maxBytes });
    expect((await spool.cursor()).nextUnackedSequence).toBe(6);
  });
});
