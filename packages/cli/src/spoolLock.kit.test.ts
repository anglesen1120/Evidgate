// Fixtures shared by the spool.lock specs: lock records, error narrowing, constants a test
// process can never collide with, and a real `mkfifo` probe. Named `.kit.test.ts` so the
// eslint project service covers it; the specs import these instead of copying them.

import { execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { isHardLinkUnsupportedErrno, isMissingLockErrno, isProcessAlive } from './spoolLock.js';

/**
 * `mkfifo` through a plain promise: no shell, no options to get wrong.
 * `Promise.withResolvers` is ES2024 and the workspace targets ES2022.
 */
export function makeFifo(path: string): Promise<void> {
  return new Promise((resolve, reject) => {
    execFile('mkfifo', [path], (error) => {
      if (error === null) resolve();
      else reject(error instanceof Error ? error : new Error(`mkfifo ${path} failed`));
    });
  });
}

export const fixedNow = new Date('2026-10-23T12:00:00.000Z');
export const fixedIso = fixedNow.toISOString();
/** A pid no test process can have: the injected liveness probe decides on it. */
export const deadPid = 987_654;

export function failureOf<T>(promise: Promise<T>): Promise<unknown> {
  return promise.then(
    () => null,
    (error: unknown) => error,
  );
}

export function expectError<T extends Error>(
  failure: unknown,
  ctor: { new (...args: never[]): T; readonly name: string },
): T {
  if (!(failure instanceof ctor)) {
    const observed = failure instanceof Error ? failure.message : typeof failure;
    throw new Error(`expected ${ctor.name}, got ${observed}`);
  }
  return failure;
}

/** Parses a sampled lock file strictly: the whole content must be one lock record. */
export function sampleLockPid(text: string): number {
  const parsed: unknown = JSON.parse(text);
  if (typeof parsed !== 'object' || parsed === null || !('pid' in parsed)) {
    throw new Error(`lock file content is not a lock record: ${text}`);
  }
  const pid: unknown = parsed.pid;
  if (typeof pid !== 'number' || !Number.isInteger(pid)) {
    throw new Error(`lock record has no integer pid: ${text}`);
  }
  if (!text.endsWith('}\n')) throw new Error(`lock record is not terminated: ${text}`);
  return pid;
}

/** The whole `writer.lock` record the reporter writes (`{ pid, started_at }`), one line. */
export function writerLockRecord(pid: number, startedAt = fixedIso): string {
  return `${JSON.stringify({ pid, started_at: startedAt })}\n`;
}

// These predicates test the lock module, not the helpers, but they live here because every
// spec imports this file: they must run once, as this file, never inside an importer. A
// runner that stopped reporting the running file would drop them, so that case throws.
const runningFile = expect.getState().testPath;
if (runningFile === undefined) {
  throw new Error('vitest did not report the running spec file; the kit specs would be skipped');
}
if (runningFile === fileURLToPath(import.meta.url)) {
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
}
