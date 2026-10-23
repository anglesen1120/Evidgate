// QA (independent, S54): negative and boundary specs for the spool, the producer lock, the
// batcher and the sender that the unit files do not pin down: exact cap boundary, per-run
// cap, CR-only and raw-control torn input, cursor corruption, kill-between-writes, stale and
// live locks, 500/501 event and 1 MiB batch edges, gzip threshold, Retry-After at the
// deadline and the 429 storm. Every clock and random source is injected; nothing sleeps.
import { mkdir, mkdtemp, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { gunzipSync } from 'node:zlib';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { OversizedEventError, maxBatchBytes, maxBatchEvents, packBatches } from './batcher.js';
import { resolveConfig } from './config.js';
import { sendBatches, type SendDeps } from './sender.js';
import { ProducerSpool, SpoolCapExceededError, SpoolCorruptError, runDirBytes } from './spool.js';
import { SpoolLockedError, withSpoolLock } from './spoolLock.js';

import type { SpoolEvent } from './spool.js';

const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
const producer = 'pw-shard-0';
const noValidate = (): void => undefined;

let roots: string[] = [];

async function tempDir(): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), 'qa-spool-'));
  roots.push(dir);
  return dir;
}

afterEach(async () => {
  await Promise.all(roots.map((dir) => rm(dir, { recursive: true, force: true })));
  roots = [];
});

function eventIdFor(sequence: number): string {
  return `00000000-0000-4000-8000-${sequence.toString(16).padStart(12, '0')}`;
}

/** One compact 00 §9.4 envelope; `pad` grows an `ext` member so the line hits a size. */
function envelopeText(sequence: number, pad = 0, padText?: string): string {
  const envelope: Record<string, unknown> = {
    schema_version: 1,
    event_id: eventIdFor(sequence),
    producer_id: producer,
    sequence,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'run.log',
    occurred_at: '2026-01-01T00:00:00Z',
  };
  if (pad > 0) {
    envelope['ext'] = { schema: 'x', pad: padText ?? 'y'.repeat(pad) };
  }
  return JSON.stringify(envelope);
}

function linesFrom(first: number, count: number, pad = 0): string[] {
  return Array.from({ length: count }, (_, i) => envelopeText(first + i, pad));
}

function exactBytes(lines: readonly string[]): number {
  return lines.reduce((sum, line) => sum + Buffer.byteLength(line, 'utf8') + 1, 0);
}

function synth(sequence: number, pad: number, padText?: string): SpoolEvent {
  const line = envelopeText(sequence, pad, padText);
  return {
    sequence,
    eventId: eventIdFor(sequence),
    line,
    lineBytes: Buffer.byteLength(line, 'utf8'),
    chunk: '000001.jsonl',
    lineNumber: sequence,
  };
}

function moderatelyCompressiblePad(): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  let seed = 12345;
  let noise = '';
  for (let i = 0; i < 30_000; i++) {
    seed = (Math.imul(seed, 1103515245) + 12345) & 0x7fffffff;
    noise += alphabet.charAt((seed >>> 16) & 63);
  }
  return 'abcdefgh;'.repeat(10_000) + noise;
}

async function* eventsOf<T>(list: readonly T[]): AsyncGenerator<T> {
  for (const item of list) {
    await Promise.resolve();
    yield item;
  }
}

const ackNothing = () => Promise.resolve();

async function collect<T>(gen: AsyncIterable<T>): Promise<T[]> {
  const out: T[] = [];
  for await (const item of gen) {
    out.push(item);
  }
  return out;
}

async function openSpool(spoolDir: string, maxBytes: number, producerId = producer) {
  return ProducerSpool.open({
    spoolDir,
    runId,
    producerId,
    maxBytes,
    createIfMissing: true,
  });
}

async function chunkFile(spoolDir: string, producerId = producer): Promise<string> {
  const dir = join(spoolDir, runId, producerId);
  const names = (await readdir(dir)).filter((name) => name.endsWith('.jsonl')).sort();
  const first = names[0];
  if (first === undefined) {
    throw new Error('no chunk file');
  }
  return join(dir, first);
}

