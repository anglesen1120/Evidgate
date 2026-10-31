// The release half of the lock protocol: dropping the record we still own, and every name a
// takeover may have given its inode. It lives apart from acquisition so that both halves
// share one unlink helper without the acquisition module outgrowing its size budget.
import { lstat, readdir, unlink } from 'node:fs/promises';
import { dirname, join } from 'node:path';

import { readSpoolLockPath } from './spoolLockRead.js';
import {
  spoolLockFileName,
  type HeldSpoolLock,
  type SpoolLockFile,
  type SpoolLockHooks,
} from './spoolLockTypes.js';

/** Unlinks `path`, treating an entry that is already gone as done. */
export async function removeIfPresent(path: string): Promise<void> {
  await unlink(path).catch((error: unknown) => {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
  });
}

/**
 * Removes the lock only while it still carries our record: a lock taken over by another
 * process (or rewritten by it) is left untouched, so a slow holder never deletes its
 * successor's lock.
 */
export async function releaseSpoolLock(held: HeldSpoolLock, hooks?: SpoolLockHooks): Promise<void> {
  let holder: SpoolLockFile | null;
  try {
    holder = await readSpoolLockPath(held.path);
  } catch {
    // The lock cannot be inspected, so it cannot be removed safely either: leave it in
    // place (our pid dies with this process, so a later run sees a stale lock) and never
    // replace `fn`'s result or error with a release failure.
    return;
  }
  if (holder !== null && (holder.pid !== held.pid || holder.startedAt !== held.startedAt)) return;
  if (holder !== null) {
    await removeIfPresent(held.path);
    await hooks?.afterReleaseUnlink?.();
  }
  await scavengeQuarantinedLock(held);
}

/**
 * Deletes every name that still carries our lock inode. A takeover renames the lock aside
 * before it decides, so a restore after our unlink fails once the quarantine name is gone;
 * a restore that already happened is removed from the path only because a quarantine link
 * still pinned our inode, which is the only proof the number was not handed to a successor.
 */
async function scavengeQuarantinedLock(held: HeldSpoolLock): Promise<void> {
  const directory = dirname(held.path);
  let names: string[];
  try {
    names = await readdir(directory);
  } catch {
    return;
  }
  const prefix = `${spoolLockFileName}.`;
  let found = false;
  for (const name of names) {
    if (!name.startsWith(prefix) || !name.endsWith('.stale')) continue;
    const candidate = join(directory, name);
    if (!(await carriesOurInode(candidate, held))) continue;
    found = true;
    await removeIfPresent(candidate);
  }
  // A quarantine link pins our inode; without one its number may belong to a successor.
  if (found && (await carriesOurInode(held.path, held))) await removeIfPresent(held.path);
}

async function carriesOurInode(path: string, held: HeldSpoolLock): Promise<boolean> {
  const stats = await lstat(path).catch(() => null);
  return stats !== null && stats.dev === held.dev && stats.ino === held.ino;
}
