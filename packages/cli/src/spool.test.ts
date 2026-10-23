// 04.2.3-T1.1: the CLI spool specs (04.2.3 §9: `restart_resumes_cursor`, `torn_write_recovery`,
// `cap_exceeded_named_error`, plus the §8 rows: 0600/0700 modes, path-traversal ids,
// symlinked producer directory, "the token never reaches the spool", purge).
//
// Everything runs against a real temporary directory: the spool's contract IS its
// on-disk layout, so a mocked filesystem would prove nothing. No network, no timers.
import { randomUUID } from 'node:crypto';
import {
  appendFile,
  mkdir,
  mkdtemp,
  readdir,
  readFile,
  rm,
  stat,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  InvalidSpoolIdError,
  ProducerSpool,
  SpoolCapExceededError,
  SpoolCorruptError,
  SpoolMissingError,
  assertValidSpoolId,
  defaultSpoolMaxBytes,
  listProducers,
  runDirBytes,
  spoolChunkMaxLines,
  spoolCursorFileName,
  spoolDirMode,
  spoolFileMode,
  spoolIdMaxLength,
} from './spool.js';
import { spoolLockFileName } from './spoolLock.js';

import type { SpoolEvent } from './spool.js';

const runId = 'run-04.2.3';
const producerId = 'producer-primary';

