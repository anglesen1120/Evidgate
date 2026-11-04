// The spool's bounds and acknowledgement: the documented defaults, lines refused before the
// chunk is touched, a line past the per-line cap, a line straddling several read blocks, and
// the fsync policy that makes a lower chunk durable once per process and never reopens it.
import { randomUUID } from 'node:crypto';
import { appendFile, readFile, symlink, unlink, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { beforeEach, describe, expect, it } from 'vitest';

import {
  SpoolCorruptError,
  defaultSpoolMaxBytes,
  maxSpoolLineBytes,
  spoolFileMode,
} from './spool.js';
import {
  chunkNames,
  collect,
  expectError,
  failureOf,
  makeEvents,
  makeLine,
  producerId,
  seedChunk,
  useSpoolFixture,
} from './spool.testing.js';

describe('ProducerSpool', () => {
  const fixture = useSpoolFixture();
  const { open } = fixture;

  let root: string;

  beforeEach(() => {
    root = fixture.root;
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

  // Review P2 "ack fsyncs every chunk on every call": the cursor's durability must not cost
  // O(chunks) syncs per ack. Below the highest chunk nothing is ever written again
  // (rotation always moves to `highest + 1`), so those chunks are synced at most once per
  // process. Pinned with a dangling symlink: re-opening an already-synced chunk would fail
  // with `spool_unsafe_path`, so the ack only passing proves it did not touch the chunk.
  it('ack syncs a lower chunk once and never re-opens it', async () => {
    if (process.platform === 'win32') return; // the swap below is POSIX-only
    const spool = await open();
    const firstChunk = join(spool.directory, '000001.jsonl');
    await seedChunk(firstChunk, 1, 2);
    await seedChunk(join(spool.directory, '000002.jsonl'), 3, 2);

    // First ack: 000001 is below the highest chunk and not yet synced by this process, so
    // it is opened and fsynced once.
    await spool.ack(4, 4);

    // Swap it for a dangling symlink: any later open would be refused.
    await unlink(firstChunk);
    await symlink(join(root, 'gone.jsonl'), firstChunk);

    await spool.append(makeEvents([5, 6]).map((event) => event.line));
    await spool.ack(6, 2);
    expect(await spool.cursor()).toMatchObject({ lastAckedSequence: 6, ackedCount: 6 });
  });

  it('ack still syncs a lower chunk it has not made durable itself', async () => {
    if (process.platform === 'win32') return; // the swap below is POSIX-only
    // A fresh process (fresh in-memory record) must not trust chunks it never fsynced: the
    // swapped chunk is opened, so the ack fails loudly instead of advancing past it.
    const spool = await open();
    await seedChunk(join(spool.directory, '000002.jsonl'), 1, 2);
    await symlink(join(root, 'gone.jsonl'), join(spool.directory, '000001.jsonl'));

    expect(await failureOf(spool.ack(2, 2))).toMatchObject({ code: 'spool_unsafe_path' });
    expect(await spool.cursor()).toMatchObject({ lastAckedSequence: 0, ackedCount: 0 });
  });

  // The piece list must survive a block boundary: a leftover fragment is COPIED, because
  // the next read reuses the same 64 KiB buffer. A line of a few hundred KiB round-trips
  // byte-exactly only if that copy (and the single-write segment join) is right.
  it('round-trips a legal line that straddles several read blocks byte-exactly', async () => {
    const spool = await open();
    const padded = JSON.stringify({
      schema_version: 1,
      event_id: randomUUID(),
      producer_id: producerId,
      sequence: 1,
      type: 'run.log',
      payload: 'p'.repeat(3 * 64 * 1024),
    });
    await spool.append([padded]);

    const [event] = await collect(spool.readUnacked());
    expect(event?.line).toBe(padded);
    expect(event?.lineBytes).toBe(Buffer.byteLength(padded, 'utf8') + 1);
    expect(await readFile(join(spool.directory, '000001.jsonl'), 'utf8')).toBe(`${padded}\n`);
  });

  // Review P2 "readChunkLines has no line cap": one line is bounded by the intake's own
  // per-line guard; past it the line cannot be legal, so the scan is `spool_corrupt`
  // instead of buffering without limit.
  it('spool_corrupt for a line past the per-line cap, naming chunk + line', async () => {
    const spool = await open();
    const chunkPath = join(spool.directory, '000001.jsonl');
    await writeFile(chunkPath, `${makeLine(1)}\n`, { mode: spoolFileMode });
    // Exactly one byte over the cap, appended in two calls so the spec holds no copy of it.
    await appendFile(chunkPath, Buffer.alloc(maxSpoolLineBytes + 1, 0x78));
    await appendFile(chunkPath, '\n');

    const read = expectError(await failureOf(collect(spool.readUnacked())), SpoolCorruptError);
    expect(read.code).toBe('spool_corrupt');
    expect(read.chunk).toBe('000001.jsonl');
    expect(read.lineNumber).toBe(2);
    expect(read.message).toContain(String(maxSpoolLineBytes));

    // The appender scans the highest chunk the same way, so it refuses to write behind it.
    const scanning = expectError(await failureOf(spool.append([makeLine(2)])), SpoolCorruptError);
    expect(scanning.chunk).toBe('000001.jsonl');
    expect(scanning.lineNumber).toBe(2);
  });
});
