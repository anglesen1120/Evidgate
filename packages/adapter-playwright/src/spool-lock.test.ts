// 04.3.3-T1 (AC2/T84, tier T3): the reporter's single-writer lock. Two live processes that
// share `(run_id, producer_id)` must not interleave lines, a lock left by a dead process is
// taken over, and a lock can never be taken from a live holder — including through a planted
// symlink. The design is the CLI's `packages/cli/src/spoolLock.ts` ported synchronously
// (`writerLock.ts`); these specs pin the behaviour, not the implementation.
import {
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import {
  EvidgateSpoolWriterLockedError,
  readWriterLock,
  SHARDS_TMP_FILE,
  SpoolWriter,
  WRITER_LOCK_FILE,
  type SpoolWriterOptions,
} from './spool.js';
import { acquireWriterLock, WRITER_LOCK_FILE_MODE } from './writerLock.js';
import { lockRecord } from './writerLockRecord.js';

const isWindows = process.platform === 'win32';
const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-spool-lock-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function envelope(producerId: string): UnsignedEnvelope {
  return buildEnvelope({
    producer_id: producerId,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'test.started',
    ext: { schema: EXT_SCHEMA },
  });
}

/** A pid that is definitely not this test process, used as "the crashed writer". */
const CRASHED_PID = 424242;

/**
 * Liveness seam: only this process is alive, so a writer opened with
 * {@link CRASHED_PID} reads as a crashed predecessor no matter what runs on the host.
 */
function onlySelfAlive(pid: number): boolean {
  return pid === process.pid;
}

function writerOptions(
  spool: string,
  overrides: Partial<SpoolWriterOptions> = {},
): SpoolWriterOptions {
  return {
    spoolDir: spool,
    runId: 'run-1',
    producerId: 'pw-0',
    lock: { isProcessAlive: onlySelfAlive, probeDelayMs: 0 },
    ...overrides,
  };
}

function lockPath(spool: string): string {
  return join(spool, 'run-1', 'pw-0', WRITER_LOCK_FILE);
}

describe('spool single-writer lock', () => {
  it('refuses a second live writer and names the holder pid', () => {
    const spool = tempSpool();
    const first = new SpoolWriter(
      writerOptions(spool, {
        lock: { pid: CRASHED_PID, isProcessAlive: () => true, probeDelayMs: 0 },
      }),
    );
    first.append(envelope('pw-0'));
    first.flush();

    let error: unknown;
    try {
      new SpoolWriter(writerOptions(spool, { lock: { isProcessAlive: () => true } }));
    } catch (thrown) {
      error = thrown;
    }
    expect(error).toBeInstanceOf(EvidgateSpoolWriterLockedError);
    const locked = error as EvidgateSpoolWriterLockedError;
    expect(locked.code).toBe('spool_writer_locked');
    expect(locked.holderPid).toBe(CRASHED_PID);
    expect(locked.lockPath).toBe(lockPath(spool));
    expect(locked.message).toContain(String(CRASHED_PID));
    // The refusal changed nothing: the holder's record is intact and one line is durable.
    expect(readWriterLock(join(spool, 'run-1', 'pw-0'))?.pid).toBe(CRASHED_PID);
    expect(
      readFileSync(join(spool, 'run-1', 'pw-0', '000001.jsonl'), 'utf8')
        .trim()
        .split('\n'),
    ).toHaveLength(1);

    first.close();
    // Once the holder closed, the directory is free again.
    const second = new SpoolWriter(writerOptions(spool));
    expect(second.nextSequenceNumber).toBe(2);
    second.close();
  });

  it('writes only {pid, started_at} in a 0600 writer.lock and removes it on close', () => {
    if (isWindows) return; // Windows has no POSIX mode bits.
    const spool = tempSpool();
    const writer = new SpoolWriter(
      writerOptions(spool, {
        lock: { pid: CRASHED_PID, isProcessAlive: () => true, probeDelayMs: 0 },
      }),
    );

    const path = lockPath(spool);
    expect(statSync(path).mode & 0o777).toBe(0o600);
    expect(JSON.parse(readFileSync(path, 'utf8'))).toEqual({
      pid: CRASHED_PID,
      started_at: expect.any(String) as unknown,
    });
    // Nothing else is ever in the record: no token, no argv, no paths.
    expect(Object.keys(JSON.parse(readFileSync(path, 'utf8')) as object).sort()).toEqual([
      'pid',
      'started_at',
    ]);

    writer.close();
    expect(existsSync(path)).toBe(false);
    expect(readWriterLock(join(spool, 'run-1', 'pw-0'))).toBeNull();
  });

  it('takes over the lock of a dead process', () => {
    const spool = tempSpool();
    // The predecessor crashed with CRASHED_PID; its record is on disk with nothing holding it.
    const crashed = new SpoolWriter(
      writerOptions(spool, {
        lock: { pid: CRASHED_PID, isProcessAlive: onlySelfAlive, probeDelayMs: 0 },
      }),
    );
    crashed.append(envelope('pw-0'));
    crashed.flush();
    // (No close: this is the "crashed without release" state, lock still on disk.)

    const resumed = new SpoolWriter(writerOptions(spool));
    expect(resumed.nextSequenceNumber).toBe(2);
    expect(readWriterLock(join(spool, 'run-1', 'pw-0'))?.pid).toBe(process.pid);
    resumed.close();
  });

  it('never deletes the successor lock when the displaced holder closes late', () => {
    const spool = tempSpool();
    const crashed = new SpoolWriter(
      writerOptions(spool, {
        lock: { pid: CRASHED_PID, isProcessAlive: onlySelfAlive, probeDelayMs: 0 },
      }),
    );
    crashed.append(envelope('pw-0'));

    const successor = new SpoolWriter(writerOptions(spool));
    // Release is content checked: the old holder must not unlink a lock that is not its own.
    crashed.close();
    expect(readWriterLock(join(spool, 'run-1', 'pw-0'))?.pid).toBe(process.pid);
    expect(() => successor.append(envelope('pw-0'))).not.toThrow();

    successor.close();
    expect(existsSync(lockPath(spool))).toBe(false);
  });

  it('does not follow a symlink planted at writer.lock', () => {
    if (isWindows) return;
    const spool = tempSpool();
    const dir = join(spool, 'run-1', 'pw-0');
    mkdirSync(dir, { recursive: true });
    const victim = join(spool, 'victim.txt');
    writeFileSync(victim, 'do not touch');
    symlinkSync(victim, lockPath(spool));

    const writer = new SpoolWriter(writerOptions(spool));

    // The link is never followed (O_NOFOLLOW -> ELOOP reads as "not a lock"), so the victim is
    // intact and the path now holds OUR regular record; the link was moved aside, not followed.
    expect(readFileSync(victim, 'utf8')).toBe('do not touch');
    const stats = lstatSync(lockPath(spool));
    expect(stats.isFile()).toBe(true);
    expect(stats.isSymbolicLink()).toBe(false);
    expect(readWriterLock(dir)?.pid).toBe(process.pid);

    writer.close();
    expect(readFileSync(victim, 'utf8')).toBe('do not touch');
  });

  it('takes over an unreadable lock record after the probe re-read', () => {
    const spool = tempSpool();
    const dir = join(spool, 'run-1', 'pw-0');
    mkdirSync(dir, { recursive: true });
    // Neither a torn record nor a foreign file names a live holder, so it is stale.
    writeFileSync(lockPath(spool), '{not a lock');

    const writer = new SpoolWriter(writerOptions(spool));
    expect(readWriterLock(dir)?.pid).toBe(process.pid);
    writer.close();
  });

  it('puts the moved lock back and reports it when it stopped matching', () => {
    const spool = tempSpool();
    const dir = join(spool, 'run-1', 'pw-0');
    mkdirSync(dir, { recursive: true });
    const path = lockPath(spool);
    const successor = lockRecord(7777, 'live-successor');
    writeFileSync(path, lockRecord(CRASHED_PID, 'crashed'), { mode: WRITER_LOCK_FILE_MODE });
    // The liveness probe runs after the record was read and before the rename, so replacing
    // the lock there is a writer that swapped it inside the takeover window: what the rename
    // moves is then not the record we observed as stale, and must not be destroyed.
    const alive = (pid: number): boolean => {
      if (pid === CRASHED_PID) writeFileSync(path, successor, { mode: WRITER_LOCK_FILE_MODE });
      return pid !== CRASHED_PID;
    };

    let error: unknown;
    try {
      // One attempt only: a `retry` outcome would end in a pid-less refusal, so the reported
      // holder proves the arm answered `held` instead of leaving the slot to a fresh link.
      acquireWriterLock(dir, {
        pid: 5555,
        isProcessAlive: alive,
        probeDelayMs: 0,
        maxTakeoverAttempts: 1,
      });
    } catch (thrown) {
      error = thrown;
    }

    // The displaced record owns the path again and the refusal names it, rather than the
    // takeover consuming a lock it never observed.
    expect(error).toBeInstanceOf(EvidgateSpoolWriterLockedError);
    const locked = error as EvidgateSpoolWriterLockedError;
    expect(locked.holderPid).toBe(7777);
    expect(readFileSync(path, 'utf8')).toBe(successor);
    expect(readdirSync(dir)).toEqual([WRITER_LOCK_FILE]);
  });

  it('does not serialise writers of different producers of the same run', () => {
    const spool = tempSpool();
    const first = new SpoolWriter(writerOptions(spool, { producerId: 'pw-0' }));
    const second = new SpoolWriter(writerOptions(spool, { producerId: 'pw-1' }));

    first.append(envelope('pw-0'));
    second.append(envelope('pw-1'));
    first.close();
    second.close();

    expect(readdirSync(join(spool, 'run-1')).sort()).toEqual(['pw-0', 'pw-1']);
  });

  it('releases the lock when close() throws on the final record write', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter(writerOptions(spool));
    writer.append(envelope('pw-0'));
    // A directory at the record's tmp path makes the close-time atomic write fail.
    mkdirSync(join(spool, 'run-1', 'pw-0', SHARDS_TMP_FILE));

    expect(() => writer.close()).toThrow();
    expect(existsSync(lockPath(spool))).toBe(false);
    // A later writer can take the directory over without waiting for a stale-lock takeover
    // once the planted obstacle is gone.
    rmSync(join(spool, 'run-1', 'pw-0', SHARDS_TMP_FILE), { recursive: true, force: true });
    const next = new SpoolWriter(writerOptions(spool));
    next.close();
  });

  it('refuses to lock a producer directory it cannot own (planted directory at the lock)', () => {
    const spool = tempSpool();
    const dir = join(spool, 'run-1', 'pw-0');
    mkdirSync(join(dir, WRITER_LOCK_FILE), { recursive: true });

    // A directory at the lock path is not a lock record, so the takeover moves it aside; a
    // directory cannot be unlinked, so the acquisition fails loudly instead of pretending to
    // hold the lock.
    expect(() => new SpoolWriter(writerOptions(spool))).toThrow();
    // The refusal left the pre-existing entry in place (moved aside under a `.stale` name).
    expect(readdirSync(dir).some((name) => name.endsWith('.stale'))).toBe(true);
    expect(readWriterLock(dir)).toBeNull();
  });
});
