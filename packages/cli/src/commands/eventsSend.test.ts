// 04.2.3-T1.4: `evidgate events send` on the binary — intake (`--from <file|->`), the
// deadline-bounded drain, resume after a process death and the failure surface
// (04.2.3 §2 AC1–AC4, §8, §9; contracts/cli.md §11).
//
// Every seam is injected, so nothing here waits on a real clock or touches the network:
// `fetch` is a stub, `sleep` advances a virtual clock (AC1's ten-minute outage is 120
// virtual waits), `now`/`random` are fixed, and `stdin` replaces `process.stdin` for
// `--from -`. No fixture carries a token literal: the dev token is assembled here and the
// assertions prove it reaches neither the spool nor a writer.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync } from 'node:fs';
import { mkdir, mkdtemp, readdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import { gunzipSync } from 'node:zlib';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';
import { ProducerSpool, listProducers, maxSpoolLineBytes } from '../spool.js';
import { writerLockFileName } from '../spoolLock.js';

import type { ExchangeDeps } from '../auth/exchange.js';

/** The run every case spools for (openapi `RunId`, `format: uuid`). */
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The producer whose directory the specs drain (a safe spool segment). */
const producer = 'pw-shard-0';
/** A second producer, to prove the intake groups by the envelope's own `producer_id`. */
const otherProducer = 'pw-shard-1';
/** A dev token, assembled so no token-looking literal is stored (T78). */
const devToken = ['dev', 'token', '0123456789'].join('-');
/** A loopback API URL, which the dev-token path requires. */
const apiUrl = 'http://127.0.0.1:8080';
/** The exchange route a dev run posts to before its first batch. */
const exchangePath = '/v1/ci/token';
/** The default run-directory cap, used by the specs that do not lower it. */
const defaultMaxBytes = 268_435_456;

/** The seams `main` forwards plus the send's own (all optional, as in the command). */
interface TestDeps extends ExchangeDeps {
  readonly now?: () => number;
  readonly random?: () => number;
  readonly stdin?: AsyncIterable<Uint8Array>;
}

interface Captured {
  readonly io: Io;
  readonly stdout: string[];
  readonly stderr: string[];
}

/**
 * `Promise.withResolvers` is ES2024 and the workspace targets ES2022, so the specs carry
 * their own two-line equivalent (the gates below must resolve from inside a fetch stub).
 */
function deferred(): { readonly promise: Promise<void>; readonly resolve: () => void } {
  let resolve: () => void = () => undefined;
  const promise = new Promise<void>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
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

/**
 * A virtual clock: `sleep` advances the same clock `now` reads, so an outage of ten
 * virtual minutes costs no wall time and the deadline is exercised deterministically.
 */
function virtualClock(): {
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

/** One recorded request: the headers the send chose and the body bytes it sent. */
interface StubCall {
  readonly request: Request;
  /** The bytes the sender actually put on the wire (gzipped when it compressed). */
  readonly wire: Buffer;
  /** The decoded JSON body bytes (equal to `wire` unless it was compressed). */
  readonly body: Buffer;
  readonly key: string | null;
  readonly events: ReadonlyArray<Record<string, unknown>>;
}

/**
 * A `fetch` stub that parses each body and answers from `handler` (never the network).
 * `EVIDGATE_DEV_TOKEN` is an ingest credential, so a run exchanges it at `POST /v1/ci/token`
 * before its first batch: the stub answers that call itself, handing `devToken` back, and
 * keeps it in `exchanges`, so `calls` holds only the send's own requests.
 */
function stubFetch(handler: (call: StubCall) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
  readonly exchanges: StubCall[];
} {
  const calls: StubCall[] = [];
  const exchanges: StubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const raw = Buffer.from(await request.clone().arrayBuffer());
    // `packBatches` switches to `Content-Encoding: gzip` once compression saves > 1 KiB,
    // so the recorded body is the decoded JSON (the bytes on the wire stay in `wire`).
    const wire = raw;
    const body = request.headers.get('content-encoding') === 'gzip' ? gunzipSync(raw) : raw;
    const parsed = JSON.parse(body.toString('utf8')) as { events?: unknown };
    const events = Array.isArray(parsed.events)
      ? parsed.events.filter(
          (event): event is Record<string, unknown> =>
            typeof event === 'object' && event !== null && !Array.isArray(event),
        )
      : [];
    const call: StubCall = {
      request,
      wire,
      body,
      key: request.headers.get('idempotency-key'),
      events,
    };
    if (new URL(request.url).pathname === exchangePath) {
      exchanges.push(call);
      return jsonResponse(200, {
        token: devToken,
        expires_at: '2030-01-01T00:00:00Z',
        project_id: '3f2504e0-4f89-41d3-9a0c-0305e82c3301',
        permission_set: [],
      });
    }
    calls.push(call);
    return handler(call);
  };
  return { fetchImpl, calls, exchanges };
}

function jsonResponse(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** The 200 the events route returns, with one ack per event (order = request order). */
function acked(call: StubCall, statuses?: ReadonlyArray<'accepted' | 'duplicate'>): Response {
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
function errorResponse(status: number, code: string, requestId: string): Response {
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
function eventIdFor(sequence: number): string {
  return `00000000-0000-4000-8000-${sequence.toString(16).padStart(12, '0')}`;
}

interface EnvelopeOverrides {
  readonly producerId?: string;
  readonly eventId?: string;
  readonly runId?: string;
  readonly type?: string;
  readonly pad?: number;
}

/** One compact 00 §9.4 envelope (a single line, no newline). */
function envelopeText(sequence: number, o: EnvelopeOverrides = {}): string {
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
function sendArgv(extra: ReadonlyArray<string> = []): string[] {
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
function baseEnv(
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

function invoke(
  argv: ReadonlyArray<string>,
  env: Readonly<Record<string, string | undefined>>,
  out: Captured,
  deps: TestDeps,
): Promise<number> {
  return main(argv, env, out.io, deps);
}

/** Opens the producer spool for assertions (never takes the lock: it only reads). */
function openSpool(spoolDir: string, producerId = producer) {
  return ProducerSpool.open({ spoolDir, runId, producerId, maxBytes: defaultMaxBytes });
}

/** Every regular file under `directory`, recursively. */
async function listFiles(directory: string): Promise<string[]> {
  const entries = await readdir(directory, { withFileTypes: true, recursive: true });
  return entries
    .filter((entry) => entry.isFile())
    .map((entry) => join(entry.parentPath, entry.name));
}

/** Every file under `directory` with its bytes, for a byte-identity assertion. */
async function snapshotTree(directory: string): Promise<Array<readonly [string, string]>> {
  const files = (await listFiles(directory)).sort();
  const snapshot: Array<readonly [string, string]> = [];
  for (const path of files) {
    snapshot.push([relative(directory, path), await readFile(path, 'utf8')]);
  }
  return snapshot;
}

/** A pid that is certainly dead: a child process spawned and already waited for. */
function exitedPid(): number {
  const child = spawnSync(process.execPath, ['-e', ''], { stdio: 'ignore' });
  if (child.pid === undefined) throw new Error('could not start the short-lived child');
  return child.pid;
}

/** The whole `{ pid, started_at }` record the reporter writes into `writer.lock`. */
function writerLockText(pid: number): string {
  return `${JSON.stringify({ pid, started_at: '2026-01-01T00:00:00.000Z' })}\n`;
}

describe('events send', () => {
  const directories: string[] = [];

  async function tempDir(): Promise<string> {
    const directory = await mkdtemp(join(tmpdir(), 'evidgate-events-send-'));
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
    for (const directory of tokenCacheDirs.splice(0)) {
      await rm(directory, { recursive: true, force: true });
    }
  });

  it('spools --from lines by producer, delivers them and reports per producer', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(
      file,
      [envelopeText(1), envelopeText(1, { producerId: otherProducer }), envelopeText(2)].join(
        '\n',
      ) + '\n',
    );
    const clock = virtualClock();
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(0);
    expect(await listProducers(spoolDir, runId)).toEqual([producer, otherProducer]);
    // One exchange for the dev token, then one batch per producer.
    expect(stub.exchanges).toHaveLength(1);
    expect(stub.calls).toHaveLength(2);
    // One line per producer, in the sorted order `listProducers` returns.
    expect(out.stdout.join('')).toBe(
      `evidgate: sent run=${runId} producer=${producer} batches=1 accepted=2 duplicate=0\n` +
        `evidgate: sent run=${runId} producer=${otherProducer} batches=1 accepted=1 ` +
        'duplicate=0\n',
    );
    expect(out.stderr.join('')).toBe('');

    const first = await openSpool(spoolDir);
    expect(await first.cursor()).toMatchObject({
      nextUnackedSequence: 3,
      lastAckedSequence: 2,
      ackedCount: 2,
    });
    const second = await openSpool(spoolDir, otherProducer);
    expect((await second.cursor()).nextUnackedSequence).toBe(2);

    // S3 analogue: the token is in no spool file, and the CLI-owned files are locked down.
    const files = await listFiles(join(spoolDir, runId));
    expect(files.length).toBeGreaterThan(0);
    for (const path of files) {
      expect(await readFile(path, 'utf8')).not.toContain(devToken);
    }
    expect(out.stdout.join('')).not.toContain(devToken);
    expect((await stat(join(spoolDir, runId, producer, 'cursor.json'))).mode & 0o777).toBe(0o600);
    expect((await stat(join(spoolDir, runId, producer))).mode & 0o777).toBe(0o700);
  });

  // D-S56-Q8: the reporter holds its own `writer.lock` for the life of a writer; the drain
  // is a chunk reader, so it refuses with `spool_locked` (exit 3) instead of acking events
  // out from under a running test run.
  it('refuses to drain while the reporter still writes the producer (D-S56-Q8)', async () => {
    const spoolDir = await tempDir();
    const spool = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId: producer,
      maxBytes: defaultMaxBytes,
      createIfMissing: true,
    });
    await spool.append([envelopeText(1), envelopeText(2)]);
    const producerDir = join(spoolDir, runId, producer);
    // A live reporter: this test process's pid is alive for certain.
    await writeFile(join(producerDir, writerLockFileName), writerLockText(process.pid), {
      mode: 0o600,
    });
    const before = await snapshotTree(join(spoolDir, runId));

    const clock = virtualClock();
    const stub = stubFetch((call) => acked(call));
    const out = capture();
    const code = await invoke(sendArgv(), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain('spool_locked');
    expect(out.stderr.join('')).toContain(String(process.pid));
    expect(out.stderr.join('')).toContain('the reporter is still writing this producer');
    // Nothing was sent, nothing was acked, nothing was deleted: the producer directory is
    // byte-identical, `writer.lock` included.
    expect(stub.calls).toHaveLength(0);
    expect(clock.sleeps).toEqual([]);
    expect(await snapshotTree(join(spoolDir, runId))).toEqual(before);
    expect(existsSync(join(producerDir, 'cursor.json'))).toBe(false);
  });

  it('drains a producer dir that also holds shards.json, uploads/ and a dead writer.lock', async () => {
    const spoolDir = await tempDir();
    const spool = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId: producer,
      maxBytes: defaultMaxBytes,
      createIfMissing: true,
    });
    await spool.append([envelopeText(1), envelopeText(2)]);
    const producerDir = join(spoolDir, runId, producer);
    // The reporter's sparse record, its upload journal row and a lock left by a reporter
    // that died: none of them is a chunk, and none of them may disturb the drain.
    const shardsText = `${JSON.stringify({
      producer_id: producer,
      shard_index: 1,
      shard_total: 1,
      sequence_next: 3,
    })}\n`;
    await writeFile(join(producerDir, 'shards.json'), shardsText);
    const artifactId = '5e0f6a3c-8b7d-4c2e-9f10-2a3b4c5d6e7f';
    const journalText = `${JSON.stringify({
      artifact_id: artifactId,
      file_sha256: `sha256:${'c'.repeat(64)}`,
      size: 42,
      put_done: false,
      complete_done: false,
      mint_no: 0,
    })}\n`;
    await mkdir(join(producerDir, 'uploads'), { recursive: true, mode: 0o700 });
    await writeFile(join(producerDir, 'uploads', `${artifactId}.json`), journalText);
    const lockText = writerLockText(exitedPid());
    await writeFile(join(producerDir, writerLockFileName), lockText, { mode: 0o600 });

    const clock = virtualClock();
    const stub = stubFetch((call) => acked(call));
    const out = capture();
    const code = await invoke(sendArgv(), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(0);
    expect(out.stderr.join('')).toBe('');
    expect(stub.calls).toHaveLength(1);
    expect(out.stdout.join('')).toBe(
      `evidgate: sent run=${runId} producer=${producer} batches=1 accepted=2 duplicate=0\n`,
    );
    const reopened = await openSpool(spoolDir);
    expect(await reopened.cursor()).toMatchObject({
      nextUnackedSequence: 3,
      lastAckedSequence: 2,
      ackedCount: 2,
    });
    // The reporter's files are untouched, and the dead lock is ignored rather than deleted
    // (the reporter's next start owns the takeover).
    expect(await readFile(join(producerDir, 'shards.json'), 'utf8')).toBe(shardsText);
    expect(await readFile(join(producerDir, writerLockFileName), 'utf8')).toBe(lockText);
    expect(await readFile(join(producerDir, 'uploads', `${artifactId}.json`), 'utf8')).toBe(
      journalText,
    );
  });

  it('survives a ten-minute outage with virtual backoff, then delivers (AC1)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${[envelopeText(1), envelopeText(2), envelopeText(3)].join('\n')}\n`);
    const clock = virtualClock();
    const startedAt = clock.now();
    // The API is down for ten virtual minutes: a batch that was stored before the ACK was
    // lost is re-sent, and every ack of the successful attempt comes back `duplicate`.
    const stub = stubFetch((call) =>
      clock.now() - startedAt < 600_000
        ? jsonResponse(
            503,
            {
              error: {
                code: 'service_unavailable',
                message: 'maintenance',
                request_id: 'req-503',
                retryable: true,
              },
            },
            { 'retry-after': '5' },
          )
        : acked(call, ['duplicate', 'duplicate', 'duplicate']),
    );
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(0);
    // Several attempts over the ten virtual minutes; the exact count depends on the
    // jittered backoff (the wait is the later of `Retry-After` and that backoff).
    expect(stub.calls.length).toBeGreaterThan(5);
    // One wait between attempts, never one after the last. `Retry-After: 5` is a FLOOR
    // under the jittered backoff (P3-1: a 0 or past date must not become a hot loop), so
    // each wait is at least 5 s and never past the 30 s backoff cap.
    expect(clock.sleeps).toHaveLength(stub.calls.length - 1);
    expect(clock.sleeps.every((ms) => ms >= 5_000)).toBe(true);
    expect(clock.sleeps.every((ms) => ms <= 30_000)).toBe(true);
    expect(out.stderr.join('')).toBe('');
    expect(out.stdout.join('')).toContain('batches=1 accepted=0 duplicate=3');
    // Every attempt carried the same bytes under the same key (AC5 identity rule).
    expect(new Set(stub.calls.map((call) => call.key)).size).toBe(1);
    expect(new Set(stub.calls.map((call) => call.wire.toString('base64'))).size).toBe(1);
    // `duplicate` counts as delivered: the cursor advanced once, not per attempt.
    const spool = await openSpool(spoolDir);
    expect(await spool.cursor()).toMatchObject({ nextUnackedSequence: 4, ackedCount: 3 });
  });

  it('resumes at the first unacked sequence after a fatal failure (AC2)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const total = 1_200;
    const lines = Array.from({ length: total }, (_, index) => envelopeText(index + 1));
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${lines.join('\n')}\n`);
    const clock = virtualClock();
    const deps = { now: clock.now, sleep: clock.sleep, random: () => 0.5 };

    // Run 1: the first two batches are acked, the third is refused permanently.
    let served = 0;
    const firstStub = stubFetch((call) => {
      served += 1;
      return served < 3 ? acked(call) : errorResponse(403, 'unknown_producer', 'req-403');
    });
    const firstOut = capture();
    const firstCode = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), firstOut, {
      ...deps,
      fetch: firstStub.fetchImpl,
    });

    expect(firstCode).toBe(3);
    expect(firstStub.calls).toHaveLength(3);
    expect(firstOut.stderr.join('')).toContain('evidgate: error unknown_producer:');
    expect(firstOut.stderr.join('')).toContain('evidgate: request_id: req-403');
    const spool = await openSpool(spoolDir);
    // Exactly the contiguous prefix advanced: batches 1..2, never batch 3.
    expect((await spool.cursor()).nextUnackedSequence).toBe(1_001);

    // Run 2 (a fresh process): only the remaining batch goes out, under the same key.
    const secondStub = stubFetch((call) => acked(call));
    const secondOut = capture();
    const secondCode = await invoke(sendArgv([]), baseEnv(spoolDir), secondOut, {
      ...deps,
      fetch: secondStub.fetchImpl,
    });

    expect(secondCode).toBe(0);
    expect(secondStub.calls).toHaveLength(1);
    const thirdKey = firstStub.calls[2]?.key;
    expect(thirdKey).not.toBeNull();
    expect(secondStub.calls[0]?.key).toBe(thirdKey);

    // The union of what was sent is every event exactly once, each under one key.
    const sent = new Map<string, string>();
    for (const call of [...firstStub.calls, ...secondStub.calls]) {
      const key = call.key ?? '';
      for (const event of call.events) {
        const eventId = String(event['event_id']);
        const previous = sent.get(eventId);
        expect(previous === undefined || previous === key).toBe(true);
        sent.set(eventId, key);
      }
    }
    expect(sent.size).toBe(total);
    for (const sequence of [1, 500, 501, 1_000, 1_001, total]) {
      expect(sent.has(eventIdFor(sequence))).toBe(true);
    }
    expect(secondStub.calls[0]?.events).toHaveLength(200);
    expect(secondStub.calls[0]?.events[0]?.['sequence']).toBe(1_001);
    expect((await spool.cursor()).nextUnackedSequence).toBe(total + 1);
  });

  it('refuses an intake that would cross the cap, naming run and range (AC3)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${[envelopeText(1), envelopeText(2)].join('\n')}\n`);
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(
      sendArgv(['--from', file]),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: '1' }),
      out,
      { fetch: stub.fetchImpl },
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error spool_cap_exceeded:');
    expect(stderr).toContain(`run ${runId}`);
    // CR2: the cap is refused while the first line is still the last one read, so the
    // range names the producer and the sequence that could not be spooled.
    expect(stderr).toContain('unsent sequences 1..1');
    expect(stderr).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    // The refusal happens before the first append, so the run directory is never created:
    // no chunk, no cursor, not even the producer directory.
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
    expect(await listProducers(spoolDir, runId)).toEqual([]);
  });

  // CR2/PF3/SC2: the byte total is kept while the input streams, so an endless `--from -`
  // is refused after the cap's worth of lines instead of being buffered whole (the old
  // code held every line of every producer in memory before the first cap check).
  it('refuses a --from stream while it is still being read (CR2)', async () => {
    const spoolDir = await tempDir();
    const capBytes = 4_096;
    let yielded = 0;
    // An endless stdin: every pull is a new envelope. The intake must stop reading it
    // once the cap is crossed, so `yielded` stays tiny.
    const endless: AsyncIterable<Uint8Array> = {
      [Symbol.asyncIterator]: () => ({
        next: (): Promise<IteratorResult<Uint8Array>> => {
          yielded += 1;
          return Promise.resolve({
            done: false,
            value: Buffer.from(`${envelopeText(yielded)}\n`, 'utf8'),
          });
        },
      }),
    };

    const out = capture();
    const code = await invoke(
      sendArgv(['--from', '-']),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: String(capBytes) }),
      out,
      { stdin: endless },
    );

    expect(code).toBe(3);
    // One envelope is ~240 bytes, so ~16 fit in 4 KiB: an endless stream must be cut off
    // after a few lines, never after the whole input.
    expect(yielded).toBeLessThan(64);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error spool_cap_exceeded:');
    expect(stderr).toContain(`run ${runId}`);
    expect(stderr).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    // Nothing was created at all: the refusal happens before the first append.
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });

  // CR3: the cap precheck covers the TOTAL of every producer before the first append, so a
  // refused intake leaves no producer half-written (the per-producer `append` would have
  // kept the first producer's lines and then failed on the second).
  it('refuses an intake whose producers together cross the cap, writing nothing (CR3)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    const first = envelopeText(1);
    const second = envelopeText(1, { producerId: otherProducer });
    await writeFile(file, `${[first, second].join('\n')}\n`);
    const lineBytes = Buffer.byteLength(first, 'utf8') + 1;
    // Each line alone would fit; only the two together cross the cap.
    const capBytes = lineBytes * 2 - 1;
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(
      sendArgv(['--from', file]),
      baseEnv(spoolDir, {
        EVIDGATE_SPOOL_MAX_BYTES: String(capBytes),
      }),
      out,
      { fetch: stub.fetchImpl },
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(out.stderr.join('')).toContain('evidgate: error spool_cap_exceeded:');
    // Producer A's line would have been durable under the old per-producer cap check.
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });

  // CR3, second half: the pre-append check must do real work, not merely repeat the
  // streaming one. The run directory is pre-seeded and then GROWS by 200 bytes while the
  // stream is being read, so every read-time total still fits and only the pre-append
  // total can refuse — with the same error and still nothing written.
  it('refuses an intake when the run directory grows past the cap mid-read (CR3)', async () => {
    const spoolDir = await tempDir();
    const seededBytes = 64;
    const growth = 200;
    const slack = 100;
    const seededProducer = 'seeded-writer';
    // A producer directory the current intake does not route to, so only its size counts.
    await mkdir(join(spoolDir, runId, seededProducer), { recursive: true, mode: 0o700 });
    await writeFile(
      join(spoolDir, runId, seededProducer, '000001.jsonl'),
      's'.repeat(seededBytes),
      {
        mode: 0o600,
      },
    );

    const lines = [envelopeText(1), envelopeText(2)];
    const totalBytes = lines.reduce((sum, line) => sum + Buffer.byteLength(line, 'utf8') + 1, 0);
    // Slack absorbs nothing: the 200-byte growth exceeds it.
    const capBytes = seededBytes + totalBytes + slack;

    let pulled = 0;
    const growing: AsyncIterable<Uint8Array> = {
      [Symbol.asyncIterator]: () => ({
        next: async (): Promise<IteratorResult<Uint8Array>> => {
          pulled += 1;
          const line = lines[pulled - 1];
          if (line !== undefined) {
            return { done: false, value: Buffer.from(`${line}\n`, 'utf8') };
          }
          // A concurrent writer grows the run directory after the last line was read.
          await writeFile(
            join(spoolDir, runId, seededProducer, '000002.jsonl'),
            'g'.repeat(growth),
            { mode: 0o600 },
          );
          return { done: true, value: undefined };
        },
      }),
    };
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(
      sendArgv(['--from', '-']),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: String(capBytes) }),
      out,
      { fetch: stub.fetchImpl, stdin: growing },
    );

    expect(code).toBe(3);
    expect(pulled).toBe(lines.length + 1);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error spool_cap_exceeded:');
    expect(stderr).toContain(`run ${runId}`);
    expect(stderr).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    // The pre-append refusal leaves no producer directory behind for the input's producer.
    await expect(readdir(join(spoolDir, runId, producer))).rejects.toThrow(/ENOENT/);
  });

  // N3: the `--from` line bound must be measured in BYTES, like the spool reader's own
  // `maxSpoolLineBytes`. A 3-byte BMP character repeated past the byte cap while staying
  // under it in UTF-16 code units would otherwise be spooled and then be `spool_corrupt`
  // for the reader, blocking that producer.
  it('refuses a multibyte --from line that is over the byte cap (N3)', async () => {
    const spoolDir = await tempDir();
    // 11.2M code units (< the 32 Mi unit count) but 33.6M UTF-8 bytes (> the 32 Mi byte
    // cap). The line has no newline, so it is still the pending partial line.
    const overLong = '\u4e00'.repeat(11_200_000);
    expect(overLong.length).toBeLessThan(maxSpoolLineBytes);
    expect(Buffer.byteLength(overLong, 'utf8')).toBeGreaterThan(maxSpoolLineBytes);
    const bytes = Buffer.from(overLong, 'utf8');
    let pulled = false;
    const overLongStream: AsyncIterable<Uint8Array> = {
      [Symbol.asyncIterator]: () => ({
        next: (): Promise<IteratorResult<Uint8Array>> => {
          const done = pulled;
          pulled = true;
          return Promise.resolve(
            done ? { done: true, value: undefined } : { done: false, value: bytes },
          );
        },
      }),
    };
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', '-']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      stdin: overLongStream,
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error invalid_spool_input:');
    expect(stderr).toContain(`line 1 is longer than ${maxSpoolLineBytes} bytes`);
    // Refused before the first append: not even the run directory exists.
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });

  /** A stdin of exactly these byte blocks, then end of stream. */
  function blocks(chunks: ReadonlyArray<Uint8Array>): AsyncIterable<Uint8Array> {
    let index = 0;
    return {
      [Symbol.asyncIterator]: () => ({
        next: (): Promise<IteratorResult<Uint8Array>> => {
          const chunk = chunks[index];
          index += 1;
          return Promise.resolve(
            chunk === undefined ? { done: true, value: undefined } : { done: false, value: chunk },
          );
        },
      }),
    };
  }

  // N3 follow-up: a line that GROWS past the bound and completes (its newline arrives)
  // inside the same read block never sits in the pending buffer at the end of that block,
  // so the pending check cannot see it. The completed line is measured before the yield.
  it('refuses an over-cap line that completes after a pending prefix (N3)', async () => {
    const spoolDir = await tempDir();
    // A cap-1 pending prefix passes the pending check, then the rest of the line plus its
    // newline arrives in a second block: only the completed-line check can refuse it.
    const prefix = 'a'.repeat(maxSpoolLineBytes - 1);
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', '-']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      stdin: blocks([Buffer.from(prefix, 'utf8'), Buffer.from('ab\n', 'utf8')]),
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error invalid_spool_input:');
    expect(stderr).toContain(`line 1 is longer than ${maxSpoolLineBytes} bytes`);
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });

  it('refuses an over-cap line that arrives complete in one block (N3)', async () => {
    const spoolDir = await tempDir();
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', '-']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      stdin: blocks([Buffer.from(`${'a'.repeat(maxSpoolLineBytes + 1)}\n`, 'utf8')]),
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error invalid_spool_input:');
    expect(stderr).toContain(`line 1 is longer than ${maxSpoolLineBytes} bytes`);
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });

  // CR7: the failure names the FULL unsent range read after the send settled, not just the
  // failed batch's range. Batch 1 (1..500) is held open on a 503 while batch 2 (501..1000)
  // fails permanently, so the cursor never moves and 1..1000 is what the operator must
  // re-send.
  it('names the full unsent range after the send settles (CR7)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const total = 1_000;
    const file = join(input, 'events.jsonl');
    await writeFile(
      file,
      `${Array.from({ length: total }, (_, index) => envelopeText(index + 1)).join('\n')}\n`,
    );
    const clock = virtualClock();
    // Batch 1 stays in flight until batch 2 has failed, so the permanent 422 is the error
    // that wins and batch 1's own retry loop cannot reach the deadline first.
    const batchTwoFailed = deferred();
    const stub = stubFetch(async (call) => {
      const first = Number(call.events[0]?.['sequence'] ?? 0);
      if (first === 1) {
        await batchTwoFailed.promise;
        return errorResponse(503, 'unavailable', 'req-503');
      }
      batchTwoFailed.resolve();
      return errorResponse(422, 'unknown_producer', 'req-422');
    });
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(3);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error unknown_producer:');
    expect(stderr).toContain('unsent sequences 1..1000');
    // The failing batch's own range (501..1000) is NOT the range that is reported.
    expect(stderr).not.toContain('501..1000');
  });

  it('stops before sending when the run directory is already above the cap (AC3)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${[envelopeText(1), envelopeText(2)].join('\n')}\n`);
    const clock = virtualClock();

    // Run 1 spools the events; the send is refused permanently, so 1..2 stay unacked.
    const failing = stubFetch(() => errorResponse(400, 'invalid_request', 'req-400'));
    const firstCode = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), capture(), {
      fetch: failing.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });
    expect(firstCode).toBe(3);
    expect((await openSpool(spoolDir).then((spool) => spool.cursor())).nextUnackedSequence).toBe(1);

    // Run 2 with a cap the existing directory already exceeds: no request at all.
    const stub = stubFetch((call) => acked(call));
    const out = capture();
    const code = await invoke(
      sendArgv([]),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: '64' }),
      out,
      { fetch: stub.fetchImpl, now: clock.now, sleep: clock.sleep, random: () => 0.5 },
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error spool_cap_exceeded:');
    expect(stderr).toContain(`run ${runId}`);
    expect(stderr).toContain('unsent sequences 1..2');
    expect(stderr).toContain('64 bytes');
  });

  it('fails an event too large for one batch body, naming its event_id (AC4)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${envelopeText(1, { pad: 1_100_000 })}\n`);
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error event_too_large:');
    expect(stderr).toContain(eventIdFor(1));
    expect(stderr).toContain('sequence 1');
    const spool = await openSpool(spoolDir);
    expect((await spool.cursor()).nextUnackedSequence).toBe(1);
  });

  for (const refusal of [
    { status: 403, code: 'producer_slot_taken' },
    { status: 422, code: 'unknown_producer' },
  ]) {
    it(`surfaces ${refusal.status} ${refusal.code} verbatim with its id (AC6)`, async () => {
      const input = await tempDir();
      const spoolDir = await tempDir();
      const file = join(input, 'events.jsonl');
      await writeFile(file, `${[envelopeText(1), envelopeText(2)].join('\n')}\n`);
      const stub = stubFetch(() =>
        errorResponse(refusal.status, refusal.code, `req-${refusal.status}`),
      );
      const clock = virtualClock();
      const out = capture();

      const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
        fetch: stub.fetchImpl,
        now: clock.now,
        sleep: clock.sleep,
        random: () => 0.5,
      });

      expect(code).toBe(3);
      // 403/422 are permanent: never retried, so exactly one request and no wait.
      expect(stub.calls).toHaveLength(1);
      expect(clock.sleeps).toEqual([]);
      const stderr = out.stderr.join('');
      expect(stderr).toContain(`evidgate: error ${refusal.code}:`);
      expect(stderr).toContain(`evidgate: request_id: req-${refusal.status}`);
      expect(stderr).toContain(`run ${runId}`);
      expect(stderr).toContain('unsent sequences 1..2');
      expect((await openSpool(spoolDir).then((spool) => spool.cursor())).nextUnackedSequence).toBe(
        1,
      );
    });
  }

  it('advances the cursor only over a contiguous prefix of out-of-order acks', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const total = 1_200;
    const file = join(input, 'events.jsonl');
    await writeFile(
      file,
      `${Array.from({ length: total }, (_, index) => envelopeText(index + 1)).join('\n')}\n`,
    );
    const gate = deferred();
    const thirdServed = deferred();
    let served = 0;
    const stub = stubFetch(async (call) => {
      served += 1;
      // Batch 1 (the first sequence range) is held in flight while batches 2 and 3 ack.
      if (served === 1) {
        await gate.promise;
      }
      if (served === 3) {
        thirdServed.resolve();
      }
      return acked(call);
    });
    const clock = virtualClock();
    const out = capture();

    const run = invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });
    // All three batches are in flight (maxInFlight is 4); two of them are already served.
    await thirdServed.promise;
    const spool = await openSpool(spoolDir);
    // Nothing may advance while the first batch's ack is missing.
    expect((await spool.cursor()).nextUnackedSequence).toBe(1);

    gate.resolve();
    expect(await run).toBe(0);
    // The completed-but-non-contiguous acks are applied only once the gap fills, and the
    // count is the whole prefix — a per-batch `ack(batch.lastSequence)` cannot do this.
    expect(await spool.cursor()).toMatchObject({
      nextUnackedSequence: total + 1,
      lastAckedSequence: total,
      ackedCount: total,
    });
  });

  it('reads --from - from the injected stdin, across chunk boundaries', async () => {
    const spoolDir = await tempDir();
    // CRLF line endings and a chunk boundary inside the first envelope.
    const bytes = Buffer.from(`${[envelopeText(1), envelopeText(2)].join('\r\n')}\r\n`, 'utf8');
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', '-']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      stdin: (async function* stream() {
        await Promise.resolve();
        yield bytes.subarray(0, 40);
        yield bytes.subarray(40);
      })(),
    });

    expect(code).toBe(0);
    expect(stub.calls).toHaveLength(1);
    expect(stub.calls[0]?.events).toHaveLength(2);
    expect(out.stdout.join('')).toContain('batches=1 accepted=2 duplicate=0');
  });

  it('prints one JSON document with --json, including the nothing-to-send case', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${[envelopeText(1), envelopeText(2)].join('\n')}\n`);
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', file, '--json']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
    });

    expect(code).toBe(0);
    expect(JSON.parse(out.stdout.join(''))).toEqual({
      run_id: runId,
      producers: [{ producer_id: producer, batches: 1, accepted: 2, duplicates: 0 }],
      batches: 1,
      accepted: 2,
      duplicates: 0,
    });

    // Everything is acked now: a second run sends nothing and makes no request.
    const idleStub = stubFetch((call) => acked(call));
    const idleOut = capture();
    const idleCode = await invoke(sendArgv(['--json']), baseEnv(spoolDir), idleOut, {
      fetch: idleStub.fetchImpl,
    });
    expect(idleCode).toBe(0);
    expect(idleStub.calls).toHaveLength(0);
    expect(JSON.parse(idleOut.stdout.join(''))).toEqual({
      run_id: runId,
      producers: [],
      batches: 0,
      accepted: 0,
      duplicates: 0,
    });
  });

  it('says nothing to send for a run with no spool, without a request', async () => {
    const spoolDir = await tempDir();
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv([]), baseEnv(spoolDir), out, { fetch: stub.fetchImpl });

    expect(code).toBe(0);
    expect(stub.calls).toHaveLength(0);
    expect(out.stdout.join('')).toBe(`evidgate: nothing to send run=${runId}\n`);
  });

  it('validates every flag before the token exchange or any request', async () => {
    const spoolDir = await tempDir();
    const cases: ReadonlyArray<{ readonly argv: string[]; readonly reason: string }> = [
      { argv: ['events', 'send', '--run', runId, '--run', runId], reason: 'only once' },
      { argv: ['events', 'send', '--run', 'not-a-uuid'], reason: '--run must be a UUID' },
      {
        argv: ['events', 'send', '--run', runId, '--producer', '../escape'],
        reason: '--producer must be 1..128',
      },
      {
        argv: ['events', 'send', '--run', runId, '--from', 'a.jsonl', '--from', 'b.jsonl'],
        reason: 'only once',
      },
      { argv: ['events', 'send', '--run', runId, '--from', '   '], reason: '--from must be' },
    ];
    for (const testCase of cases) {
      const stub = stubFetch((call) => acked(call));
      const out = capture();
      const code = await invoke(testCase.argv, baseEnv(spoolDir), out, {
        fetch: stub.fetchImpl,
      });
      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(0);
      expect(out.stderr.join('')).toContain('evidgate: error usage:');
      expect(out.stderr.join('')).toContain(testCase.reason);
    }
  });

  it('refuses a missing EVIDGATE_SPOOL_DIR before the verbose dump and any request', async () => {
    const env = baseEnv('/tmp/unused-spool');
    delete env['EVIDGATE_SPOOL_DIR'];
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(['events', 'send', '--run', runId, '--verbose'], env, out, {
      fetch: stub.fetchImpl,
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error missing_config:');
    expect(stderr).toContain('EVIDGATE_SPOOL_DIR');
    // `validateConfig` runs before the dump, so the refused configuration is not echoed.
    expect(stderr).not.toContain('resolved config');
  });

  // CR8: the spool surface is lazy and the command forces it in `validateConfig`, so a
  // malformed value is this command's `invalid_config` — before any request and before the
  // `--verbose` dump, which therefore never echoes it.
  it('refuses a malformed EVIDGATE_SPOOL_MAX_BYTES before any request (CR8)', async () => {
    const spoolDir = await tempDir();
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(
      sendArgv([]),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: 'lots' }),
      out,
      { fetch: stub.fetchImpl },
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error invalid_config:');
    expect(stderr).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    expect(stderr).not.toContain('lots');
    expect(stderr).not.toContain('resolved config');
  });

  it('refuses --from input of another run or producer, spooling nothing', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const otherRun = '11111111-2222-4333-8444-555555555555';
    const cases: ReadonlyArray<{
      readonly name: string;
      readonly argv: string[];
      readonly lines: string[];
    }> = [
      {
        name: 'run',
        argv: sendArgv(['--from', join(input, 'run.jsonl')]),
        lines: [envelopeText(1, { runId: otherRun })],
      },
      {
        name: 'producer',
        argv: sendArgv(['--producer', otherProducer, '--from', join(input, 'producer.jsonl')]),
        lines: [envelopeText(1)],
      },
    ];
    for (const testCase of cases) {
      const file = testCase.argv[testCase.argv.indexOf('--from') + 1] ?? '';
      await writeFile(file, `${testCase.lines.join('\n')}\n`);
      const stub = stubFetch((call) => acked(call));
      const out = capture();
      const code = await invoke(testCase.argv, baseEnv(spoolDir), out, {
        fetch: stub.fetchImpl,
      });
      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(0);
      expect(out.stderr.join('')).toContain('evidgate: error usage:');
    }
    expect(await listProducers(spoolDir, runId)).toEqual([]);
  });

  it('refuses malformed --from input with the code that names the problem', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const cases: ReadonlyArray<{ readonly lines: string[]; readonly code: string }> = [
      { lines: ['not json'], code: 'envelope_invalid' },
      { lines: [envelopeText(1, { type: 'Bad Type' })], code: 'envelope_invalid' },
      { lines: ['[]'], code: 'envelope_invalid' },
      { lines: [envelopeText(2)], code: 'invalid_spool_input' },
      { lines: [envelopeText(1), envelopeText(1)], code: 'invalid_spool_input' },
      { lines: [envelopeText(1), ''], code: 'invalid_spool_input' },
    ];
    for (const [index, testCase] of cases.entries()) {
      const file = join(input, `case-${index}.jsonl`);
      await writeFile(file, `${testCase.lines.join('\n')}\n`);
      const stub = stubFetch((call) => acked(call));
      const out = capture();
      const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
        fetch: stub.fetchImpl,
      });
      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(0);
      expect(out.stderr.join('')).toContain(`evidgate: error ${testCase.code}:`);
    }
    // Nothing was spooled by any of the refused files, and no directory was created.
    expect(await listProducers(spoolDir, runId)).toEqual([]);
  });

  it('purges the cached CI token on 401 only (contracts/cli.md §7)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const cacheDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${envelopeText(1)}\n`);
    // A `string | undefined` record: the dev token must be absent, and a cached CI token
    // is then the only source (deleting an optional-typed key keeps the type honest).
    const env: Record<string, string | undefined> = baseEnv(spoolDir, {
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: ['ingest', 'credential'].join('-'),
    });
    delete env['EVIDGATE_DEV_TOKEN'];
    const config = resolveConfig({ env });
    await writeCachedToken(config, {
      token: ['cached', 'token'].join('-'),
      expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(),
    });
    const stub = stubFetch(() => errorResponse(401, 'unauthorized', 'req-401'));
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), env, out, { fetch: stub.fetchImpl });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('evidgate: error unauthorized:');
    expect(out.stderr.join('')).toContain('evidgate: request_id: req-401');
    expect(await readCachedToken(config)).toBeUndefined();
    // The spool is kept: a 401 is not a reason to drop the operator's events.
    expect((await openSpool(spoolDir).then((spool) => spool.cursor())).nextUnackedSequence).toBe(1);
  });
});
