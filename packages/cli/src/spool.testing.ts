// The seams every spool spec shares: one temporary root per spec (created before it, removed
// after it), the producer spool opened on that root, and the on-disk readers the assertions
// use. Nothing is mocked: the spool's contract IS its on-disk layout, so a fake filesystem
// would prove nothing. No network, no timers.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach } from 'vitest';

import { ProducerSpool, defaultSpoolMaxBytes, spoolDirMode, spoolFileMode } from './spool.js';

import type { SpoolEvent } from './spool.js';

export const runId = 'run-04.2.3';
export const producerId = 'producer-primary';

/** One realistic 00 §9.4 envelope line (the batcher, not the spool, validates the schema). */
export function makeLine(sequence: number, eventId: string = randomUUID()): string {
  return JSON.stringify({
    schema_version: 1,
    event_id: eventId,
    producer_id: producerId,
    sequence,
    execution_key: 'a'.repeat(64),
    attempt_no: 1,
    type: 'run.log',
    occurred_at: '2026-10-23T00:00:00.000Z',
  });
}

export interface TestEvent {
  readonly sequence: number;
  readonly eventId: string;
  readonly line: string;
}

export function makeEvents(sequences: readonly number[]): TestEvent[] {
  return sequences.map((sequence) => {
    const eventId = randomUUID();
    return { sequence, eventId, line: makeLine(sequence, eventId) };
  });
}

export function range(from: number, to: number): number[] {
  const values: number[] = [];
  for (let value = from; value <= to; value += 1) values.push(value);
  return values;
}

export async function collect(events: AsyncIterable<SpoolEvent>): Promise<SpoolEvent[]> {
  const collected: SpoolEvent[] = [];
  for await (const event of events) collected.push(event);
  return collected;
}

export function failureOf<T>(promise: Promise<T>): Promise<unknown> {
  return promise.then(
    () => null,
    (error: unknown) => error,
  );
}

/** Chunk file names of a producer directory, sorted. */
export async function chunkNames(directory: string): Promise<string[]> {
  return (await readdir(directory)).filter((name) => name.endsWith('.jsonl')).sort();
}

export async function linesOf(path: string): Promise<string[]> {
  return (await readFile(path, 'utf8')).split('\n').filter((line) => line.length > 0);
}

/** Writes `count` contiguous envelope lines, starting at `first`, into one chunk file. */
export async function seedChunk(path: string, first: number, count: number): Promise<void> {
  const lines = makeEvents(range(first, first + count - 1)).map((event) => event.line);
  await writeFile(path, `${lines.join('\n')}\n`, { mode: spoolFileMode });
}

export async function modeOf(path: string): Promise<number> {
  return (await stat(path)).mode & 0o777;
}

/** Narrows a caught failure to the expected error class (no unchecked cast). */
export function expectError<T extends Error>(
  failure: unknown,
  ctor: { new (...args: never[]): T; readonly name: string },
): T {
  if (!(failure instanceof ctor)) {
    const observed = failure instanceof Error ? failure.message : typeof failure;
    throw new Error(`expected ${ctor.name}, got ${observed}`);
  }
  return failure;
}

/** The `sequence` of every envelope line, read back the way the batcher will. */
export function sequencesOf(lines: readonly string[]): number[] {
  return lines.map((line) => {
    const parsed: unknown = JSON.parse(line);
    if (typeof parsed !== 'object' || parsed === null || !('sequence' in parsed)) {
      throw new Error(`line is not an envelope: ${line}`);
    }
    const sequence = parsed.sequence;
    if (typeof sequence !== 'number') throw new Error(`line has no numeric sequence: ${line}`);
    return sequence;
  });
}

/** Every file under `root`, recursively (this spec's own scan, not the spool's). */
export async function allFiles(root: string): Promise<string[]> {
  const files: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const child = join(root, entry.name);
    if (entry.isDirectory()) files.push(...(await allFiles(child)));
    else if (entry.isFile()) files.push(child);
  }
  return files;
}

/** The fields a spec overrides when it opens a producer spool. */
export interface OpenOverrides {
  readonly runId?: string;
  readonly producerId?: string;
  readonly maxBytes?: number;
  readonly createIfMissing?: boolean;
}

/** The per-spec fixture: one temporary root, and the producer spool opened on it. */
export interface SpoolFixture {
  /** This spec's temporary root, removed after it. */
  readonly root: string;
  /** The spool root under `root`, already created. */
  readonly spoolDir: string;
  /** Opens a producer spool on this spec's spool root. */
  readonly open: (overrides?: OpenOverrides) => Promise<ProducerSpool>;
}

/**
 * Registers the per-spec temp root (created before each spec, removed after it) and returns
 * the fixture the specs open their producers through. `open` closes over that root, so a
 * spec may destructure it; `root` and `spoolDir` are getters because they change per spec.
 */
export function useSpoolFixture(): SpoolFixture {
  let root = '';
  let spoolDir = '';

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), 'evidgate-spool-'));
    // The spool root is created by the CLI command (config.ts); the tests create it here
    // so the "missing producer" path is the one under test.
    spoolDir = join(root, 'spool');
    await mkdir(spoolDir, { recursive: true, mode: spoolDirMode });
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  return {
    get root(): string {
      return root;
    },
    get spoolDir(): string {
      return spoolDir;
    },
    // Built field by field: `exactOptionalPropertyTypes` refuses a `Partial<…>` spread.
    open: (overrides: OpenOverrides = {}) =>
      ProducerSpool.open({
        spoolDir,
        runId: overrides.runId ?? runId,
        producerId: overrides.producerId ?? producerId,
        maxBytes: overrides.maxBytes ?? defaultSpoolMaxBytes,
        createIfMissing: overrides.createIfMissing ?? true,
      }),
  };
}
