// 04.2.4-T1.3: the crash-safe upload journal (04.2.4 §4/§6, contracts/cli.md §12).
//
// Layout (one level below the producer directory the 04.2.3 spool already uses, so a
// finalize purge that removes the producer directory removes the journal too):
//
//   <spool_dir>/<run_id>/<producer_id>/uploads/<identity>-<artifact_id>.json
//
// `<identity>` is the first 16 hex of the upload identity digest (see
// {@link uploadIdentityDigest}). The identity is every value the `upload-url` request is
// built from — `kind`, `execution_key`, `attempt_no`, `media_type`, `file_sha256`, `size`
// — so the same bytes under a different execution, kind or attempt are a DIFFERENT upload:
// they neither resume nor reuse the other row's mint. Keying the file name by that digest
// also means a lookup reads the directory once and parses only its own candidates, instead
// of parsing every record twice per upload.
//
// Content is the identity plus `{ artifact_id, put_done, complete_done, mint_no }`, where
// `mint_no` is the 0-based upload-url mint this row belongs to (see `upload.ts` and
// contracts/cli.md §12 for why an expired presigned URL needs a new mint). The record never
// carries the presigned URL, a token or any header value: those are bearer credentials
// (04 §6/S3) and a journal survives on disk after the process dies.
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
import { createHash, randomUUID } from 'node:crypto';
import { constants as fsConstants } from 'node:fs';
import { readdir, rename, unlink } from 'node:fs/promises';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

import { CliError } from './errors.js';
import {
  fsyncDirectorySync,
  hardenDirectory,
  mkdirOwned,
  openVerifiedFile,
  readVerifiedText,
  spoolFileMode,
  writeAll,
} from './spool.js';
import { artifactKinds, type ArtifactKind } from './uploadPaths.js';

import type { FileHandle } from 'node:fs/promises';

/** The journal directory inside a producer directory (04.2.4 §4). */
export const uploadsDirectoryName = 'uploads';

/**
 * The upload an artifact belongs to: every value the `upload-url` request is built from.
 * Two uploads with the same bytes but a different execution, kind, attempt or media type
 * are different rows, so all of these must match before a record is resumed.
 */
export interface UploadIdentity {
  /** The file digest, in the server's `sha256:<hex>` form. */
  readonly file_sha256: string;
  /** The declared byte count. */
  readonly size: number;
  /** The artifact kind (openapi `createArtifactUploadUrl.kind`). */
  readonly kind: ArtifactKind;
  /** The planned execution (64 lowercase hex). */
  readonly execution_key: string;
  /** The execution attempt (integer ≥ 1). */
  readonly attempt_no: number;
  /** The media type sent with the mint. */
  readonly media_type: string;
}

/** One journal record: what an interrupted upload needs to resume where it stopped. */
export interface UploadJournalEntry extends UploadIdentity {
  /** The server's artifact id (a UUID); part of the record's file name. */
  readonly artifact_id: string;
  /** The object was PUT successfully; a restart only calls `complete`. */
  readonly put_done: boolean;
  /** `complete` answered for this row (`ready` or a committed `rejected`). */
  readonly complete_done: boolean;
  /** Which upload-url mint this row came from (0 for the first); see `upload.ts`. */
  readonly mint_no: number;
}

/**
 * How a journal write waits for a busy `spool.lock`. `sleep` and `now` are injected by the
 * specs (a virtual clock); production uses the real ones.
 */
export interface UploadJournalLockOptions {
  /** Absolute upload deadline (`now() + EVIDGATE_UPLOAD_DEADLINE`). */
  readonly deadlineAt?: number;
  /** Clock in milliseconds; it bounds the wait. */
  readonly now?: () => number;
  /** Virtual sleep; the wait between two lock attempts. */
  readonly sleep?: (milliseconds: number) => Promise<void>;
  /**
   * The journal's own cap on one wait, overridable per call (see
   * {@link UploadJournalWriteOptions}). By default the wait is bounded by the upload deadline
   * alone (`deadlineAt`), because `events send` holds the same lock for its whole run;
   * without a deadline a short fallback keeps a stuck writer from blocking forever.
   */
  readonly maxWaitMs?: number;
}

/**
 * A per-call override of the lock budget. The post-transfer best-effort updates
 * (`put_done`, `complete_done`) use it: the bytes are already in the store, so costing the
 * rest of the upload deadline for a lock `events send` may hold for its whole run is worse
 * than recounting the transfer on the next run.
 */
export interface UploadJournalWriteOptions {
  /** A cap on THIS call's wait for `spool.lock`, on top of the journal's own budget. */
  readonly maxWaitMs?: number;
}

