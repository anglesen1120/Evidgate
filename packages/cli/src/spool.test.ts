// The spool's write path: the cursor a restart resumes from, a torn tail and the corrupt
// chunk/line refusals, the cap that refuses before writing anything, and chunk rotation.
//
// Everything runs against a real temporary directory: the spool's contract IS its
// on-disk layout, so a mocked filesystem would prove nothing. No network, no timers.
import { appendFile, readFile, stat, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { beforeEach, describe, expect, it } from 'vitest';

import {
  SpoolCapExceededError,
  SpoolCorruptError,
  runDirBytes,
  spoolChunkMaxLines,
  spoolFileMode,
} from './spool.js';
import {
  chunkNames,
  collect,
  expectError,
  failureOf,
  linesOf,
  makeEvents,
  producerId,
  range,
  runId,
  sequencesOf,
  useSpoolFixture,
} from './spool.testing.js';

describe('ProducerSpool', () => {
  const fixture = useSpoolFixture();
  const { open } = fixture;

  let spoolDir: string;

  beforeEach(() => {
    spoolDir = fixture.spoolDir;
  });

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
});
