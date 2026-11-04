// The seams the spool, batcher and sender QA specs share: one temporary root per spec, the
// envelope and event builders every cap and boundary assertion measures against, and the
// scripted transport the sender specs answer with. Every clock and random source is
// injected; nothing sleeps.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { gunzipSync } from 'node:zlib';

import { afterEach } from 'vitest';

import { packBatches } from './batcher.js';
import { jsonResponse } from './commands/cliSpec.testing.js';
import { ProducerSpool } from './spool.js';

import type { Batch } from './batcher.js';
import type { SendDeps } from './sender.js';
import type { SpoolEvent } from './spool.js';

export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
export const producer = 'pw-shard-0';
export const noValidate = (): void => undefined;

/** The per-spec temporary roots: every `tempDir()` is fresh, and all are removed after the spec. */
export interface TempRoots {
  readonly tempDir: () => Promise<string>;
}

export function useTempRoots(): TempRoots {
  const roots: string[] = [];
  afterEach(async () => {
    await Promise.all(roots.map((dir) => rm(dir, { recursive: true, force: true })));
    roots.length = 0;
  });
  return {
    tempDir: async (): Promise<string> => {
      const dir = await mkdtemp(join(tmpdir(), 'qa-spool-'));
      roots.push(dir);
      return dir;
    },
  };
}

export function eventIdFor(sequence: number): string {
  return `00000000-0000-4000-8000-${sequence.toString(16).padStart(12, '0')}`;
}

/** One compact event envelope; `pad` grows an `ext` member so the line hits a size. */
export function envelopeText(sequence: number, pad = 0, padText?: string): string {
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

export function linesFrom(first: number, count: number, pad = 0): string[] {
  return Array.from({ length: count }, (_, i) => envelopeText(first + i, pad));
}

export function exactBytes(lines: readonly string[]): number {
  return lines.reduce((sum, line) => sum + Buffer.byteLength(line, 'utf8') + 1, 0);
}

export function synth(sequence: number, pad: number, padText?: string): SpoolEvent {
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

export function moderatelyCompressiblePad(): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  let seed = 12345;
  let noise = '';
  for (let i = 0; i < 30_000; i++) {
    seed = (Math.imul(seed, 1103515245) + 12345) & 0x7fffffff;
    noise += alphabet.charAt((seed >>> 16) & 63);
  }
  return 'abcdefgh;'.repeat(10_000) + noise;
}

export async function* eventsOf<T>(list: readonly T[]): AsyncGenerator<T> {
  for (const item of list) {
    await Promise.resolve();
    yield item;
  }
}

export const ackNothing = (): Promise<void> => Promise.resolve();

export async function collect<T>(gen: AsyncIterable<T>): Promise<T[]> {
  const out: T[] = [];
  for await (const item of gen) {
    out.push(item);
  }
  return out;
}

export async function openSpool(
  spoolDir: string,
  maxBytes: number,
  producerId = producer,
): Promise<ProducerSpool> {
  return ProducerSpool.open({
    spoolDir,
    runId,
    producerId,
    maxBytes,
    createIfMissing: true,
  });
}

export async function chunkFile(spoolDir: string, producerId = producer): Promise<string> {
  const dir = join(spoolDir, runId, producerId);
  const names = (await readdir(dir)).filter((name) => name.endsWith('.jsonl')).sort();
  const first = names[0];
  if (first === undefined) {
    throw new Error('no chunk file');
  }
  return join(dir, first);
}

/** The injected clock a sender spec drives: `sleep` advances it, and every wait is recorded. */
export interface FakeClock {
  now: () => number;
  sleep: (ms: number) => Promise<void>;
  sleeps: number[];
}

export function clock(): FakeClock {
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

/** One 200 ack envelope in request order; `reverse` lies about that order on purpose. */
export function ackFor(events: ReadonlyArray<Record<string, unknown>>, reverse = false): Response {
  const acks = events.map((event, index) => ({
    index,
    event_id: String(event['event_id']),
    ack: 'accepted',
  }));
  const ordered = reverse ? [...acks].reverse() : acks;
  return jsonResponse(200, { acks: ordered, accepted: acks.length, duplicates: 0 });
}

export async function batchesOf(events: readonly SpoolEvent[]): Promise<Batch[]> {
  return collect(
    packBatches(eventsOf(events), { runId, producerId: producer, validate: noValidate }),
  );
}

export function depsFor(fetchImpl: typeof fetch, c: FakeClock): SendDeps {
  return { fetch: fetchImpl, sleep: c.sleep, now: c.now, random: () => 0.5 };
}

/** The batch body a stubbed `fetch` was handed, decoded (a gzip body is inflated first). */
export async function bodyEvents(
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