describe('spool: cap boundary (04.2.3 AC3, contracts/cli.md §11.4)', () => {
  let spoolDir: string;

  beforeEach(async () => {
    spoolDir = await tempDir();
  });

  it('accepts a batch that lands exactly on the cap and refuses one byte less', async () => {
    const lines = linesFrom(1, 3);
    const exact = exactBytes(lines);

    const atCap = await openSpool(spoolDir, exact);
    await expect(atCap.append(lines)).resolves.toBeUndefined();
    expect(await runDirBytes(spoolDir, runId)).toBe(exact);

    const tight = await tempDir();
    const under = await openSpool(tight, exact - 1);
    await expect(under.append(lines)).rejects.toBeInstanceOf(SpoolCapExceededError);
    // Refused whole: nothing of the batch reached disk.
    expect(await runDirBytes(tight, runId)).toBe(0);
    expect(await collect(under.readUnacked())).toEqual([]);
  });

  it('refuses one more line once the exact cap is reached, leaving the first lines intact', async () => {
    const lines = linesFrom(1, 3);
    const spool = await openSpool(spoolDir, exactBytes(lines));
    await spool.append(lines);

    await expect(spool.append(linesFrom(4, 1))).rejects.toBeInstanceOf(SpoolCapExceededError);
    const seqs = (await collect(spool.readUnacked())).map((event) => event.sequence);
    expect(seqs).toEqual([1, 2, 3]);
  });

  it('shares the cap across producers of the same run (the cap is per run, not per producer)', async () => {
    const lines = linesFrom(1, 2);
    const cap = exactBytes(lines);
    const first = await openSpool(spoolDir, cap, 'pw-shard-0');
    await first.append(lines);

    const second = await openSpool(spoolDir, cap, 'pw-shard-1');
    await expect(
      second.append([envelopeText(1).replace(producer, 'pw-shard-1')]),
    ).rejects.toBeInstanceOf(SpoolCapExceededError);
  });
});

describe('spool: torn and corrupt input (contracts/cli.md §11.3)', () => {
  let spoolDir: string;

  beforeEach(async () => {
    spoolDir = await tempDir();
  });

  it('treats a bare CR between two envelopes as one unparseable line, never two events', async () => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append([envelopeText(1)]);
    const path = await chunkFile(spoolDir);
    // Two envelopes joined by CR only: CR is not a line terminator (only LF is).
    await writeFile(path, `${envelopeText(1)}\r${envelopeText(2)}\n`);

    const reopened = await openSpool(spoolDir, 1_000_000);
    await expect(collect(reopened.readUnacked())).rejects.toBeInstanceOf(SpoolCorruptError);
  });

  it('rejects a raw CR inside a JSON string (control characters are invalid JSON)', async () => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append([envelopeText(1)]);
    const path = await chunkFile(spoolDir);
    // Replace the type value with a literal CR byte inside the quotes (not the escape \r).
    const raw = envelopeText(1).replace('"run.log"', '"run\rlog"');
    await writeFile(path, `${raw}\n`);

    const reopened = await openSpool(spoolDir, 1_000_000);
    await expect(collect(reopened.readUnacked())).rejects.toBeInstanceOf(SpoolCorruptError);
  });

  it('refuses a sequence gap on disk: the reader reports spool_corrupt, never a silent skip', async () => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append(linesFrom(1, 3));
    // Hand-write a gap (4 is missing) directly into the chunk; the reader must not skip it.
    const path = await chunkFile(spoolDir);
    const text = await readFile(path, 'utf8');
    await writeFile(path, `${text}${envelopeText(5)}\n`);

    const reopened = await openSpool(spoolDir, 1_000_000);
    await expect(collect(reopened.readUnacked())).rejects.toBeInstanceOf(SpoolCorruptError);
  });

  it.each([
    ['not JSON', '{not json'],
    ['an array', '[]'],
    ['next below 1', { next_unacked_sequence: 0, last_acked_sequence: -1, acked_count: 0 }],
    ['last != next - 1', { next_unacked_sequence: 3, last_acked_sequence: 1, acked_count: 2 }],
    ['fractional count', { next_unacked_sequence: 2, last_acked_sequence: 1, acked_count: 1.5 }],
    [
      'foreign producer',
      { producer_id: 'other', next_unacked_sequence: 2, last_acked_sequence: 1, acked_count: 1 },
    ],
  ])('refuses a cursor that is %s with spool_corrupt', async (_label, payload) => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append(linesFrom(1, 3));
    const cursorPath = join(spoolDir, runId, producer, 'cursor.json');
    const body =
      typeof payload === 'string'
        ? payload
        : JSON.stringify({ producer_id: producer, updated_at: '2026-01-01T00:00:00Z', ...payload });
    await writeFile(cursorPath, body);

    const reopened = await openSpool(spoolDir, 1_000_000);
    await expect(reopened.cursor()).rejects.toBeInstanceOf(SpoolCorruptError);
  });

  it('a kill between the chunk write and the cursor write re-yields the unacked tail, no more', async () => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append(linesFrom(1, 5));
    await spool.ack(2, 2);
    const cursorPath = join(spoolDir, runId, producer, 'cursor.json');
    const stale = await readFile(cursorPath, 'utf8');
    await spool.ack(4, 2);
    // Simulate the process dying after the ack's cursor rename was lost: put the old cursor back.
    await writeFile(cursorPath, stale);

    const restarted = await openSpool(spoolDir, 1_000_000);
    const seqs = (await collect(restarted.readUnacked())).map((event) => event.sequence);
    expect(seqs).toEqual([3, 4, 5]);
    expect(await restarted.cursor()).toMatchObject({
      nextUnackedSequence: 3,
      lastAckedSequence: 2,
      ackedCount: 2,
    });
  });
});

