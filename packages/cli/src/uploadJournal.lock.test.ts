// A producer lock held by another writer: the journal waits it out with bounded backoff
// instead of failing the upload, the wait is bounded by the upload deadline or by a per-call
// cap, and the last `spool_locked` surfaces unchanged once the budget is spent.
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { SpoolLockedError } from './spoolLock.js';
import { UploadJournal } from './uploadJournal.js';
import { artifactId, entry, identity, useUploadJournalSpec } from './uploadJournal.testing.js';

describe('upload journal', () => {
  const { producerDir } = useUploadJournalSpec();

  // `events send` holds the same producer lock, so a journal write waits it out with bounded
  // backoff instead of failing the upload at once.
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

  // The wait is bounded by the upload deadline by default, so a long `events send` does
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

  // The per-call cap is what the post-transfer best-effort updates use. It shortens THAT
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
});