/** One realistic 00 §9.4 envelope line (the batcher, not the spool, validates the schema). */
function makeLine(sequence: number, eventId: string = randomUUID()): string {
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

interface TestEvent {
  readonly sequence: number;
  readonly eventId: string;
  readonly line: string;
}

function makeEvents(sequences: readonly number[]): TestEvent[] {
  return sequences.map((sequence) => {
    const eventId = randomUUID();
    return { sequence, eventId, line: makeLine(sequence, eventId) };
  });
}

function range(from: number, to: number): number[] {
  const values: number[] = [];
  for (let value = from; value <= to; value += 1) values.push(value);
  return values;
}

async function collect(events: AsyncIterable<SpoolEvent>): Promise<SpoolEvent[]> {
  const collected: SpoolEvent[] = [];
  for await (const event of events) collected.push(event);
  return collected;
}

function failureOf<T>(promise: Promise<T>): Promise<unknown> {
  return promise.then(
    () => null,
    (error: unknown) => error,
  );
}

/** Chunk file names of a producer directory, sorted. */
async function chunkNames(directory: string): Promise<string[]> {
  return (await readdir(directory)).filter((name) => name.endsWith('.jsonl')).sort();
}

async function linesOf(path: string): Promise<string[]> {
  return (await readFile(path, 'utf8')).split('\n').filter((line) => line.length > 0);
}

async function modeOf(path: string): Promise<number> {
  return (await stat(path)).mode & 0o777;
}

/** Narrows a caught failure to the expected error class (no unchecked cast). */
function expectError<T extends Error>(
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
function sequencesOf(lines: readonly string[]): number[] {
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
async function allFiles(root: string): Promise<string[]> {
  const files: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const child = join(root, entry.name);
    if (entry.isDirectory()) files.push(...(await allFiles(child)));
    else if (entry.isFile()) files.push(child);
  }
  return files;
}

/** The fields a spec overrides when it opens a producer spool. */
interface OpenOverrides {
  readonly runId?: string;
  readonly producerId?: string;
  readonly maxBytes?: number;
  readonly createIfMissing?: boolean;
}

describe('ProducerSpool', () => {
  let root: string;
  let spoolDir: string;

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

  function open(overrides: OpenOverrides = {}): Promise<ProducerSpool> {
    // Built field by field: `exactOptionalPropertyTypes` refuses a `Partial<…>` spread.
    return ProducerSpool.open({
      spoolDir,
      runId: overrides.runId ?? runId,
      producerId: overrides.producerId ?? producerId,
      maxBytes: overrides.maxBytes ?? defaultSpoolMaxBytes,
      createIfMissing: overrides.createIfMissing ?? true,
    });
  }

  it('restart_resumes_cursor: a reopened spool continues at the first unacked sequence', async () => {
    const events = makeEvents(range(1, 10));
    const first = await open();
    expect(await first.cursor()).toEqual({
      producerId,
      nextUnackedSequence: 1,
      lastAckedSequence: 0,
      ackedCount: 0,
    });
    await first.append(events.map((event) => event.line));
    await first.ack(4, 4);
    expect(await first.cursor()).toEqual({
      producerId,
      nextUnackedSequence: 5,
      lastAckedSequence: 4,
      ackedCount: 4,
    });

    // A new process: same directory, fresh instance, no in-memory state.
    const restarted = await open();
    const unacked = await collect(restarted.readUnacked());
    expect(unacked.map((event) => event.sequence)).toEqual(range(5, 10));
    expect(unacked.map((event) => event.eventId)).toEqual(events.slice(4).map((e) => e.eventId));
    expect(new Set(unacked.map((event) => event.eventId)).size).toBe(6);
    expect(unacked.map((event) => event.line)).toEqual(events.slice(4).map((e) => e.line));
    expect(unacked[0]?.lineBytes).toBe(Buffer.byteLength(events[4]?.line ?? '', 'utf8') + 1);
    expect(unacked[0]?.chunk).toBe('000001.jsonl');
    expect(unacked[0]?.lineNumber).toBe(5);
    expect(await restarted.unsentRange()).toEqual({ first: 5, last: 10 });

    await restarted.ack(10, 6);
    expect(await restarted.cursor()).toEqual({
      producerId,
      nextUnackedSequence: 11,
      lastAckedSequence: 10,
      ackedCount: 10,
    });
    expect(await restarted.unsentRange()).toBeNull();
    expect(await collect(restarted.readUnacked())).toEqual([]);

    // Monotonic: never backwards, never past what is durable on disk.
    expect(await failureOf(restarted.ack(9, 0))).toMatchObject({ code: 'invalid_ack' });
    expect(await failureOf(restarted.ack(11, 1))).toMatchObject({ code: 'invalid_ack' });
    expect(await restarted.cursor()).toMatchObject({ nextUnackedSequence: 11, ackedCount: 10 });
  });

  it('torn_write_recovery: a partial tail is ignored, then truncated by the appender', async () => {
    const events = makeEvents(range(1, 3));
    const first = await open();
    await first.append(events.map((event) => event.line));
    const chunkPath = join(first.directory, '000001.jsonl');

    // A crash inside write(2) leaves a JSON fragment with no terminator.
    const fragment = '{"sequence":4,"event_id":"torn-fragment"';
    await appendFile(chunkPath, fragment);

    // The reader ignores it (D3) instead of reporting a corrupt spool …
    const reopened = await open();
    const before = await collect(reopened.readUnacked());
    expect(before.map((event) => event.sequence)).toEqual([1, 2, 3]);

    // … and the appender repairs it before its first write, so the JSONL stays
    // line-delimited and no fragment is ever concatenated onto a new line.
    const next = makeEvents([4]);
    await reopened.append(next.map((event) => event.line));
    const raw = await readFile(chunkPath, 'utf8');
    expect(raw.endsWith('\n')).toBe(true);
    expect(raw).not.toContain('torn-fragment');
    const lines = await linesOf(chunkPath);
    expect(sequencesOf(lines)).toEqual([1, 2, 3, 4]);
    expect((await collect(reopened.readUnacked())).map((event) => event.sequence)).toEqual([
      1, 2, 3, 4,
    ]);
  });

  it('torn_write_recovery: an interior bad line is spool_corrupt naming chunk + line', async () => {
    const spool = await open();
    const [one, two] = makeEvents([1, 3]);
    await writeFile(
      join(spool.directory, '000001.jsonl'),
      `${one?.line ?? ''}\nthis is not an envelope\n${two?.line ?? ''}\n`,
      { mode: spoolFileMode },
    );

    const corrupt = expectError(await failureOf(collect(spool.readUnacked())), SpoolCorruptError);
    expect(corrupt.chunk).toBe('000001.jsonl');
    expect(corrupt.lineNumber).toBe(2);
    expect(corrupt.code).toBe('spool_corrupt');
  });

  it('torn_write_recovery: gaps and regressions are spool_corrupt, never skipped', async () => {
    const spool = await open();
    const gap = makeEvents([1, 2, 4]);
    await writeFile(
      join(spool.directory, '000001.jsonl'),
      `${gap.map((event) => event.line).join('\n')}\n`,
      { mode: spoolFileMode },
    );
    const gapFailure = expectError(
      await failureOf(collect(spool.readUnacked())),
      SpoolCorruptError,
    );
    expect(gapFailure.lineNumber).toBe(3);
    expect(gapFailure.message).toContain('skips the expected 3');

    const regression = makeEvents([1, 2]);
    const second = await open({ producerId: 'producer-regression' });
    await writeFile(
      join(second.directory, '000001.jsonl'),
      `${regression[0]?.line ?? ''}\n${regression[1]?.line ?? ''}\n${regression[1]?.line ?? ''}\n`,
      { mode: spoolFileMode },
    );
    const regressionFailure = expectError(
      await failureOf(collect(second.readUnacked())),
      SpoolCorruptError,
    );
    expect(regressionFailure.lineNumber).toBe(3);
    expect(regressionFailure.message).toContain('regresses');
  });

  it('torn_write_recovery: a torn line in a non-final chunk is corrupt', async () => {
    const spool = await open();
    const [one, two] = makeEvents([1, 2]);
    await writeFile(join(spool.directory, '000001.jsonl'), `${one?.line ?? ''}`, {
      mode: spoolFileMode,
    });
    await writeFile(join(spool.directory, '000002.jsonl'), `${two?.line ?? ''}\n`, {
      mode: spoolFileMode,
    });

    const failure = expectError(await failureOf(collect(spool.readUnacked())), SpoolCorruptError);
    expect(failure.chunk).toBe('000001.jsonl');
    expect(failure.lineNumber).toBe(1);
  });

  it('cap_exceeded_named_error: the cap names run, producer, range and cap, and writes nothing', async () => {
    const capBytes = 2_000;
    const spool = await open({ maxBytes: capBytes });
    const firstBatch = makeEvents([1, 2]);
    await spool.append(firstBatch.map((event) => event.line));
    const chunkPath = join(spool.directory, '000001.jsonl');
    const sizeBefore = (await stat(chunkPath)).size;
    const bytesBefore = await runDirBytes(spoolDir, runId);

    const overflow = makeEvents(range(3, 22));
    const capError = expectError(
      await failureOf(spool.append(overflow.map((event) => event.line))),
      SpoolCapExceededError,
    );
    expect(capError.code).toBe('spool_cap_exceeded');
    expect(capError.runId).toBe(runId);
    expect(capError.producerId).toBe(producerId);
    expect(capError.capBytes).toBe(capBytes);
    expect(capError.usedBytes).toBe(bytesBefore);
    expect(capError.unsentFirstSequence).toBe(1);
    expect(capError.unsentLastSequence).toBe(22);
    expect(capError.message).toContain(runId);
    expect(capError.message).toContain(producerId);
    expect(capError.message).toContain('unsent sequences 1..22');
    expect(capError.hint).toContain('EVIDGATE_SPOOL_MAX_BYTES');

    // All-or-nothing: the spool is byte-identical, no chunk was created, nothing acked.
    expect((await stat(chunkPath)).size).toBe(sizeBefore);
    expect(await chunkNames(spool.directory)).toEqual(['000001.jsonl']);
    expect(await runDirBytes(spoolDir, runId)).toBe(bytesBefore);
    expect((await collect(spool.readUnacked())).map((event) => event.sequence)).toEqual([1, 2]);
  });

  it('rotates at the line limit and never reuses a consumed chunk number', async () => {
    const spool = await open();
    // Two chunks written by the reporter before the CLI ever ran; both are fully acked.
    const seeded = makeEvents([1, 2, 3, 4]);
    await writeFile(
      join(spool.directory, '000001.jsonl'),
      `${seeded[0]?.line ?? ''}\n${seeded[1]?.line ?? ''}\n`,
      { mode: spoolFileMode },
    );
    await writeFile(
      join(spool.directory, '000002.jsonl'),
      `${seeded[2]?.line ?? ''}\n${seeded[3]?.line ?? ''}\n`,
      { mode: spoolFileMode },
    );
    const reopened = await open();
    await reopened.ack(4, 4);
    expect(await reopened.unsentRange()).toBeNull();

    // 1000 more events: the highest chunk (000002) fills to its line limit, then rotation
    // must continue at 000003 — never back at the consumed 000001.
    const batch = makeEvents(range(5, 1004));
    await reopened.append(batch.map((event) => event.line));
    expect(await chunkNames(reopened.directory)).toEqual([
      '000001.jsonl',
      '000002.jsonl',
      '000003.jsonl',
    ]);
    expect(await linesOf(join(reopened.directory, '000001.jsonl'))).toHaveLength(2);
    const secondLines = await linesOf(join(reopened.directory, '000002.jsonl'));
    expect(secondLines).toHaveLength(spoolChunkMaxLines);
    expect(secondLines[0]).toBe(seeded[2]?.line);
    expect(secondLines[spoolChunkMaxLines - 1]).toBe(batch[997]?.line);
    const thirdLines = await linesOf(join(reopened.directory, '000003.jsonl'));
    expect(sequencesOf(thirdLines)).toEqual([1003, 1004]);

    const unacked = await collect(reopened.readUnacked());
    expect(unacked.map((event) => event.sequence)).toEqual(range(5, 1004));
    expect(unacked[0]?.chunk).toBe('000002.jsonl');
    expect(unacked[0]?.lineNumber).toBe(3);

    // The next append continues in the highest chunk, it does not open 000004.
    await reopened.append(makeEvents([1005]).map((event) => event.line));
    expect(await chunkNames(reopened.directory)).toEqual([
      '000001.jsonl',
      '000002.jsonl',
      '000003.jsonl',
    ]);
    expect(await linesOf(join(reopened.directory, '000003.jsonl'))).toHaveLength(3);
  });

  it('file_modes: dirs 0700 and every file 0600', async () => {
    if (process.platform === 'win32') {
      // Windows has no POSIX mode bits; the AC skips this check there.
      return;
    }
    const spool = await open();
    await spool.append(makeEvents([1]).map((event) => event.line));
    await spool.ack(1, 1);
    await spool.withLock(async () => {
      expect(await modeOf(join(spool.directory, spoolLockFileName))).toBe(spoolFileMode);
    });
    expect(await modeOf(spool.directory)).toBe(spoolDirMode);
    expect(await modeOf(spool.runDirectory)).toBe(spoolDirMode);
    expect(await modeOf(join(spool.directory, '000001.jsonl'))).toBe(spoolFileMode);
    expect(await modeOf(join(spool.directory, spoolCursorFileName))).toBe(spoolFileMode);
  });

  it('rejects path-traversal run and producer ids', async () => {
    const invalid = ['', '.', '..', 'a/b', 'a\\b', 'bad\u0000id', 'x'.repeat(spoolIdMaxLength + 1)];
    for (const value of invalid) {
      expect(() => assertValidSpoolId('run_id', value)).toThrow(InvalidSpoolIdError);
      expect(() => assertValidSpoolId('producer_id', value)).toThrow(InvalidSpoolIdError);
    }
    // The writer's own charset and length boundary stays accepted.
    expect(() => assertValidSpoolId('producer_id', 'a.b_c-1')).not.toThrow();
    expect(() => assertValidSpoolId('producer_id', 'x'.repeat(spoolIdMaxLength))).not.toThrow();

    for (const overrides of [{ runId: '..' }, { producerId: '../escape' }] as const) {
      const failure = expectError(await failureOf(open(overrides)), InvalidSpoolIdError);
      expect(failure.code).toBe('invalid_spool_id');
    }
    // Nothing escaped: the refused opens created no directory at all.
    expect(await readdir(spoolDir)).toEqual([]);
  });

  it('refuses a symlinked producer directory', async () => {
    const victim = join(root, 'victim');
    await mkdir(victim);
    const runDirectory = join(spoolDir, runId);
    await mkdir(runDirectory, { recursive: true });
    await symlink(victim, join(runDirectory, producerId));

    const failure = await failureOf(open());
    expect(failure).toMatchObject({ code: 'spool_unsafe_path' });
    // The link itself is untouched and still points at the victim.
    expect((await readdir(victim)).length).toBe(0);
  });

  it('writes no credential material into any spool file', async () => {
    // A JWT-shaped value and a token, assembled at runtime: no literal secret in the spec.
    const secret = ['evg', 'tok', randomUUID().replace(/-/g, '')].join('_');
    const jwt = [
      ['eyJ', 'hbGciOi', 'JIUzI1NiJ9'].join(''),
      ['eyJ', 'zdWIiOiJ0ZXN0In0'].join(''),
      'c2lnbmF0dXJlLXZhbHVl',
    ].join('.');

    const spool = await open();
    await spool.append(makeEvents([1, 2]).map((event) => event.line));
    await spool.ack(2, 2);
    await spool.withLock(async () => {
      const lockText = await readFile(join(spool.directory, spoolLockFileName), 'utf8');
      // Exactly `{ pid, started_at }`, so no credential field can hide in the lock.
      expect([...lockText.matchAll(/"([a-z_]+)":/gu)].map((match) => match[1])).toEqual([
        'pid',
        'started_at',
      ]);
    });

    const cursor: unknown = JSON.parse(
      await readFile(join(spool.directory, spoolCursorFileName), 'utf8'),
    );
    if (typeof cursor !== 'object' || cursor === null) {
      throw new Error('cursor.json is not an object');
    }
    expect(Object.keys(cursor).sort()).toEqual([
      'acked_count',
      'last_acked_sequence',
      'next_unacked_sequence',
      'producer_id',
      'updated_at',
    ]);

    for (const file of await allFiles(spoolDir)) {
      const text = await readFile(file, 'utf8');
      expect(text).not.toContain(secret);
      expect(text).not.toContain(jwt);
      expect(text).not.toMatch(/Bearer|https:\/\/[^\s:@/]+:[^\s@/]+@/u);
    }
  });

  it('purge removes the producer directory and leaves the run directory', async () => {
    const spool = await open();
    await spool.append(makeEvents([1]).map((event) => event.line));
    await spool.purge();
    expect(await failureOf(stat(spool.directory))).toMatchObject({ code: 'ENOENT' });
    expect((await stat(spool.runDirectory)).isDirectory()).toBe(true);

    const missing = await failureOf(open({ createIfMissing: false }));
    expect(missing).toBeInstanceOf(SpoolMissingError);
    expect(missing).toMatchObject({ code: 'spool_missing', producerId });
    expect(await failureOf(open({ createIfMissing: false, producerId: 'other' }))).toBeInstanceOf(
      SpoolMissingError,
    );
  });

  it('lists producer ids and sums the run directory bytes without following symlinks', async () => {
    const first = await open({ producerId: 'b-producer' });
    const second = await open({ producerId: 'a-producer' });
    await first.append(makeEvents([1]).map((event) => event.line));
    await second.append(makeEvents([1]).map((event) => event.line));
    // Neither a foreign-named directory nor a symlink is a producer the CLI will send.
    await mkdir(join(first.runDirectory, 'bad name'));
    await symlink(first.directory, join(first.runDirectory, 'linked-producer'));

    expect(await listProducers(spoolDir, runId)).toEqual(['a-producer', 'b-producer']);
    const expected =
      (await stat(join(first.directory, '000001.jsonl'))).size +
      (await stat(join(second.directory, '000001.jsonl'))).size;
    expect(await runDirBytes(spoolDir, runId)).toBe(expected);
    expect(await runDirBytes(spoolDir, 'no-such-run')).toBe(0);
    expect(await listProducers(spoolDir, 'no-such-run')).toEqual([]);
  });

  it('exposes the documented defaults and rejects a non-positive cap', async () => {
    expect(defaultSpoolMaxBytes).toBe(268_435_456);
    expect(await failureOf(open({ maxBytes: 0 }))).toMatchObject({
      code: 'invalid_spool_max_bytes',
    });
    expect(await failureOf(open({ maxBytes: 1.5 }))).toMatchObject({
      code: 'invalid_spool_max_bytes',
    });
  });

  it('refuses invalid lines before touching the chunk', async () => {
    const spool = await open();
    await expect(spool.append(['{not json'])).rejects.toMatchObject({
      code: 'invalid_spool_line',
    });
    await expect(spool.append([makeLine(1), makeLine(1, randomUUID())])).rejects.toMatchObject({
      code: 'invalid_spool_line',
    });
    await expect(spool.append([`${makeLine(1)}\n${makeLine(2)}`])).rejects.toMatchObject({
      code: 'invalid_spool_line',
    });
    expect(await chunkNames(spool.directory)).toEqual([]);
  });
});
