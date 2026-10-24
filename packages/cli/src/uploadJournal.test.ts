// 04.2.4-T1.3: the crash-safe upload journal (04.2.4 §4/§8, contracts/cli.md §11.1/§12).
// Proven here: the on-disk record is the upload identity plus the four state fields, the
// directory is 0700 and every record 0600, no temp file survives a write, the resume
// selection and the mint derivation survive a restart and never cross identities, a legacy
// record is ignored instead of crashing a resume, a busy producer lock is waited out with
// bounded backoff, and a tampered record, a symlinked `uploads/` or a missing record is
// refused instead of trusted. Nothing here waits on a real clock or touches the network.
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
import { SpoolLockedError } from './spoolLock.js';
import {
  UploadJournal,
  journalFileName,
  journalFor,
  uploadIdentityDigest,
  uploadsDirectoryName,
  type UploadIdentity,
  type UploadJournalEntry,
} from './uploadJournal.js';

const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
const otherArtifactId = '0a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d';
const sha256 = `sha256:${'ab'.repeat(32)}`;
const otherSha = `sha256:${'cd'.repeat(32)}`;
const executionKey = 'a3'.repeat(32);

/** The identity an upload derives from its request (kind, execution, attempt, media type). */
function identity(overrides: Partial<UploadIdentity> = {}): UploadIdentity {
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

function entry(overrides: Partial<UploadJournalEntry> = {}): UploadJournalEntry {
  return {
    artifact_id: artifactId,
    ...identity(),
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

    const path = join(dir, uploadsDirectoryName, journalFileName(identity(), artifactId));
    const parsed = JSON.parse(await readFile(path, 'utf8')) as Record<string, unknown>;
    expect(Object.keys(parsed).sort()).toEqual(
      [
        'artifact_id',
        'attempt_no',
        'complete_done',
        'execution_key',
        'file_sha256',
        'kind',
        'media_type',
        'mint_no',
        'put_done',
        'size',
      ].sort(),
    );
    expect(parsed['put_done']).toBe(false);
    // A journal survives on disk after the process dies: it can never carry the presigned
    // URL, a token or a header value (04 §6, S3).
    const text = await readFile(path, 'utf8');
    expect(text).not.toContain('http');
    expect(text).not.toContain('X-Amz');
    expect(text).not.toContain('authorization');
  });

  // F6: the record name is the identity prefix, so a lookup filters by name before it reads
  // anything. The prefix is derived from the identity, not from the artifact id.
  it('names_the_record_by_the_identity_prefix', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await store.write(entry());

    const names = await readdir(join(dir, uploadsDirectoryName));
    const prefix = uploadIdentityDigest(identity()).slice(0, 16);
    expect(names).toEqual([`${prefix}-${artifactId}.json`]);
    expect(prefix).toMatch(/^[0-9a-f]{16}$/);
  });

  it('uses_owner_only_permissions', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await store.write(entry());

    const directoryMode = (await stat(join(dir, uploadsDirectoryName))).mode & 0o777;
    const fileMode =
      (await stat(join(dir, uploadsDirectoryName, journalFileName(identity(), artifactId)))).mode &
      0o777;
    expect(directoryMode).toBe(0o700);
    expect(fileMode).toBe(0o600);
  });

  it('leaves_no_temp_file_behind', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await store.write(entry());
    await store.update(artifactId, { put_done: true });

    const names = await readdir(join(dir, uploadsDirectoryName));
    expect(names).toEqual([journalFileName(identity(), artifactId)]);
  });

  it('finds_the_highest_incomplete_mint_and_never_a_completed_row', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    await store.write(entry({ artifact_id: otherArtifactId, mint_no: 0 }));
    await store.write(entry({ mint_no: 1, put_done: true }));

    // A restart picks the highest mint for the same identity.
    const resumed = await store.findResumable(identity());
    expect(resumed?.mint_no).toBe(1);
    expect(resumed?.put_done).toBe(true);

    // A different digest or size is a different file: nothing to resume.
    expect(await store.findResumable(identity({ file_sha256: otherSha }))).toBeUndefined();
    expect(await store.findResumable(identity({ size: 4_097 }))).toBeUndefined();

    // A completed row is the one a re-run of the same file finds (it answers `ready`
    // without a request), so a re-run never mints a second artifact row.
    await store.update(artifactId, { complete_done: true });
    const completed = await store.findResumable(identity());
    expect(completed?.artifact_id).toBe(artifactId);
    expect(completed?.complete_done).toBe(true);
  });

  // F1: the same bytes under a different execution, kind, attempt or media type are a
  // DIFFERENT upload, so they neither resume the row nor reuse its mint.
  it.each([
    ['execution_key', { execution_key: 'b4'.repeat(32) }],
    ['kind', { kind: 'raw_report' as const }],
    ['attempt_no', { attempt_no: 2 }],
    ['media_type', { media_type: 'application/octet-stream' }],
  ])('never_matches_a_record_from_another_%s', async (_field, overrides) => {
    const dir = await producerDir();
    const store = await journal(dir);
    await store.write(entry({ mint_no: 2, put_done: true }));

    const other = identity(overrides);
    expect(await store.findResumable(other)).toBeUndefined();
    expect(await store.nextMintNo(other)).toBe(0);
    // The original identity still resumes, so the miss is the identity's, not the store's.
    expect((await store.findResumable(identity()))?.mint_no).toBe(2);
  });

  // F1/F6: a record written by an older CLI has no identity fields (and an older name):
  // it is ignored as a resume candidate, never parsed into a row, and never crashes.
  it('ignores_a_legacy_record_without_the_identity_fields', async () => {
    const dir = await producerDir();
    await mkdir(join(dir, uploadsDirectoryName), { recursive: true });
    await writeFile(
      join(dir, uploadsDirectoryName, `${artifactId}.json`),
      JSON.stringify({
        artifact_id: artifactId,
        file_sha256: sha256,
        size: 4_096,
        put_done: true,
        complete_done: false,
        mint_no: 3,
      }),
      'utf8',
    );
    const store = await journal(dir);

    expect(await store.entries()).toEqual([]);
    expect(await store.findResumable(identity())).toBeUndefined();
    expect(await store.nextMintNo(identity())).toBe(0);
  });

  // F6: a lookup reads only its own prefix, so a record of another upload cannot fail it —
  // even when that record does not parse.
  it('parses_only_the_candidates_of_the_lookup', async () => {
    const dir = await producerDir();
    const uploads = join(dir, uploadsDirectoryName);
    await mkdir(uploads, { recursive: true });
    const foreign = journalFileName(identity({ file_sha256: otherSha }), otherArtifactId);
    await writeFile(join(uploads, foreign), '{"size":', 'utf8');
    const store = await journal(dir);
    await store.write(entry());

    expect((await store.findResumable(identity()))?.artifact_id).toBe(artifactId);
    // `entries()` reads every current record, so the broken one is still refused there.
    await expect(store.entries()).rejects.toMatchObject({ code: 'spool_corrupt' });
  });

  it('derives_the_same_next_mint_across_restarts', async () => {
    const dir = await producerDir();
    const first = await journal(dir);
    expect(await first.nextMintNo(identity())).toBe(0);
    await first.write(entry());

    // A new process (a new `UploadJournal`) must derive the same number from the disk.
    const second = await journal(dir);
    expect(await second.nextMintNo(identity())).toBe(1);
    await second.write(entry({ artifact_id: otherArtifactId, mint_no: 1 }));
    expect(await second.nextMintNo(identity())).toBe(2);
  });

  // F5: `events send` holds the same producer lock, so a journal write waits it out with
  // bounded backoff instead of failing the upload at once.
  it('waits_out_a_busy_producer_lock', async () => {
    const dir = await producerDir();
    const clock = { now: 1_000 };
    const sleeps: number[] = [];
    let busy = 2;
    const store = await UploadJournal.open({
      producerDirectory: dir,
      withLock: async (fn) => {
        if (busy > 0) {
          busy -= 1;
          throw new SpoolLockedError(join(dir, 'spool.lock'), 4_242);
        }
        return fn();
      },
      now: () => clock.now,
      sleep: (milliseconds) => {
        sleeps.push(milliseconds);
        clock.now += milliseconds;
        return Promise.resolve();
      },
      deadlineAt: 1_000 + 60_000,
    });

    await store.write(entry());
    expect(sleeps).toEqual([25, 50]);
    expect((await store.findResumable(identity()))?.artifact_id).toBe(artifactId);
  });

  // F5: the wait is bounded by the upload deadline by default, so a long `events send` does
  // not fail the write — and the last `spool_locked` surfaces unchanged when the deadline is
  // spent.
  it('waits_out_a_lock_held_past_the_old_ten_second_budget', async () => {
    const dir = await producerDir();
    const clock = { now: 1_000 };
    let busy = 6;
    // 25 + 50 + 100 + 200 = 375 ms of sleeps, but the clock jumps to `busy` seconds so the
    // wait itself lasts 20 simulated seconds — longer than the old fixed 10 s budget.
    const store = await UploadJournal.open({
      producerDirectory: dir,
      withLock: (fn) => {
        if (busy > 0) {
          busy -= 1;
          clock.now += 3_500;
          return Promise.reject(new SpoolLockedError(join(dir, 'spool.lock'), 4_242));
        }
        return fn();
      },
      now: () => clock.now,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      deadlineAt: 1_000 + 60_000,
    });

    await store.write(entry());
    expect((await store.findResumable(identity()))?.artifact_id).toBe(artifactId);
  });
  it('surfaces_spool_locked_when_the_wait_budget_is_spent', async () => {
    const dir = await producerDir();
    const clock = { now: 1_000 };
    const store = await UploadJournal.open({
      producerDirectory: dir,
      withLock: () => Promise.reject(new SpoolLockedError(join(dir, 'spool.lock'), 4_242)),
      now: () => clock.now,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      deadlineAt: 1_000 + 3_600_000,
      maxWaitMs: 100,
    });

    await expect(store.write(entry())).rejects.toMatchObject({ code: 'spool_locked' });
    await expect(store.update(artifactId, { put_done: true })).rejects.toMatchObject({
      code: 'spool_locked',
    });
    // The budget stopped the wait; the 1 h upload deadline was never reached.
    expect(clock.now).toBeLessThan(1_000 + 200);
  });

  // N1: the per-call cap is what the post-transfer best-effort updates use. It shortens THAT
  // call only — the pre-PUT `write` that records a minted row keeps the deadline budget, so a
  // long `events send` still does not lose the record a restart resumes from.
  it('caps_one_update_without_shrinking_the_write_budget', async () => {
    const dir = await producerDir();
    const clock = { now: 1_000 };
    let refusals = 0;
    const store = await UploadJournal.open({
      producerDirectory: dir,
      withLock: (fn) => {
        if (refusals > 0) {
          refusals -= 1;
          return Promise.reject(new SpoolLockedError(join(dir, 'spool.lock'), 4_242));
        }
        return fn();
      },
      now: () => clock.now,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      deadlineAt: 1_000 + 3_600_000,
    });
    await store.write(entry());

    refusals = 100;
    await expect(
      store.update(artifactId, { put_done: true }, { maxWaitMs: 2_000 }),
    ).rejects.toMatchObject({ code: 'spool_locked' });
    const capped = clock.now - 1_000;
    expect(capped).toBeGreaterThan(1_000);
    expect(capped).toBeLessThan(2_000);

    // The journal's own budget is untouched: this write waits past the 2 s cap and lands.
    refusals = 8;
    await expect(store.write(entry({ put_done: true }))).resolves.toBeUndefined();
    expect(clock.now - 1_000).toBeGreaterThan(2_000);
    expect((await store.findResumable(identity()))?.put_done).toBe(true);
  });

  it('refuses_a_tampered_record', async () => {
    const dir = await producerDir();
    await mkdir(join(dir, uploadsDirectoryName), { recursive: true });
    await writeFile(
      join(dir, uploadsDirectoryName, journalFileName(identity(), artifactId)),
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
      join(dir, uploadsDirectoryName, journalFileName(identity(), otherArtifactId)),
      JSON.stringify(entry()),
      'utf8',
    );
    const store = await journal(dir);

    await expect(store.entries()).rejects.toMatchObject({ code: 'spool_corrupt' });
  });

  it('refuses_a_record_whose_name_disagrees_with_its_identity', async () => {
    const dir = await producerDir();
    await mkdir(join(dir, uploadsDirectoryName), { recursive: true });
    // The name says "another file", the content says this one: the prefix is derived from
    // the content, so the disagreement is a tampered record.
    await writeFile(
      join(dir, uploadsDirectoryName, journalFileName(identity({ size: 4_097 }), artifactId)),
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

  it('refuses_an_identity_with_an_unknown_kind', async () => {
    const dir = await producerDir();
    const store = await journal(dir);
    const bad = { ...entry(), kind: 'not-a-kind' } as unknown as UploadJournalEntry;
    await expect(store.write(bad)).rejects.toMatchObject({ code: 'spool_corrupt' });
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
