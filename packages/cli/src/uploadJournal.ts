// 04.2.4-T1.3: the crash-safe upload journal (04.2.4 §4/§6, contracts/cli.md §12).
//
// Layout (one level below the producer directory the 04.2.3 spool already uses, so a
// finalize purge that removes the producer directory removes the journal too):
//
//   <spool_dir>/<run_id>/<producer_id>/uploads/<artifact_id>.json
//
// Content is exactly the plan's record — `{ artifact_id, file_sha256, size, put_done,
// complete_done }` — plus `mint_no`, the 0-based upload-url mint this row belongs to (see
// `upload.ts` and contracts/cli.md §12 for why an expired presigned URL needs a new mint).
// The record never carries the presigned URL, a token or any header value: those are
// bearer credentials (04 §6/S3) and a journal survives on disk after the process dies.
//
// Every write is tmp + fsync + rename + directory fsync under the producer's exclusive
// `spool.lock` (`spoolLock.ts`), the same lock `events send` and `runs finalize` take, so
// two CLI processes can never interleave a journal write. The directory is created 0700
// and hardened like the rest of the spool; a symlinked or foreign-owned `uploads/` entry,
// a planted symlink for a journal and a record that does not parse are all refused
// (`spool_unsafe_path`, `spool_corrupt`) instead of trusted.
//
// A missing `EVIDGATE_SPOOL_DIR` means no journal: the upload still works, it simply has
// no resume state across process restarts (`journalFor` returns `undefined`).
import { randomUUID } from 'node:crypto';
import { constants as fsConstants } from 'node:fs';
import { readdir, rename, unlink } from 'node:fs/promises';
import { join } from 'node:path';

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

import type { FileHandle } from 'node:fs/promises';

/** The journal directory inside a producer directory (04.2.4 §4). */
export const uploadsDirectoryName = 'uploads';

/** One journal record: what an interrupted upload needs to resume where it stopped. */
export interface UploadJournalEntry {
  /** The server's artifact id (a UUID); also the record's file name. */
  readonly artifact_id: string;
  /** The file digest the row was minted for, in the server's `sha256:<hex>` form. */
  readonly file_sha256: string;
  /** The declared byte count. */
  readonly size: number;
  /** The object was PUT successfully; a restart only calls `complete`. */
  readonly put_done: boolean;
  /** `complete` answered for this row (`ready` or a committed `rejected`). */
  readonly complete_done: boolean;
  /** Which upload-url mint this row came from (0 for the first); see `upload.ts`. */
  readonly mint_no: number;
}