export interface UploadJournalOptions extends UploadJournalLockOptions {
  /** The producer directory (`<spool_dir>/<run_id>/<producer_id>`), already validated. */
  readonly producerDirectory: string;
  /** The producer's exclusive lock (`ProducerSpool.withLock`), reused, never re-implemented. */
  readonly withLock: <T>(fn: () => Promise<T>) => Promise<T>;
}

/** The fields a journal update may change; the identity fields never change. */
export interface UploadJournalPatch {
  readonly put_done?: boolean;
  readonly complete_done?: boolean;
}

/** The artifact id's shape (openapi `RunId`/artifact id, `format: uuid`). */
export const artifactIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const checksumPattern = /^sha256:[0-9a-f]{64}$/;
const executionKeyPattern = /^[0-9a-f]{64}$/;
/** openapi `createArtifactUploadUrl.media_type`: 1..255 code points, no Cc/Cf character. */
const mediaTypePattern = /^[^\p{Cc}\p{Cf}]{1,255}$/u;
/** `<identity digest prefix>-<artifact id>.json`; a legacy `<artifact id>.json` is skipped. */
const journalFilePattern =
  /^([0-9a-f]{16})-([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.json$/i;
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
        (name) =>
          journalFilePattern.test(name) && (prefix === undefined || name.startsWith(prefix)),
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

/** True when the failure is the producer lock being held by a live process. */
export function isSpoolLocked(error: unknown): boolean {
  return error instanceof Error && (error as CliError).code === 'spool_locked';
}

/** Field-by-field equality: every identity member must match, not just the digest. */
function sameIdentity(entry: UploadIdentity, identity: UploadIdentity): boolean {
  return (
    entry.file_sha256 === identity.file_sha256 &&
    entry.size === identity.size &&
    entry.kind === identity.kind &&
    entry.execution_key === identity.execution_key &&
    entry.attempt_no === identity.attempt_no &&
    entry.media_type === identity.media_type
  );
}

/**
 * The identity digest: the length-prefixed UTF-8 concatenation of a version tag and every
 * identity field, in a fixed order, hashed with SHA-256. Length prefixes keep the encoding
 * unambiguous (`ab|c` and `a|bc` must not collide), exactly like the AC1 `upload-url` key.
 * 64 lowercase hex; the first 16 are the record's name prefix.
 */
export function uploadIdentityDigest(identity: UploadIdentity): string {
  const fields = [
    'evidgate.artifact-upload-identity.v1',
    identity.kind,
    identity.execution_key,
    String(identity.attempt_no),
    identity.media_type,
    identity.file_sha256,
    String(identity.size),
  ];
  const preImage = fields.map((field) => `${Buffer.byteLength(field, 'utf8')}:${field}`).join('');
  return createHash('sha256').update(preImage, 'utf8').digest('hex');
}

/** The record's file name: the identity prefix, then the artifact id. */
export function journalFileName(identity: UploadIdentity, artifactId: string): string {
  assertArtifactId(artifactId);
  assertIdentity(identity);
  return `${uploadIdentityDigest(identity).slice(0, 16)}-${artifactId}.json`;
}

function assertArtifactId(artifactId: string): void {
  if (!artifactIdPattern.test(artifactId)) {
    throw new CliError(`the artifact id ${artifactId.slice(0, 64)} is not a UUID`, {
      code: 'spool_corrupt',
      hint: 'delete the upload journal directory and retry',
    });
  }
}

/** Every identity member, typed; a bad value is `spool_corrupt` naming the field. */
function assertIdentity(identity: UploadIdentity): void {
  if (!checksumPattern.test(identity.file_sha256)) {
    throw corrupt('file_sha256', undefined);
  }
  if (!Number.isSafeInteger(identity.size) || identity.size <= 0) {
    throw corrupt('size', undefined);
  }
  if (!artifactKinds.includes(identity.kind)) {
    throw corrupt('kind', undefined);
  }
  if (!executionKeyPattern.test(identity.execution_key)) {
    throw corrupt('execution_key', undefined);
  }
  if (!Number.isSafeInteger(identity.attempt_no) || identity.attempt_no < 1) {
    throw corrupt('attempt_no', undefined);
  }
  if (!mediaTypePattern.test(identity.media_type)) {
    throw corrupt('media_type', undefined);
  }
}

function assertEntry(entry: UploadJournalEntry): void {
  assertArtifactId(entry.artifact_id);
  assertIdentity(entry);
  if (!Number.isSafeInteger(entry.mint_no) || entry.mint_no < 0) {
    throw corrupt('mint_no', entry.artifact_id);
  }
}

/**
 * Reads the identity members out of a parsed record, or `undefined` when the record is not a
 * current one (it has no identity fields, or a field is not of the documented type). A
 * record from an older CLI therefore never matches a lookup and never crashes one; it is
 * simply invisible until `runs finalize` purges the producer directory.
 */
function readIdentity(record: Record<string, unknown>): UploadIdentity | undefined {
  const sha = record['file_sha256'];
  const size = record['size'];
  const kind = record['kind'];
  const executionKey = record['execution_key'];
  const attemptNo = record['attempt_no'];
  const mediaType = record['media_type'];
  if (
    typeof sha !== 'string' ||
    !checksumPattern.test(sha) ||
    !Number.isSafeInteger(size) ||
    (size as number) <= 0 ||
    typeof kind !== 'string' ||
    !artifactKinds.includes(kind as ArtifactKind) ||
    typeof executionKey !== 'string' ||
    !executionKeyPattern.test(executionKey) ||
    !Number.isSafeInteger(attemptNo) ||
    (attemptNo as number) < 1 ||
    typeof mediaType !== 'string' ||
    !mediaTypePattern.test(mediaType)
  ) {
    return undefined;
  }
  return {
    file_sha256: sha,
    size: size as number,
    kind: kind as ArtifactKind,
    execution_key: executionKey,
    attempt_no: attemptNo as number,
    media_type: mediaType,
  };
}

/**
 * Parses one current record. Invalid JSON, a name that is not of the current shape, a name
 * whose artifact id or identity prefix disagrees with the content, and a bad state field are
 * `spool_corrupt`; a record without the identity fields is an older row and returns
 * `undefined` (ignored, never trusted).
 */
function parseEntry(name: string, text: string): UploadJournalEntry | undefined {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw corrupt(name, undefined);
  }
  if (typeof parsed !== 'object' || parsed === null) {
    throw corrupt(name, undefined);
  }
  const record = parsed as Record<string, unknown>;
  const artifactId = record['artifact_id'];
  const identity = readIdentity(record);
  if (identity === undefined) {
    return undefined;
  }
  const match = journalFilePattern.exec(name);
  const putDone = record['put_done'];
  const completeDone = record['complete_done'];
  const mintNo = record['mint_no'];
  if (
    typeof artifactId !== 'string' ||
    match === null ||
    match[2]?.toLowerCase() !== artifactId.toLowerCase() ||
    match[1]?.toLowerCase() !== uploadIdentityDigest(identity).slice(0, 16) ||
    typeof putDone !== 'boolean' ||
    typeof completeDone !== 'boolean' ||
    !Number.isSafeInteger(mintNo) ||
    (mintNo as number) < 0
  ) {
    throw corrupt(name, undefined);
  }
  return {
    artifact_id: artifactId,
    ...identity,
    put_done: putDone,
    complete_done: completeDone,
    mint_no: mintNo as number,
  };
}

/** `spool_corrupt` for one journal record; the message names the file, never its content. */
function corrupt(name: string, artifactId: string | undefined): CliError {
  return new CliError(
    `the upload journal ${name}${artifactId === undefined ? '' : ` for ${artifactId}`} is not a valid record`,
    {
      code: 'spool_corrupt',
      hint: 'delete the upload journal directory and retry (a completed upload is never duplicated)',
    },
  );
}

/**
 * Replaces one record atomically: a unique 0600 temp file in the same directory, fsynced,
 * renamed over the target, then the directory fsynced. A crash at any point leaves either
 * the previous record or the new one — never a torn file.
 */
async function writeRecord(directory: string, entry: UploadJournalEntry): Promise<void> {
  const target = join(directory, journalFileName(entry, entry.artifact_id));
  const tmp = join(directory, `${entry.artifact_id}.json.${randomUUID()}.tmp`);
  // The identity plus the four state fields; no URL, no token, no header.
  const text = `${JSON.stringify({
    artifact_id: entry.artifact_id,
    file_sha256: entry.file_sha256,
    size: entry.size,
    kind: entry.kind,
    execution_key: entry.execution_key,
    attempt_no: entry.attempt_no,
    media_type: entry.media_type,
    put_done: entry.put_done,
    complete_done: entry.complete_done,
    mint_no: entry.mint_no,
  })}\n`;
  let handle: FileHandle | undefined;
  try {
    handle = await openVerifiedFile(
      tmp,
      fsConstants.O_WRONLY | fsConstants.O_CREAT | fsConstants.O_EXCL,
      spoolFileMode,
    );
    await writeAll(handle, Buffer.from(text, 'utf8'));
    await handle.sync();
    await handle.close();
    handle = undefined;
    await rename(tmp, target);
    fsyncDirectorySync(directory);
  } catch (error) {
    if (handle !== undefined) await handle.close().catch(() => undefined);
    await unlink(tmp).catch(() => undefined);
    throw error;
  }
}
