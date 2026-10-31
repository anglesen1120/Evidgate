// The CLI-side single-writer lock on a producer directory: `events send` and
// `runs finalize` must never interleave. The CLI only inspects the reporter's
// `writer.lock` and never creates, rewrites or deletes it, so a live successor's lock
// cannot be destroyed.
//
// The implementation is split by concern, and this module is the surface every caller
// imports: shapes and on-disk constants (`spoolLockTypes`), the read/classify path
// (`spoolLockRead`), the reporter's lock check (`spoolLockWriter`) and the
// acquire/release path with the stale-lock takeover (`spoolLockAcquire`).
export {
  spoolLockFileName,
  spoolLockFileMode,
  SpoolLockedError,
  type SpoolLockFile,
  type SpoolLockOptions,
} from './spoolLockTypes.js';
export { isMissingLockErrno, isProcessAlive, readSpoolLock } from './spoolLockRead.js';
export {
  assertNoLiveWriter,
  readWriterLock,
  WriterLockedError,
  writerLockFileName,
  type ProducerLockOptions,
  type WriterLockCheckOptions,
} from './spoolLockWriter.js';
export { isHardLinkUnsupportedErrno, withSpoolLock } from './spoolLockAcquire.js';
