// The crash-safe upload journal, one record per upload under the producer directory so a
// finalize purge removes it too. The name digests every `upload-url` value, so a different
// execution, kind or attempt cannot resume another row's mint. A record never carries the
// presigned URL or a token: both are bearer credentials and the file outlives the process.
//
// Every write is tmp + fsync + rename + directory fsync under the producer's exclusive
// `spool.lock` (`spoolLock.ts`), the same lock `events send` and `runs finalize` take, so
// two CLI processes can never interleave a journal write. A busy lock is waited for with
// bounded backoff instead of failing at once, because a running `events send` must not turn
// a journal write into `spool_locked`. The two calls get different budgets: the pre-PUT
// `write` that records a minted row may wait up to the upload deadline (`deadlineAt`),
// while the post-transfer best-effort `update` passes a short per-call `maxWaitMs` (2 s in
// `upload.ts`) and then swallows `spool_locked`, so a busy lock can never spend the deadline
// the `complete` call still needs. The directory is created 0700 and hardened like the rest
// of the spool; a symlinked or foreign-owned `uploads/` entry, a planted symlink for a
// journal and a record that does not parse are all refused (`spool_unsafe_path`,
// `spool_corrupt`) instead of trusted. A record from an older CLI (a `<artifact_id>.json`
// name, or one without the identity fields) is ignored as a resume candidate: it is never
// parsed into a row, so it can never crash a resume and can never claim a mint.
//
// A missing `EVIDGATE_SPOOL_DIR` means no journal: the upload still works, it simply has
// no resume state across process restarts (`journalFor` returns `undefined`).
//
// The record codec (`uploadJournalRecord`) and the shapes (`uploadJournalTypes`) are
// separate modules; this one owns the directory, the lock budget and the resume lookup.
import { readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

import { hardenDirectory, mkdirOwned, readVerifiedText } from './spool.js';
import {
  assertArtifactId,
  assertEntry,
  corrupt,
  isJournalFileName,
  isSpoolLocked,
  parseEntry,
  sameIdentity,
  uploadIdentityDigest,
  writeRecord,
} from './uploadJournalRecord.js';
import {
  uploadsDirectoryName,
  type UploadIdentity,
  type UploadJournalEntry,
  type UploadJournalLockOptions,
  type UploadJournalOptions,
  type UploadJournalPatch,
  type UploadJournalWriteOptions,
} from './uploadJournalTypes.js';

export {
  artifactIdPattern,
  isSpoolLocked,
  journalFileName,
  uploadIdentityDigest,
} from './uploadJournalRecord.js';
export {
  uploadsDirectoryName,
  type UploadIdentity,
  type UploadJournalEntry,
  type UploadJournalLockOptions,
  type UploadJournalOptions,
  type UploadJournalPatch,
  type UploadJournalWriteOptions,
} from './uploadJournalTypes.js';

/** First attempt's wait, doubling up to {@link lockRetryMaxMs}. */
const lockRetryBaseMs = 25;
const lockRetryMaxMs = 500;
/** Fallback bound when the caller supplies no upload deadline at all. */
const defaultLockWaitMs = 10_000;

/** Builds the per-producer journal, or `undefined` when there is no spool root. */
export async function journalFor(input: {
  readonly spoolDir: string | undefined;
  readonly producerDirectory: string;
  readonly withLock: <T>(fn: () => Promise<T>) => Promise<T>;
  readonly lock?: UploadJournalLockOptions;
}): Promise<UploadJournal | undefined> {
  if (input.spoolDir === undefined) {
    return undefined;
  }
  return UploadJournal.open({
    producerDirectory: input.producerDirectory,
    withLock: input.withLock,
    ...input.lock,
  });
}

export class UploadJournal {
  /** `<producer_dir>/uploads` — created lazily, 0700, under the producer lock. */
  readonly uploadsDirectory: string;
  private readonly withLock: <T>(fn: () => Promise<T>) => Promise<T>;
  private readonly now: () => number;
  private readonly sleep: (milliseconds: number) => Promise<void>;
  private readonly lockDeadlineAt: number;
  private readonly maxWaitMs: number;

  private constructor(o: UploadJournalOptions) {
    this.uploadsDirectory = join(o.producerDirectory, uploadsDirectoryName);
    this.withLock = o.withLock;
    this.now = o.now ?? Date.now;
    this.sleep = o.sleep ?? ((milliseconds) => delay(milliseconds));
    this.lockDeadlineAt = o.deadlineAt ?? Number.POSITIVE_INFINITY;
    this.maxWaitMs =
      o.maxWaitMs ?? (o.deadlineAt === undefined ? defaultLockWaitMs : Number.POSITIVE_INFINITY);
  }

  static async open(o: UploadJournalOptions): Promise<UploadJournal> {
    const journal = new UploadJournal(o);
    await journal.ensureDirectory();
    return journal;
  }

  /**
   * The row a restarted upload continues: the highest `mint_no` whose identity matches on
   * EVERY field (digest, size, kind, execution key, attempt, media type). `complete_done`
   * means the artifact is already committed ready, so the call returns without a request; a
   * row without it is resumed (only `complete` when `put_done`), so a re-run after a crash
   * or after a rejection converges on the same artifact row instead of minting a second one.
   * The same bytes under a different execution/kind/attempt are a different upload and get
   * no match — deliberately, because they are a different server row.
   */
  async findResumable(identity: UploadIdentity): Promise<UploadJournalEntry | undefined> {
    let best: UploadJournalEntry | undefined;
    for (const entry of await this.candidates(identity)) {
      if (!sameIdentity(entry, identity)) continue;
      if (best === undefined || entry.mint_no > best.mint_no) best = entry;
    }
    return best;
  }

  /**
   * The next mint number for this identity: one past the highest row ever written, so an
   * expired presigned URL gets a fresh artifact row instead of replaying the stored
   * response of a dead one, and a restart derives the same number it would have used.
   */
  async nextMintNo(identity: UploadIdentity): Promise<number> {
    let highest = -1;
    for (const entry of await this.candidates(identity)) {
      if (sameIdentity(entry, identity)) highest = Math.max(highest, entry.mint_no);
    }
    return highest + 1;
  }

  /**
   * Reads every current record; a record that does not parse is `spool_corrupt`, never
   * skipped. A record from an older CLI (a legacy name, or one without the identity fields)
   * is not a current record and is ignored.
   */
  async entries(): Promise<readonly UploadJournalEntry[]> {
    const entries: UploadJournalEntry[] = [];
    for (const name of await this.recordNames()) {
      const entry = parseEntry(name, await readVerifiedText(join(this.uploadsDirectory, name)));
      if (entry !== undefined) entries.push(entry);
    }
    return entries;
  }

  /** Writes one record atomically, under the producer lock (tmp + fsync + rename + dir fsync). */
  async write(entry: UploadJournalEntry): Promise<void> {
    assertEntry(entry);
    await this.withBoundedLock(async () => {
      await this.ensureDirectory();
      await writeRecord(this.uploadsDirectory, entry);
    });
  }

  /**
   * Applies a state change to an existing record; a missing record is `spool_corrupt`.
   * `options.maxWaitMs` caps THIS call's wait for the lock, which is how the post-transfer
   * best-effort updates keep a busy `events send` from spending the upload deadline.
   */
  async update(
    artifactId: string,
    patch: UploadJournalPatch,
    options: UploadJournalWriteOptions = {},
  ): Promise<UploadJournalEntry> {
    assertArtifactId(artifactId);
    return this.withBoundedLock(async () => {
      const suffix = `-${artifactId}.json`;
      const names = (await this.recordNames()).filter((name) => name.endsWith(suffix));
      const name = names[0];
      if (name === undefined) {
        throw corrupt(`${suffix.slice(1)}`, artifactId);
      }
      if (names.length > 1) {
        throw corrupt(`${names.length} records`, artifactId);
      }
      const current = parseEntry(name, await readVerifiedText(join(this.uploadsDirectory, name)));
      if (current === undefined) {
        throw corrupt(name, artifactId);
      }
      const next: UploadJournalEntry = {
        ...current,
        put_done: patch.put_done ?? current.put_done,
        complete_done: patch.complete_done ?? current.complete_done,
      };
      await writeRecord(this.uploadsDirectory, next);
      return next;
    }, options.maxWaitMs);
  }

  /**
   * Creates and hardens `uploads/`; idempotent, and refuses a symlinked or foreign dir.
   */
  private async ensureDirectory(): Promise<void> {
    await mkdirOwned(this.uploadsDirectory);
    await hardenDirectory(this.uploadsDirectory);
  }

  /**
   * One directory read, filtered to the identity's own prefix: the digest-derived name means
   * a lookup never has to stat or parse another upload's records.
   */
  private async candidates(identity: UploadIdentity): Promise<readonly UploadJournalEntry[]> {
    const prefix = `${uploadIdentityDigest(identity).slice(0, 16)}-`;
    const entries: UploadJournalEntry[] = [];
    for (const name of await this.recordNames(prefix)) {
      const entry = parseEntry(name, await readVerifiedText(join(this.uploadsDirectory, name)));
      if (entry !== undefined) entries.push(entry);
    }
    return entries;
  }

  /** Every current record name (optionally only the ones under `prefix`), sorted. */
  private async recordNames(prefix?: string): Promise<string[]> {
    let names: string[];
    try {
      names = await readdir(this.uploadsDirectory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return [];
      throw error;
    }
    return names
      .filter(
        (name) => isJournalFileName(name) && (prefix === undefined || name.startsWith(prefix)),
      )
      .sort();
  }

  /**
   * Runs one locked write, waiting out a busy `spool.lock` instead of failing at once: a
   * live `events send` holds the same producer lock, and a journal write is a few hundred
   * bytes, so it is worth a bounded wait rather than a `spool_locked` failure. The wait
   * stops at the upload deadline, and at the smaller of the journal's budget and a per-call
   * `maxWaitMs` when one is given; the last `spool_locked` error is re-raised then, unchanged.
   */
  private async withBoundedLock<T>(fn: () => Promise<T>, maxWaitMs?: number): Promise<T> {
    const startedAt = this.now();
    const budget = Math.min(this.maxWaitMs, maxWaitMs ?? Number.POSITIVE_INFINITY);
    const until = Math.min(startedAt + budget, this.lockDeadlineAt);
    let wait = lockRetryBaseMs;
    for (;;) {
      try {
        return await this.withLock(fn);
      } catch (error) {
        if (!isSpoolLocked(error)) throw error;
        if (this.now() + wait > until) throw error;
        await this.sleep(wait);
        wait = Math.min(wait * 2, lockRetryMaxMs);
      }
    }
  }
}