describe('spool: producer lock (contracts/cli.md §11.2)', () => {
  let spoolDir: string;

  beforeEach(async () => {
    spoolDir = await tempDir();
  });

  it('refuses a second writer while a live holder owns the producer directory', async () => {
    const dir = join(spoolDir, runId, producer);
    await mkdir(dir, { recursive: true });
    await withSpoolLock(dir, async () => {
      await expect(withSpoolLock(dir, ackNothing)).rejects.toBeInstanceOf(SpoolLockedError);
    });
  });

  it('records the holder pid and started_at, and creates the lock file 0600', async () => {
    const dir = join(spoolDir, runId, producer);
    await mkdir(dir, { recursive: true });
    await withSpoolLock(dir, async () => {
      const raw = await readFile(join(dir, 'spool.lock'), 'utf8');
      const parsed = JSON.parse(raw) as Record<string, unknown>;
      expect(Object.keys(parsed).sort()).toEqual(['pid', 'started_at']);
      expect(parsed['pid']).toBe(process.pid);
      expect((await stat(join(dir, 'spool.lock'))).mode & 0o777).toBe(0o600);
    });
  });

  it('takes over a stale lock whose holder is dead', async () => {
    const dir = join(spoolDir, runId, producer);
    await mkdir(dir, { recursive: true });
    await withSpoolLock(dir, ackNothing);
    // A lock left by a crashed writer: pid 4242 is named, and the probe reports it dead.
    await writeFile(
      join(dir, 'spool.lock'),
      JSON.stringify({ pid: 4242, started_at: '2026-01-01T00:00:00Z' }),
    );
    let ran = false;
    await withSpoolLock(
      dir,
      async () => {
        await Promise.resolve();
        ran = true;
      },
      { isProcessAlive: () => false },
    );
    expect(ran).toBe(true);
  });

  it('releases the lock when the body throws, so the next writer is not blocked', async () => {
    const dir = join(spoolDir, runId, producer);
    await mkdir(dir, { recursive: true });
    await expect(
      withSpoolLock(dir, async () => {
        await Promise.resolve();
        throw new Error('body failed');
      }),
    ).rejects.toThrow('body failed');
    await expect(withSpoolLock(dir, () => Promise.resolve('free'))).resolves.toBe('free');
  });
});

