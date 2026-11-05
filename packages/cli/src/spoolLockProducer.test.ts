// ProducerSpool.withLock puts spool.lock around a producer operation: a second CLI process
// on the same directory is refused, and a throwing callback still releases the lock.

import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ProducerSpool, defaultSpoolMaxBytes } from './spool.js';
import { SpoolLockedError, readSpoolLock } from './spoolLock.js';
import { expectError, failureOf } from './spool.testing.js';

describe('ProducerSpool.withLock', () => {
  let root: string;
  let spoolDir: string;

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), 'evidgate-spool-lock-'));
    spoolDir = join(root, 'spool');
    await mkdir(spoolDir, { recursive: true, mode: 0o700 });
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  function openSpool(): Promise<ProducerSpool> {
    return ProducerSpool.open({
      spoolDir,
      runId: 'run-04.2.3',
      producerId: 'producer-primary',
      maxBytes: defaultSpoolMaxBytes,
      createIfMissing: true,
    });
  }

  it('excludes a second CLI process and re-locks after release', async () => {
    const first = await openSpool();
    const second = await openSpool();
    const failure = await first.withLock(() =>
      failureOf(second.withLock(() => Promise.resolve('second'))),
    );
    const locked = expectError(failure, SpoolLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(await readSpoolLock(first.directory)).toBeNull();
    expect(await first.withLock(() => Promise.resolve('again'))).toBe('again');
  });

  it('releases the lock when the callback throws', async () => {
    const spool = await openSpool();
    const failure = await failureOf(spool.withLock(() => Promise.reject(new Error('send failed'))));
    expect(failure).toBeInstanceOf(Error);
    expect(await readSpoolLock(spool.directory)).toBeNull();
    expect(await spool.withLock(() => Promise.resolve('ok'))).toBe('ok');
  });
});
