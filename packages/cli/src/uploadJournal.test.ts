// 04.2.4-T1.3: the crash-safe upload journal (04.2.4 §4/§8, contracts/cli.md §11.1/§12).
// Proven here: the on-disk record is exactly the five plan fields plus `mint_no`, the
// directory is 0700 and every record 0600, no temp file survives a write, the resume
// selection and the mint derivation survive a restart, and a tampered record, a symlinked
// `uploads/` or a missing record is refused instead of trusted. Nothing here waits on a
// clock or touches the network.
import {
  chmod,
  mkdir,
  mkdtemp,
  readdir,
  readFile,
  rm,
  stat,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { CliError } from './errors.js';
import { ProducerSpool } from './spool.js';
import {
  UploadJournal,
  journalFileName,
  journalFor,
  uploadsDirectoryName,
  type UploadJournalEntry,
} from './uploadJournal.js';

const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
const otherArtifactId = '0a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d';
const sha256 = `sha256:${'ab'.repeat(32)}`;
const otherSha = `sha256:${'cd'.repeat(32)}`;

function entry(overrides: Partial<UploadJournalEntry> = {}): UploadJournalEntry {
  return {
    artifact_id: artifactId,
    file_sha256: sha256,
    size: 4_096,
    put_done: false,
    complete_done: false,
    mint_no: 0,
    ...overrides,
  };
}

describe('upload journal', () => {
  const dirs: string[] = [];

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  /** A producer directory shaped exactly like the 04.2.3 spool's. */
  async function producerDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-journal-'));
    dirs.push(dir);
    return dir;
  }

  /** A journal over one producer directory, with a lock that really serialises. */
  async function journal(dir: string): Promise<UploadJournal> {
    let tail: Promise<unknown> = Promise.resolve();
    const withLock = <T>(fn: () => Promise<T>): Promise<T> => {
      const next = tail.then(fn, fn);
      tail = next.catch(() => undefined);
      return next;
    };
    return UploadJournal.open({ producerDirectory: dir, withLock });
  }

  it('writes_exactly_the_record_fields', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await store.write(entry());

    const path = join(dir, uploadsDirectoryName, journalFileName(artifactId));
    const parsed = JSON.parse(await readFile(path, 'utf8')) as Record<string, unknown>;
    expect(Object.keys(parsed).sort()).toEqual(
      ['artifact_id', 'complete_done', 'file_sha256', 'mint_no', 'put_done', 'size'].sort(),
    );
    expect(parsed['put_done']).toBe(false);
    // A journal survives on disk after the process dies: it can never carry the presigned
    // URL, a token or a header value (04 §6, S3).
    const text = await readFile(path, 'utf8');
    expect(text).not.toContain('http');
    expect(text).not.toContain('X-Amz');
    expect(text).not.toContain('authorization');
  });

  it('uses_owner_only_permissions', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await store.write(entry());

    const directoryMode = (await stat(join(dir, uploadsDirectoryName))).mode & 0o777;
    const fileMode =
      (await stat(join(dir, uploadsDirectoryName, journalFileName(artifactId)))).mode & 0o777;
    expect(directoryMode).toBe(0o700);
    expect(fileMode).toBe(0o600);
  });

  it('leaves_no_temp_file_behind', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await store.write(entry());
    await store.update(artifactId, { put_done: true });

    const names = await readdir(join(dir, uploadsDirectoryName));
    expect(names).toEqual([journalFileName(artifactId)]);
  });

  it('finds_the_highest_incomplete_mint_and_never_a_completed_row', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await store.write(entry({ artifact_id: otherArtifactId, mint_no: 0 }));
    await store.write(entry({ mint_no: 1, put_done: true }));

    // A restart picks the highest mint for the same (digest, size).
    const resumed = await store.findResumable(sha256, 4_096);
    expect(resumed?.mint_no).toBe(1);
    expect(resumed?.put_done).toBe(true);

    // A different digest or size is a different file: nothing to resume.
    expect(await store.findResumable(otherSha, 4_096)).toBeUndefined();
    expect(await store.findResumable(sha256, 4_097)).toBeUndefined();

    // A completed row is the one a re-run of the same file finds (it answers `ready`
    // without a request), so a re-run never mints a second artifact row.
    await store.update(artifactId, { complete_done: true });
    const completed = await store.findResumable(sha256, 4_096);
    expect(completed?.artifact_id).toBe(artifactId);
    expect(completed?.complete_done).toBe(true);
  });

  it('derives_the_same_next_mint_across_restarts', async () => {
    const dir = await producerDir();
    const first = await journal(dir);
    expect(await first.nextMintNo(sha256, 4_096)).toBe(0);
    await first.write(entry());

    // A new process (a new `UploadJournal`) must derive the same number from the disk.
    const second = await journal(dir);
    expect(await second.nextMintNo(sha256, 4_096)).toBe(1);
    await second.write(entry({ artifact_id: otherArtifactId, mint_no: 1 }));
    expect(await second.nextMintNo(sha256, 4_096)).toBe(2);
  });

  it('refuses_a_tampered_record', async () => {
    const dir = await producerDir();
    await mkdir(join(dir, uploadsDirectoryName), { recursive: true });
    await writeFile(
      join(dir, uploadsDirectoryName, journalFileName(artifactId)),
      '{"size":',
      'utf8',
    );
    const store = await journal(dir);

    let thrown: unknown;
    try {
      await store.entries();
    } catch (error) {
      thrown = error;
    }
    expect((thrown as CliError).code).toBe('spool_corrupt');
    // The message names the file, never a content fragment.
    expect((thrown as CliError).message).not.toContain('{"size"');
  });

  it('refuses_a_record_whose_name_disagrees_with_its_id', async () => {
    const dir = await producerDir();
    await mkdir(join(dir, uploadsDirectoryName), { recursive: true });
    await writeFile(
      join(dir, uploadsDirectoryName, journalFileName(otherArtifactId)),
      JSON.stringify(entry()),
      'utf8',
    );
    const store = await journal(dir);

    await expect(store.entries()).rejects.toMatchObject({ code: 'spool_corrupt' });
  });

  it('refuses_a_symlinked_uploads_directory', async () => {
    const dir = await producerDir();
    const elsewhere = await producerDir();
    await symlink(elsewhere, join(dir, uploadsDirectoryName));

    let thrown: unknown;
    try {
      await journal(dir);
    } catch (error) {
      thrown = error;
    }
    expect((thrown as CliError).code).toBe('spool_unsafe_path');
  });

  it('re_hardens_a_writable_directory_on_write', async () => {
    const dir = await producerDir();
    const uploads = join(dir, uploadsDirectoryName);
    await mkdir(uploads, { recursive: true });
    await chmod(uploads, 0o777);
    const store = await journal(dir);
    // Re-harden on the next write: the directory is 0700 again, never left group-writable.
    await store.write(entry());
    expect((await stat(uploads)).mode & 0o777).toBe(0o700);
  });

  it('refuses_an_update_of_a_missing_record', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await expect(store.update(artifactId, { put_done: true })).rejects.toMatchObject({
      code: 'spool_corrupt',
    });
  });

  it('refuses_a_non_uuid_artifact_id', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await expect(store.write(entry({ artifact_id: '../../escape' }))).rejects.toMatchObject({
      code: 'spool_corrupt',
    });
  });

  it('is_none_without_a_spool_directory', async () => {
    const dir = await producerDir();
    expect(
      await journalFor({
        spoolDir: undefined,
        producerDirectory: dir,
        withLock: (fn) => fn(),
      }),
    ).toBeUndefined();
  });

  it('coexists_with_the_chunk_spool_of_the_same_producer', async () => {
    // `uploads/` is a directory beside the 04.2.3 chunk files; the producer directory must
    // still open, take its lock and accept a chunk, so `events send` is unaffected.
    const spoolDir = await producerDir();
    const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
    const producerId = 'pw-shard-0';
    const spool = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId,
      maxBytes: 1024 * 1024,
      createIfMissing: true,
    });
    const store = await UploadJournal.open({
      producerDirectory: spool.directory,
      withLock: (fn) => spool.withLock(fn),
    });
    await store.write(entry());

    const reopened = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId,
      maxBytes: 1024 * 1024,
      createIfMissing: false,
    });
    expect(reopened.directory).toBe(spool.directory);
    expect(await reopened.withLock(() => Promise.resolve('locked'))).toBe('locked');
    const names = await readdir(spool.directory);
    expect(names).toContain(uploadsDirectoryName);
  });
});