describe('batcher: boundaries (04.2.3 AC4, contracts/cli.md §11)', () => {
  it.each([
    [499, [499]],
    [500, [500]],
    [501, [500, 1]],
  ])('packs %i small events into batches of %j', async (count, sizes) => {
    const events = Array.from({ length: count }, (_, i) => synth(i + 1, 0));
    const batches = await collect(
      packBatches(eventsOf(events), { runId, producerId: producer, validate: noValidate }),
    );
    expect(batches.map((b) => b.events.length)).toEqual(sizes);
    expect(batches.flatMap((b) => b.events.map((e) => e.sequence))).toEqual(
      events.map((e) => e.sequence),
    );
  });

  it('finds the exact 1 MiB single-event edge: the largest fitting line packs, one byte more is oversized', async () => {
    const fits = async (pad: number): Promise<boolean> => {
      try {
        await collect(
          packBatches(eventsOf([synth(1, pad)]), {
            runId,
            producerId: producer,
            validate: noValidate,
          }),
        );
        return true;
      } catch (error) {
        if (error instanceof OversizedEventError) {
          return false;
        }
        throw error;
      }
    };
    let lo = 0;
    let hi = maxBatchBytes;
    while (lo < hi) {
      const mid = Math.ceil((lo + hi) / 2);
      if (await fits(mid)) {
        lo = mid;
      } else {
        hi = mid - 1;
      }
    }
    const [batch] = await collect(
      packBatches(eventsOf([synth(1, lo)]), { runId, producerId: producer, validate: noValidate }),
    );
    expect(batch?.body.length ?? Number.POSITIVE_INFINITY).toBeLessThanOrEqual(maxBatchBytes);
    expect(await fits(lo + 1)).toBe(false);
    await expect(
      collect(
        packBatches(eventsOf([synth(1, lo + 1)]), {
          runId,
          producerId: producer,
          validate: noValidate,
        }),
      ),
    ).rejects.toMatchObject({ eventId: eventIdFor(1), sequence: 1 });
  });

  it('keeps every batch within the byte cap and the original order over varied sizes', async () => {
    // A fixed-seed LCG: deterministic sizes, no flake, no RNG in the spec.
    let state = 0x2f6e2b1;
    const next = (): number => {
      state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
      return state / 0x100000000;
    };
    const events = Array.from({ length: 1500 }, (_, i) =>
      synth(i + 1, Math.floor(next() * 40_000)),
    );
    const batches = await collect(
      packBatches(eventsOf(events), { runId, producerId: producer, validate: noValidate }),
    );
    for (const batch of batches) {
      expect(batch.body.length).toBeLessThanOrEqual(maxBatchBytes);
      expect(batch.events.length).toBeLessThanOrEqual(maxBatchEvents);
    }
    expect(batches.flatMap((b) => b.events.map((e) => e.sequence))).toEqual(
      events.map((e) => e.sequence),
    );
  });

  it('stops at the first invalid line: no batch containing it or anything after it is yielded', async () => {
    const events = [synth(1, 0), synth(2, 0), synth(3, 0), synth(4, 0)];
    const seen: number[][] = [];
    const bad = packBatches(eventsOf(events), {
      runId,
      producerId: producer,
      maxEvents: 2,
      validate: (_line, sequence) => {
        if (sequence === 3) {
          throw new Error('schema refused sequence 3');
        }
      },
    });
    await expect(
      (async () => {
        for await (const batch of bad) {
          seen.push(batch.events.map((e) => e.sequence));
        }
      })(),
    ).rejects.toThrow('schema refused sequence 3');
    expect(seen).toEqual([[1, 2]]);
  });

  it('gzips a moderately compressible body (about 5:1) and the payload decodes back to the exact body', async () => {
    const pad = moderatelyCompressiblePad();
    const [batch] = await collect(
      packBatches(eventsOf([synth(1, pad.length, pad)]), {
        runId,
        producerId: producer,
        validate: noValidate,
      }),
    );
    expect(batch?.gzipBody).toBeDefined();
    expect(batch?.gzipBody?.length).toBeLessThan(batch?.body.length ?? 0);
    expect(
      gunzipSync(batch?.gzipBody ?? Buffer.alloc(0)).equals(batch?.body ?? Buffer.alloc(1)),
    ).toBe(true);
  });

  it('drops gzip when it would expand more than 20:1 (server MaxExpansionRatio)', async () => {
    const [batch] = await collect(
      packBatches(eventsOf([synth(1, 200_000)]), {
        runId,
        producerId: producer,
        validate: noValidate,
      }),
    );
    expect(batch?.body.length).toBeGreaterThan(200_000);
    expect(batch?.gzipBody).toBeUndefined();
  });

  it('does not gzip a small body: the saving is under the 1 KiB floor', async () => {
    const [batch] = await collect(
      packBatches(eventsOf([synth(1, 0)]), { runId, producerId: producer, validate: noValidate }),
    );
    expect(batch?.gzipBody).toBeUndefined();
  });
});

