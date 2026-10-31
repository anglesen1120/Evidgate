// The journal record codec: the file name (a digest of the whole upload identity, so a
// different execution or attempt cannot resume another row's mint), the field validation
// that makes a bad row `spool_corrupt` instead of trusted, and the atomic replacement
// (tmp + fsync + rename + directory fsync) that leaves either the old or the new record.
import { createHash, randomUUID } from 'node:crypto';
import { constants as fsConstants } from 'node:fs';
import { rename, unlink } from 'node:fs/promises';
import { join } from 'node:path';

import { CliError } from './errors.js';
import { fsyncDirectorySync, openVerifiedFile, spoolFileMode, writeAll } from './spool.js';
import { artifactKinds, type ArtifactKind } from './uploadPaths.js';

import type { FileHandle } from 'node:fs/promises';
import type { UploadIdentity, UploadJournalEntry } from './uploadJournalTypes.js';

/** The artifact id's shape (openapi `RunId`/artifact id, `format: uuid`). */
export const artifactIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const checksumPattern = /^sha256:[0-9a-f]{64}$/;
const executionKeyPattern = /^[0-9a-f]{64}$/;
/** openapi `createArtifactUploadUrl.media_type`: 1..255 code points, no Cc/Cf character. */
const mediaTypePattern = /^[^\p{Cc}\p{Cf}]{1,255}$/u;
/** `<identity digest prefix>-<artifact id>.json`; a legacy `<artifact id>.json` is skipped. */
const journalFilePattern =
  /^([0-9a-f]{16})-([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.json$/i;

/** Whether a directory entry name is a current-format journal record. */
export function isJournalFileName(name: string): boolean {
  return journalFilePattern.test(name);
}

/** True when the failure is the producer lock being held by a live process. */
export function isSpoolLocked(error: unknown): boolean {
  return error instanceof Error && (error as CliError).code === 'spool_locked';
}

/** Field-by-field equality: every identity member must match, not just the digest. */
export function sameIdentity(entry: UploadIdentity, identity: UploadIdentity): boolean {
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
 * identity field, fixed order, hashed with SHA-256. Length prefixes keep the encoding
 * unambiguous (`ab|c` and `a|bc` must not collide), like the `upload-url` key.
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

export function assertArtifactId(artifactId: string): void {
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

export function assertEntry(entry: UploadJournalEntry): void {
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
export function parseEntry(name: string, text: string): UploadJournalEntry | undefined {
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
export function corrupt(name: string, artifactId: string | undefined): CliError {
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
export async function writeRecord(directory: string, entry: UploadJournalEntry): Promise<void> {
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
