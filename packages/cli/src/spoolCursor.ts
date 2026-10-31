// The CLI-owned ack cursor: `cursor.json` is replaced atomically (tmp + fsync + rename +
// directory fsync), so a crash leaves either the old cursor or the new one. A cursor that
// does not parse or does not belong to this producer is `spool_corrupt`, never "nothing
// acked yet".
import { constants as fsConstants } from 'node:fs';
import { rename, unlink } from 'node:fs/promises';
import { join } from 'node:path';

import { SpoolCorruptError } from './spoolErrors.js';
import { fsyncDirectorySync, openVerifiedFile, readVerifiedText, writeAll } from './spoolFs.js';
import {
  O_NOFOLLOW,
  spoolCursorFileName,
  spoolCursorTmpFileName,
  spoolFileMode,
} from './spoolLimits.js';

import type { SpoolCursor } from './spoolTypes.js';

/** The ack cursor; a missing `cursor.json` means "nothing acked yet". */
export async function readCursor(directory: string, producerId: string): Promise<SpoolCursor> {
  let text: string;
  try {
    text = await readVerifiedText(join(directory, spoolCursorFileName));
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
      return {
        producerId,
        nextUnackedSequence: 1,
        lastAckedSequence: 0,
        ackedCount: 0,
      };
    }
    throw error;
  }
  return parseCursor(text, producerId);
}

function parseCursor(text: string, producerId: string): SpoolCursor {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw corruptCursor('not valid JSON');
  }
  if (typeof parsed !== 'object' || parsed === null) {
    throw corruptCursor('not a JSON object');
  }
  return checkedCursor(parsed, producerId);
}

/** The cursor record's fields, refused unless they are the shape `ack` may advance. */
function checkedCursor(record: object, producerId: string): SpoolCursor {
  const fileProducer: unknown = 'producer_id' in record ? record.producer_id : undefined;
  const nextUnacked: unknown =
    'next_unacked_sequence' in record ? record.next_unacked_sequence : undefined;
  const lastAcked: unknown =
    'last_acked_sequence' in record ? record.last_acked_sequence : undefined;
  const ackedCount: unknown = 'acked_count' in record ? record.acked_count : undefined;
  if (fileProducer !== producerId) {
    throw corruptCursor(`belongs to producer ${JSON.stringify(fileProducer)}`);
  }
  if (typeof nextUnacked !== 'number' || !Number.isSafeInteger(nextUnacked) || nextUnacked < 1) {
    throw corruptCursor('next_unacked_sequence must be an integer >= 1');
  }
  if (typeof lastAcked !== 'number' || !Number.isSafeInteger(lastAcked) || lastAcked < 0) {
    throw corruptCursor('last_acked_sequence must be an integer >= 0');
  }
  if (typeof ackedCount !== 'number' || !Number.isSafeInteger(ackedCount) || ackedCount < 0) {
    throw corruptCursor('acked_count must be an integer >= 0');
  }
  if (lastAcked !== nextUnacked - 1) {
    throw corruptCursor('last_acked_sequence must be next_unacked_sequence - 1');
  }
  return {
    producerId,
    nextUnackedSequence: nextUnacked,
    lastAckedSequence: lastAcked,
    ackedCount,
  };
}

function corruptCursor(reason: string): SpoolCorruptError {
  return new SpoolCorruptError({ chunk: spoolCursorFileName, lineNumber: 1, reason });
}

/** Atomic cursor replace: tmp (0600) + fsync + rename + directory fsync. */
export async function writeCursor(directory: string, cursor: SpoolCursor): Promise<void> {
  const state = {
    producer_id: cursor.producerId,
    next_unacked_sequence: cursor.nextUnackedSequence,
    last_acked_sequence: cursor.lastAckedSequence,
    acked_count: cursor.ackedCount,
    updated_at: new Date().toISOString(),
  };
  const tmpPath = join(directory, spoolCursorTmpFileName);
  const targetPath = join(directory, spoolCursorFileName);
  // A leftover tmp is a crash between write and rename; a planted symlink at that path
  // is unlinked, never truncated through.
  await unlink(tmpPath).catch((error: unknown) => {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
  });
  const handle = await openVerifiedFile(
    tmpPath,
    fsConstants.O_WRONLY | fsConstants.O_CREAT | fsConstants.O_EXCL | O_NOFOLLOW,
    spoolFileMode,
  );
  try {
    await writeAll(handle, Buffer.from(`${JSON.stringify(state)}\n`, 'utf8'));
    await handle.sync();
  } finally {
    await handle.close();
  }
  await rename(tmpPath, targetPath);
  fsyncDirectorySync(directory);
}
