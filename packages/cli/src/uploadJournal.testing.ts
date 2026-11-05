// The fixtures the upload-journal specs share: the identity an upload derives from its
// request, a journal record with every field defaulted, and a producer directory that is
// removed after each test. Nothing here reaches the network or waits on a real clock.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only, and
// tsconfig.json excludes it from the emitting project. tsconfig.spec.json type-checks it.
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach } from 'vitest';

import { UploadJournal, type UploadIdentity, type UploadJournalEntry } from './uploadJournal.js';

export const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
export const otherArtifactId = '0a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d';
export const sha256 = `sha256:${'ab'.repeat(32)}`;
export const otherSha = `sha256:${'cd'.repeat(32)}`;
const executionKey = 'a3'.repeat(32);

/** The identity an upload derives from its request (kind, execution, attempt, media type). */
export function identity(overrides: Partial<UploadIdentity> = {}): UploadIdentity {
  return {
    file_sha256: sha256,
    size: 4_096,
    kind: 'trace',
    execution_key: executionKey,
    attempt_no: 1,
    media_type: 'application/zip',
    ...overrides,
  };
}

export function entry(overrides: Partial<UploadJournalEntry> = {}): UploadJournalEntry {
  return {
    artifact_id: artifactId,
    ...identity(),
    put_done: false,
    complete_done: false,
    mint_no: 0,
    ...overrides,
  };
}

export interface UploadJournalSpec {
  /** A producer directory shaped exactly like the chunk spool's. */
  producerDir(this: void): Promise<string>;
  /** A journal over one producer directory, with a lock that really serialises. */
  journal(this: void, dir: string): Promise<UploadJournal>;
}

/** Registers the temp-directory cleanup of the calling spec file. */
export function useUploadJournalSpec(): UploadJournalSpec {
  const dirs: string[] = [];

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function producerDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-journal-'));
    dirs.push(dir);
    return dir;
  }

  async function journal(dir: string): Promise<UploadJournal> {
    let tail: Promise<unknown> = Promise.resolve();
    const withLock = <T>(fn: () => Promise<T>): Promise<T> => {
      const next = tail.then(fn, fn);
      tail = next.catch(() => undefined);
      return next;
    };
    return UploadJournal.open({ producerDirectory: dir, withLock });
  }

  return { producerDir, journal };
}
