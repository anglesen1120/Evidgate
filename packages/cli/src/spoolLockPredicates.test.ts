// The lock module's predicates: which pids count as alive, which errnos mean "there is no
// lock", and which filesystem refusals mean a mutex cannot be hard-linked.
import { describe, expect, it } from 'vitest';

import { isHardLinkUnsupportedErrno, isMissingLockErrno, isProcessAlive } from './spoolLock.js';

describe('withSpoolLock', () => {
  it('detects liveness only for real pids', () => {
    expect(isProcessAlive(process.pid)).toBe(true);
    expect(isProcessAlive(0)).toBe(false);
    expect(isProcessAlive(-1)).toBe(false);
    expect(isProcessAlive(1.5)).toBe(false);
  });

  it('treats only "nothing is there" errnos as an absent lock', () => {
    for (const code of ['ENOENT', 'ELOOP', 'ENXIO']) {
      expect(isMissingLockErrno(code)).toBe(true);
    }
    const failures: (string | undefined)[] = [
      'EMFILE',
      'ENFILE',
      'EIO',
      'EACCES',
      'EPERM',
      'ENOTDIR',
      'EISDIR',
      undefined,
    ];
    for (const code of failures) {
      expect(isMissingLockErrno(code)).toBe(false);
    }
  });

  it('refuses the lock on a filesystem without hard links', () => {
    for (const code of ['EPERM', 'ENOTSUP', 'EOPNOTSUPP', 'EXDEV', 'ENOSYS']) {
      expect(isHardLinkUnsupportedErrno(code)).toBe(true);
    }
    for (const code of ['EEXIST', 'ENOENT', 'EMLINK', 'EIO', undefined]) {
      expect(isHardLinkUnsupportedErrno(code)).toBe(false);
    }
  });
});
