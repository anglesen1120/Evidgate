// The reporter's own `writer.lock` is authoritative: a live reporter blocks the drain with
// `spool_locked`, while a dead lock and the reporter's other files (its shard record, its
// upload journal) never disturb it.
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join, relative } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';
import { ProducerSpool } from '../spool.js';
import { writerLockFileName } from '../spoolLock.js';

import {
  acked,
  baseEnv,
  capture,
  cleanupSpecState,
  defaultMaxBytes,
  envelopeText,
  invoke,
  listFiles,
  openSpool,
  producer,
  runId,
  sendArgv,
  stubFetch,
  tempDir,
  virtualClock,
} from './eventsSend.testing.js';

/** Every file under `directory` with its bytes, for a byte-identity assertion. */
async function snapshotTree(directory: string): Promise<Array<readonly [string, string]>> {
  const files = (await listFiles(directory)).sort();
  const snapshot: Array<readonly [string, string]> = [];
  for (const path of files) {
    snapshot.push([relative(directory, path), await readFile(path, 'utf8')]);
  }
  return snapshot;
}

/** A pid that is certainly dead: a child process spawned and already waited for. */
function exitedPid(): number {
  const child = spawnSync(process.execPath, ['-e', ''], { stdio: 'ignore' });
  if (child.pid === undefined) throw new Error('could not start the short-lived child');
  return child.pid;
}

/** The whole `{ pid, started_at }` record the reporter writes into `writer.lock`. */
function writerLockText(pid: number): string {
  return `${JSON.stringify({ pid, started_at: '2026-01-01T00:00:00.000Z' })}\n`;
}

describe('events send', () => {
  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await cleanupSpecState();
  });

  // D-S56-Q8: the reporter holds its own `writer.lock` for the life of a writer; the drain
  // is a chunk reader, so it refuses with `spool_locked` (exit 3) instead of acking events
  // out from under a running test run.
  it('refuses to drain while the reporter still writes the producer (D-S56-Q8)', async () => {
    const spoolDir = await tempDir();
    const spool = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId: producer,
      maxBytes: defaultMaxBytes,
      createIfMissing: true,
    });
    await spool.append([envelopeText(1), envelopeText(2)]);
    const producerDir = join(spoolDir, runId, producer);
    // A live reporter: this test process's pid is alive for certain.
    await writeFile(join(producerDir, writerLockFileName), writerLockText(process.pid), {
      mode: 0o600,
    });
    const before = await snapshotTree(join(spoolDir, runId));

    const clock = virtualClock();
    const stub = stubFetch((call) => acked(call));
    const out = capture();
    const code = await invoke(sendArgv(), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain('spool_locked');
    expect(out.stderr.join('')).toContain(String(process.pid));
    expect(out.stderr.join('')).toContain('the reporter is still writing this producer');
    // Nothing was sent, nothing was acked, nothing was deleted: the producer directory is
    // byte-identical, `writer.lock` included.
    expect(stub.calls).toHaveLength(0);
    expect(clock.sleeps).toEqual([]);
    expect(await snapshotTree(join(spoolDir, runId))).toEqual(before);
    expect(existsSync(join(producerDir, 'cursor.json'))).toBe(false);
  });

  it('drains a producer dir that also holds shards.json, uploads/ and a dead writer.lock', async () => {
    const spoolDir = await tempDir();
    const spool = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId: producer,
      maxBytes: defaultMaxBytes,
      createIfMissing: true,
    });
    await spool.append([envelopeText(1), envelopeText(2)]);
    const producerDir = join(spoolDir, runId, producer);
    // The reporter's sparse record, its upload journal row and a lock left by a reporter
    // that died: none of them is a chunk, and none of them may disturb the drain.
    const shardsText = `${JSON.stringify({
      producer_id: producer,
      shard_index: 1,
      shard_total: 1,
      sequence_next: 3,
    })}\n`;
    await writeFile(join(producerDir, 'shards.json'), shardsText);
    const artifactId = '5e0f6a3c-8b7d-4c2e-9f10-2a3b4c5d6e7f';
    const journalText = `${JSON.stringify({
      artifact_id: artifactId,
      file_sha256: `sha256:${'c'.repeat(64)}`,
      size: 42,
      put_done: false,
      complete_done: false,
      mint_no: 0,
    })}\n`;
    await mkdir(join(producerDir, 'uploads'), { recursive: true, mode: 0o700 });
    await writeFile(join(producerDir, 'uploads', `${artifactId}.json`), journalText);
    const lockText = writerLockText(exitedPid());
    await writeFile(join(producerDir, writerLockFileName), lockText, { mode: 0o600 });

    const clock = virtualClock();
    const stub = stubFetch((call) => acked(call));
    const out = capture();
    const code = await invoke(sendArgv(), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(0);
    expect(out.stderr.join('')).toBe('');
    expect(stub.calls).toHaveLength(1);
    expect(out.stdout.join('')).toBe(
      `evidgate: sent run=${runId} producer=${producer} batches=1 accepted=2 duplicate=0\n`,
    );
    const reopened = await openSpool(spoolDir);
    expect(await reopened.cursor()).toMatchObject({
      nextUnackedSequence: 3,
      lastAckedSequence: 2,
      ackedCount: 2,
    });
    // The reporter's files are untouched, and the dead lock is ignored rather than deleted
    // (the reporter's next start owns the takeover).
    expect(await readFile(join(producerDir, 'shards.json'), 'utf8')).toBe(shardsText);
    expect(await readFile(join(producerDir, writerLockFileName), 'utf8')).toBe(lockText);
    expect(await readFile(join(producerDir, 'uploads', `${artifactId}.json`), 'utf8')).toBe(
      journalText,
    );
  });
});
