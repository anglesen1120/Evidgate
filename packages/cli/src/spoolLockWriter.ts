// Reading the reporter's own `writer.lock`. The CLI never creates, takes over or deletes
// it: it is the only module allowed to refuse work while a live reporter is writing, and a
// lock it cannot inspect must refuse rather than read as absent.
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

import { CliError } from './errors.js';
import { isProcessAlive, readSpoolLockPath } from './spoolLockRead.js';
import { staleProbeDelayMs, type SpoolLockFile } from './spoolLockTypes.js';

/**
 * The reporter's own lock (`@evidgate/playwright-reporter` `writerLock.ts`):
 * `<producer_dir>/writer.lock`, mode 0600, whole content `{ pid, started_at }`, held for
 * the life of a writer. It is never the CLI's: only {@link assertNoLiveWriter} reads it.
 */
export const writerLockFileName = 'writer.lock';

/** Options of {@link assertNoLiveWriter}; both are test seams. */
export interface WriterLockCheckOptions {
  /** Liveness probe. Defaults to {@link isProcessAlive}; injected by tests. */
  readonly isProcessAlive?: (pid: number) => boolean;
  /** Pause before the confirming re-read of an unreadable lock. Defaults to 10 ms. */
  readonly probeDelayMs?: number;
}

/**
 * `spool_locked` (exit 3): the reporter is writing this producer right now, so
 * nothing may read, ack, finalize or purge its chunk stream. The message names the pid.
 */
export class WriterLockedError extends CliError {
  /** The `writer.lock` that is held. */
  readonly lockPath: string;
  /** The live reporter's pid. */
  readonly holderPid?: number;

  constructor(lockPath: string, holderPid?: number) {
    super(
      holderPid === undefined
        ? `the reporter's writer lock is held by another process (${lockPath})`
        : `the reporter is writing this producer right now (live process ${holderPid}, ${lockPath})`,
      {
        code: 'spool_locked',
        hint: 'the reporter is still writing this producer; wait for the test run to finish, then retry',
      },
    );
    this.name = 'WriterLockedError';
    this.lockPath = lockPath;
    if (holderPid !== undefined) this.holderPid = holderPid;
  }
}

/**
 * Reads `<producer_dir>/writer.lock`, or `null` when there is none it can trust (absent,
 * symlink/FIFO/device, oversized, or unparseable). The classification is the one
 * {@link readSpoolLockPath} applies, so a foreign or torn file can neither be followed nor
 * read as a lock; a real inspection failure throws `spool_unsafe_path`.
 */
export function readWriterLock(directory: string): Promise<SpoolLockFile | null> {
  return readSpoolLockPath(join(directory, writerLockFileName));
}

/**
 * Refuses when the reporter holds a *live* `writer.lock`. Called under the
 * CLI's own `spool.lock`, so a second CLI can never be inside the same producer directory.
 *
 * - live pid -> `WriterLockedError` (`spool_locked`, exit 3), naming the pid. Nothing is
 *   read, acked or purged, and the lock is not touched: the CLI never waits for, takes
 *   over or deletes a reporter's lock, so it can never delete a live successor's.
 * - dead pid -> the lock is ignored and left in place. The reporter's next start breaks it
 *   with its own verified rename-aside takeover, which is the only writer that may.
 * - unparseable / non-regular / oversized / `ENOENT|ELOOP|ENXIO` -> absent, after one
 *   re-read so a foreign writer caught mid-create is not mistaken for a dead one.
 * - any other errno -> `spool_unsafe_path` (exit 3), never "absent": a lock the CLI could
 *   not inspect must not turn into a purge of a live reporter's spool.
 */
export async function assertNoLiveWriter(
  directory: string,
  options: WriterLockCheckOptions = {},
): Promise<void> {
  const alive = options.isProcessAlive ?? isProcessAlive;
  const lockPath = join(directory, writerLockFileName);
  for (let probe = 0; probe < 2; probe += 1) {
    if (probe > 0) await delay(options.probeDelayMs ?? staleProbeDelayMs);
    const holder = await readWriterLock(directory);
    if (holder === null) continue;
    if (!alive(holder.pid)) return;
    throw new WriterLockedError(lockPath, holder.pid);
  }
}

/**
 * What a caller may ask {@link ProducerSpool.withLock} to guard beyond the CLI lock.
 * Absent (the default) is today's behaviour: the upload journal path must not be blocked
 * by a live reporter, only the chunk readers/purgers are.
 */
export interface ProducerLockOptions extends WriterLockCheckOptions {
  /** Refuse with `spool_locked` while the reporter holds `writer.lock`. */
  readonly requireNoLiveWriter?: boolean;
}
