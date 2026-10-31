// One module-level `exit` hook releases every writer lock this process still holds. Writers
// release theirs in `close()`; an unclosed one (a Playwright process that exits without `onEnd`,
// or a test) is handled here — one hook instead of one listener per writer, which also keeps
// `MaxListenersExceededWarning` away when a process opens many producers in a row.
import { releaseWriterLock, type HeldWriterLock } from './writerLock.js';

const liveLocks = new Set<HeldWriterLock>();
let exitHook: (() => void) | undefined;

export function trackLock(held: HeldWriterLock): void {
  liveLocks.add(held);
  if (exitHook !== undefined) return;
  exitHook = () => {
    for (const lock of liveLocks) {
      try {
        releaseWriterLock(lock);
      } catch {
        // Nothing can be reported from an exit handler; the lock goes stale with the pid.
      }
    }
    liveLocks.clear();
  };
  process.once('exit', exitHook);
}

export function untrackLock(held: HeldWriterLock): void {
  liveLocks.delete(held);
  if (liveLocks.size > 0 || exitHook === undefined) return;
  process.off('exit', exitHook);
  exitHook = undefined;
}
