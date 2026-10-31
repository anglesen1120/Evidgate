// The shapes and the on-disk constants of the producer-directory locks: the CLI's own
// `spool.lock`, the takeover mutex that serialises breaking a stale one, and the reporter's
// `writer.lock`. They live together so the file names, the mode and the size bound cannot
// drift between the writer that creates a lock and the reader that has to trust it.
import { CliError } from './errors.js';

/** The lock file name inside a producer directory. */
export const spoolLockFileName = 'spool.lock';
/** `spool.lock` is created 0600 like every other spool file. */
export const spoolLockFileMode = 0o600;
/**
 * The takeover mutex beside `spool.lock`: only its holder may inspect and break a stale
 * lock, so the read-then-rename cannot interleave with a second contender.
 */
export const takeoverLockFileName = `${spoolLockFileName}.takeover`;

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

/**
 * Seams that park a contender inside the stale-lock takeover or its release so a spec can
 * interleave a competitor deterministically. They are never set in production and change
 * no outcome on their own; each one runs exactly where its name says.
 */
export interface SpoolLockHooks {
  /** After a stale lock was observed and before the takeover mutex is taken. */
  readonly beforeTakeover?: () => Promise<void>;
  /** While the observed stale lock is quarantined and the takeover mutex is still held. */
  readonly afterQuarantine?: () => Promise<void>;
  /** After the lock at the path was read and before its inode is confirmed again. */
  readonly afterEntryRead?: () => Promise<void>;
  /** After a dead mutex was re-checked and immediately before its reclaim rename. */
  readonly beforeDeadMutexReclaim?: () => Promise<void>;
  /** After the holder unlinks its matched record, before quarantine scavenging. */
  readonly afterReleaseUnlink?: () => Promise<void>;
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
  /** Takeover seams for the race specs; absent in production. */
  readonly hooks?: SpoolLockHooks;
}

/**
 * A lock this process holds: where it is, what it records, and which inode carries it, so
 * release can still find the record after a takeover renamed it aside.
 */
export interface HeldSpoolLock {
  readonly path: string;
  readonly pid: number;
  readonly startedAt: string;
  readonly dev: number;
  readonly ino: number;
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