export interface UploadJournalOptions {
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

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const checksumPattern = /^sha256:[0-9a-f]{64}$/;
const journalFilePattern =
  /^([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.json$/i;

/** Builds the per-producer journal, or `undefined` when there is no spool root. */
export async function journalFor(input: {
  readonly spoolDir: string | undefined;
  readonly producerDirectory: string;
  readonly withLock: <T>(fn: () => Promise<T>) => Promise<T>;
}): Promise<UploadJournal | undefined> {
  if (input.spoolDir === undefined) {
    return undefined;
  }
  return UploadJournal.open({
    producerDirectory: input.producerDirectory,
    withLock: input.withLock,
  });
}

export class UploadJournal {
  /** `<producer_dir>/uploads` — created lazily, 0700, under the producer lock. */
  readonly uploadsDirectory: string;
  private readonly withLock: <T>(fn: () => Promise<T>) => Promise<T>;

  private constructor(o: UploadJournalOptions) {
    this.uploadsDirectory = join(o.producerDirectory, uploadsDirectoryName);
    this.withLock = o.withLock;
  }

  static async open(o: UploadJournalOptions): Promise<UploadJournal> {
    const journal = new UploadJournal(o);
    await journal.ensureDirectory();
    return journal;
  }

  /**
   * The row a restarted upload continues: the highest `mint_no` for this digest and size.
   * `complete_done` means the artifact is already committed ready, so the call returns
   * without a request; a row without it is resumed (only `complete` when `put_done`), so a
   * re-run after a crash or after a rejection converges on the same artifact row instead of
   * minting a second one.
   */
  async findResumable(fileSha256: string, size: number): Promise<UploadJournalEntry | undefined> {
    const entries = await this.entries();
    return entries
      .filter((entry) => entry.file_sha256 === fileSha256 && entry.size === size)
      .sort((a, b) => b.mint_no - a.mint_no)[0];
  }

  /**
   * The next mint number for this (digest, size): one past the highest row ever written,
   * so an expired presigned URL gets a fresh artifact row instead of replaying the stored
   * response of a dead one, and a restart derives the same number it would have used.
   */
  async nextMintNo(fileSha256: string, size: number): Promise<number> {
    const entries = await this.entries();
    let highest = -1;
    for (const entry of entries) {
      if (entry.file_sha256 === fileSha256 && entry.size === size) {
        highest = Math.max(highest, entry.mint_no);
      }
    }
    return highest + 1;
  }

  /** Reads every record; a record that does not parse is `spool_corrupt`, never skipped. */
  async entries(): Promise<readonly UploadJournalEntry[]> {
    let names: string[];
    try {
      names = await readdir(this.uploadsDirectory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return [];
      throw error;
    }
    const entries: UploadJournalEntry[] = [];
    for (const name of names.sort()) {
      if (!journalFilePattern.test(name)) continue;
      const text = await readVerifiedText(join(this.uploadsDirectory, name));
      entries.push(parseEntry(name, text));
    }
    return entries;
  }

  /** Writes one record atomically, under the producer lock (tmp + fsync + rename + dir fsync). */
  async write(entry: UploadJournalEntry): Promise<void> {
    assertEntry(entry);
    await this.withLock(async () => {
      await this.ensureDirectory();
      await writeRecord(this.uploadsDirectory, entry);
    });
  }

  /** Applies a state change to an existing record; a missing record is `spool_corrupt`. */
  async update(artifactId: string, patch: UploadJournalPatch): Promise<UploadJournalEntry> {
    assertArtifactId(artifactId);
    return this.withLock(async () => {
      const name = `${artifactId}.json`;
      let text: string;
      try {
        text = await readVerifiedText(join(this.uploadsDirectory, name));
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
          throw corrupt(name, artifactId);
        }
        throw error;
      }
      const current = parseEntry(name, text);
      const next: UploadJournalEntry = {
        ...current,
        put_done: patch.put_done ?? current.put_done,
        complete_done: patch.complete_done ?? current.complete_done,
      };
      await writeRecord(this.uploadsDirectory, next);
      return next;
    });
  }

  /** Creates and hardens `uploads/`; idempotent, and refuses a symlinked or foreign dir. */
  private async ensureDirectory(): Promise<void> {
    await mkdirOwned(this.uploadsDirectory);
    await hardenDirectory(this.uploadsDirectory);
  }
}

/** The record's file name for an artifact id. */
export function journalFileName(artifactId: string): string {
  assertArtifactId(artifactId);
  return `${artifactId}.json`;
}

function assertArtifactId(artifactId: string): void {
  if (!uuidPattern.test(artifactId)) {
    throw new CliError(`the artifact id ${artifactId.slice(0, 64)} is not a UUID`, {
      code: 'spool_corrupt',
      hint: 'delete the upload journal directory and retry',
    });
  }
}

function assertEntry(entry: UploadJournalEntry): void {
  assertArtifactId(entry.artifact_id);
  if (!checksumPattern.test(entry.file_sha256)) {
    throw corrupt('file_sha256', entry.artifact_id);
  }
  if (!Number.isSafeInteger(entry.size) || entry.size <= 0) {
    throw corrupt('size', entry.artifact_id);
  }
  if (!Number.isSafeInteger(entry.mint_no) || entry.mint_no < 0) {
    throw corrupt('mint_no', entry.artifact_id);
  }
}

/** Parses one record; every field is required and typed, nothing is defaulted. */
function parseEntry(name: string, text: string): UploadJournalEntry {
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
  const sha = record['file_sha256'];
  const size = record['size'];
  const putDone = record['put_done'];
  const completeDone = record['complete_done'];
  const mintNo = record['mint_no'];
  if (
    typeof artifactId !== 'string' ||
    !journalFilePattern.test(name) ||
    name.slice(0, -'.json'.length) !== artifactId ||
    typeof sha !== 'string' ||
    !checksumPattern.test(sha) ||
    !Number.isSafeInteger(size) ||
    (size as number) <= 0 ||
    typeof putDone !== 'boolean' ||
    typeof completeDone !== 'boolean' ||
    !Number.isSafeInteger(mintNo) ||
    (mintNo as number) < 0
  ) {
    throw corrupt(name, undefined);
  }
  return {
    artifact_id: artifactId,
    file_sha256: sha,
    size: size as number,
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
  const target = join(directory, journalFileName(entry.artifact_id));
  const tmp = join(directory, `${entry.artifact_id}.json.${randomUUID()}.tmp`);
  // Exactly the five plan fields plus `mint_no`; no URL, no token, no header.
  const text = `${JSON.stringify({
    artifact_id: entry.artifact_id,
    file_sha256: entry.file_sha256,
    size: entry.size,
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