describe('sender: retry edges (04.2.3 AC1, AC5)', () => {
  const config = resolveConfig({
    env: {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_ENV: 'dev',
    },
  });
  const token = ['dev', 'token', '0123456789'].join('-');

  interface FakeClock {
    now: () => number;
    sleep: (ms: number) => Promise<void>;
    sleeps: number[];
  }

  function clock(): FakeClock {
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

  function ackFor(events: ReadonlyArray<Record<string, unknown>>, reverse = false): Response {
    const acks = events.map((event, index) => ({
      index,
      event_id: String(event['event_id']),
      ack: 'accepted',
    }));
    const ordered = reverse ? [...acks].reverse() : acks;
    return jsonResponse(200, { acks: ordered, accepted: acks.length, duplicates: 0 });
  }

  async function batchesOf(events: readonly SpoolEvent[]) {
    return collect(
      packBatches(eventsOf(events), { runId, producerId: producer, validate: noValidate }),
    );
  }

  function depsFor(fetchImpl: typeof fetch, c: FakeClock): SendDeps {
    return { fetch: fetchImpl, sleep: c.sleep, now: c.now, random: () => 0.5 };
  }

  async function bodyEvents(
    input: string | URL | Request,
    init?: RequestInit,
  ): Promise<Array<Record<string, unknown>>> {
    const request = input instanceof Request ? input : new Request(input, init);
    const raw = Buffer.from(await request.arrayBuffer());
    const bytes = request.headers.get('content-encoding') === 'gzip' ? gunzipSync(raw) : raw;
    const body = JSON.parse(bytes.toString('utf8')) as {
      events: Array<Record<string, unknown>>;
    };
    return body.events;
  }

  it('sends no Content-Encoding for a small body and sets gzip only when the batch carries gzipBody', async () => {
    const seen: Array<string | null> = [];
    const fetchImpl: typeof fetch = async (input, init) => {
      const request = input instanceof Request ? input : new Request(input, init);
      seen.push(request.headers.get('content-encoding'));
      return ackFor(await bodyEvents(input, init));
    };
    const small = await batchesOf([synth(1, 0)]);
    const pad = moderatelyCompressiblePad();
    const big = await batchesOf([synth(2, pad.length, pad)]);
    const c = clock();
    await sendBatches(
      eventsOf(small),
      { config, token, runId, deadlineMs: 60_000 },
      depsFor(fetchImpl, c),
      ackNothing,
    );
    await sendBatches(
      eventsOf(big),
      { config, token, runId, deadlineMs: 60_000 },
      depsFor(fetchImpl, c),
      ackNothing,
    );
    expect(seen).toEqual([null, 'gzip']);
  });

  it('gives up when Retry-After equals the remaining deadline: no sleep that reaches the deadline', async () => {
    const c = clock();
    const calls: number[] = [];
    const fetchImpl: typeof fetch = async () => {
      await Promise.resolve();
      calls.push(c.now());
      return jsonResponse(
        429,
        { error: { code: 'rate_limited', message: 'slow', request_id: 'r', retryable: true } },
        { 'retry-after': '5' },
      );
    };
    const batches = await batchesOf([synth(1, 0)]);
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 5_000 },
        depsFor(fetchImpl, c),
        ackNothing,
      ),
    ).rejects.toMatchObject({ code: 'deadline_exceeded' });
    expect(calls).toHaveLength(1);
    expect(c.sleeps).toEqual([]);
  });

  it('does not wait out an absurd Retry-After: one attempt, deadline_exceeded, zero sleeps', async () => {
    const c = clock();
    let calls = 0;
    const fetchImpl: typeof fetch = async () => {
      await Promise.resolve();
      calls += 1;
      return jsonResponse(
        429,
        { error: { code: 'rate_limited', message: 'slow', request_id: 'r', retryable: true } },
        { 'retry-after': '999999999' },
      );
    };
    const batches = await batchesOf([synth(1, 0)]);
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 60_000 },
        depsFor(fetchImpl, c),
        ackNothing,
      ),
    ).rejects.toMatchObject({ code: 'deadline_exceeded' });
    expect(calls).toBe(1);
    expect(c.sleeps).toEqual([]);
  });

  it('honours a Retry-After that fits the deadline, then delivers', async () => {
    const c = clock();
    let calls = 0;
    const fetchImpl: typeof fetch = async (input, init) => {
      calls += 1;
      if (calls === 1) {
        return jsonResponse(
          429,
          { error: { code: 'rate_limited', message: 'slow', request_id: 'r', retryable: true } },
          { 'retry-after': '4' },
        );
      }
      return ackFor(await bodyEvents(input, init));
    };
    const batches = await batchesOf([synth(1, 0)]);
    const acked: number[] = [];
    await sendBatches(
      eventsOf(batches),
      { config, token, runId, deadlineMs: 5_000 },
      depsFor(fetchImpl, c),
      async (batch) => {
        await Promise.resolve();
        acked.push(batch.lastSequence);
      },
    );
    expect(c.sleeps).toEqual([4_000]);
    expect(acked).toEqual([1]);
  });

  it('a 429 storm inside a 10 s deadline ends with deadline_exceeded and never sleeps past it', async () => {
    const c = clock();
    const start = c.now();
    let calls = 0;
    const fetchImpl: typeof fetch = async () => {
      await Promise.resolve();
      calls += 1;
      return jsonResponse(
        429,
        { error: { code: 'rate_limited', message: 'slow', request_id: 'r', retryable: true } },
        { 'retry-after': '1' },
      );
    };
    const batches = await batchesOf([synth(1, 0)]);
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 10_000 },
        depsFor(fetchImpl, c),
        ackNothing,
      ),
    ).rejects.toMatchObject({ code: 'deadline_exceeded' });
    expect(c.now() - start).toBeLessThanOrEqual(10_000);
    expect(c.sleeps.reduce((a, b) => a + b, 0)).toBeLessThanOrEqual(10_000);
    expect(calls).toBeGreaterThan(1);
  });

  it('a permanent 422 is one attempt with the envelope code, never retried', async () => {
    const c = clock();
    let calls = 0;
    const fetchImpl: typeof fetch = async () => {
      await Promise.resolve();
      calls += 1;
      return jsonResponse(422, {
        error: {
          code: 'validation_failed',
          message: 'refused',
          request_id: 'req-x',
          retryable: false,
        },
      });
    };
    const batches = await batchesOf([synth(1, 0)]);
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 60_000 },
        depsFor(fetchImpl, c),
        ackNothing,
      ),
    ).rejects.toMatchObject({ code: 'validation_failed', status: 422 });
    expect(calls).toBe(1);
    expect(c.sleeps).toEqual([]);
  });

  it('an ack list in the wrong order is refused and the batch is never recorded as acked', async () => {
    const c = clock();
    const fetchImpl: typeof fetch = async (input, init) =>
      ackFor(await bodyEvents(input, init), true);
    const batches = await batchesOf([synth(1, 0), synth(2, 0)]);
    const acked: number[] = [];
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 60_000 },
        depsFor(fetchImpl, c),
        async (batch) => {
          await Promise.resolve();
          acked.push(batch.lastSequence);
        },
      ),
    ).rejects.toThrow();
    expect(acked).toEqual([]);
  });
});
