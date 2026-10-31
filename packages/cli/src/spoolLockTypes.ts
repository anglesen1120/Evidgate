// The shapes and the on-disk constants of the two producer-directory locks: the CLI's own
// `spool.lock` and the reporter's `writer.lock`. They live together so the file names, the
// mode and the size bound cannot drift between the writer that creates a lock and the
// reader that has to trust it.
import { CliError } from './errors.js';

/** The lock file name inside a producer directory. */
export const spoolLockFileName = 'spool.lock';
/** `spool.lock` is created 0600 like every other spool file. */
export const spoolLockFileMode = 0o600;

/**
 * A lock file written by an older writer (or by anything else) may still be empty for a
 * moment; re-read once after this pause before deciding that an unparseable lock is stale.
 * A lock we create is never observable empty, so this only covers a foreign writer.
 */
export const staleProbeDelayMs = 10;
/** Bounded takeover attempts, so two processes racing over a dead lock cannot loop. */
export const defaultTakeoverAttempts = 3;
/** No lock record is longer than a few dozen bytes; anything larger is not a lock. */
export const maxLockFileBytes = 1024;

/** The lock file's whole content: no token, no argv, nothing a support bundle could leak. */
export interface SpoolLockFile {
  readonly pid: number;
  readonly startedAt: string;
}

export interface SpoolLockOptions {
  /** Pid recorded in the lock. Defaults to `process.pid`; injected by tests. */
  readonly pid?: number;
  /** Clock for `started_at`. Defaults to `new Date()`. */
  readonly now?: () => Date;
  /** Liveness probe. Defaults to the platform `process.kill(pid, 0)` check; injected by tests. */
  readonly isProcessAlive?: (pid: number) => boolean;
  /** Attempts allowed while breaking a stale lock. Defaults to 3. */
  readonly maxTakeoverAttempts?: number;
}

/** `spool_locked`: a live process holds this producer directory (exit 3, contracts/cli.md). */
export class SpoolLockedError extends CliError {
  /** The lock file that is held. */
  readonly lockPath: string;
  /** The live holder's pid, when the lock file named one. */
  readonly holderPid?: number;

  constructor(lockPath: string, holderPid?: number) {
    super(
      holderPid === undefined
        ? `the spool directory is locked by another process (${lockPath})`
        : `the spool directory is locked by live process ${holderPid} (${lockPath})`,
      {
        code: 'spool_locked',
        hint: 'wait for the running send/finalize to finish, then retry',
      },
    );
    this.name = 'SpoolLockedError';
    this.lockPath = lockPath;
    if (holderPid !== undefined) this.holderPid = holderPid;
  }
}
