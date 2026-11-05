// Fixtures shared by the spool.lock specs: lock records, a pid no test process can have, and a
// real `mkfifo` probe. `failureOf` and `expectError` live in `spool.testing`.
import { execFile } from 'node:child_process';

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
