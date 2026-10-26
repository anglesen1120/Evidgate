// 04.3.3-T2.2: the `onEnd` upload drain (04.3.3 §5 bounded drain; §7 NFR-04-1 for the
// 120 s deadline, §7 Performance for the ≤ 2 concurrency cap; AC5): two uploads at once,
// FIFO start order, a hard deadline that abandons (never rejects) and a job failure that
// keeps the other results. Every case drives an injected virtual clock, so no test waits
// out the real 120 s deadline.
import { describe, expect, it } from 'vitest';

import {
  DEFAULT_DRAIN_DEADLINE_MS,
  UploadDrain,
  type DrainJobContext,
  type DrainTimers,
} from './drain.js';

interface FakeTimerHandle {
  unref(): void;
}

/** A virtual clock behind the drain's `timers`/`now` seams. */
interface FakeClock {
  readonly timers: DrainTimers;
  /** Every delay `set()` was called with, in order. */
  readonly setDelays: readonly number[];
  /** Every handle passed to `clear()`. */
  readonly cleared: readonly unknown[];
  /** Timers still scheduled (neither fired nor cleared). */
  scheduledCount(): number;
  now(): number;
  /** Moves the clock forward, firing due timers and draining microtasks between them. */
  advance(milliseconds: number): Promise<void>;
}

/**
 * `Promise.withResolvers` is ES2024 and the workspace targets ES2022 (tsconfig.base), so
 * the executor form is how the settle handles escape here.
 */
function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T | PromiseLike<T>) => void;
  reject: (reason?: unknown) => void;
} {
  let resolve: (value: T | PromiseLike<T>) => void = () => undefined;
  let reject: (reason?: unknown) => void = () => undefined;
  const promise = new Promise<T>((settle, fail) => {
    resolve = settle;
    reject = fail;
  });
  return { promise, resolve, reject };
}

/** Drains the microtask queue with a real `setImmediate` — immediate, never a sleep. */
function flush(): Promise<void> {
  return new Promise<void>((resolve) => {
    setImmediate(() => {
      resolve();
    });
  });
}

function createFakeClock(unref?: () => void): FakeClock {
  let current = 0;
  let nextId = 1;
  const scheduled = new Map<number, { at: number; fn: () => void }>();
  const handles = new Map<number, FakeTimerHandle>();
  const setDelays: number[] = [];
  const cleared: unknown[] = [];
  const timers: DrainTimers = {
    set(milliseconds, fn) {
      const id = nextId;
      nextId += 1;
      const handle: FakeTimerHandle = { unref: unref ?? (() => undefined) };
      handles.set(id, handle);
      scheduled.set(id, { at: current + milliseconds, fn });
      setDelays.push(milliseconds);
      return handle;
    },
    clear(handle) {
      cleared.push(handle);
      for (const [id, candidate] of handles) {
        if (candidate === handle) {
          handles.delete(id);
          scheduled.delete(id);
        }
      }
    },
  };
  return {
    timers,
    setDelays,
    cleared,
    scheduledCount: () => scheduled.size,
    now: () => current,
    async advance(milliseconds) {
      current += milliseconds;
      for (;;) {
        const due = [...scheduled.entries()]
          .filter(([, timer]) => timer.at <= current)
          .sort(([, left], [, right]) => left.at - right.at);
        if (due.length === 0) {
          break;
        }
        for (const [id, timer] of due) {
          handles.delete(id);
          scheduled.delete(id);
          timer.fn();
        }
        await flush();
      }
      await flush();
    },
  };
}

describe('UploadDrain', () => {
  it('runs at most two jobs at once and starts them first in, first out', async () => {
    const drain = new UploadDrain();
    const gates = [0, 1, 2, 3, 4].map(() => deferred<void>());
    const started: number[] = [];
    let running = 0;
    let peak = 0;
    const results = gates.map((gate, index) =>
      drain.submit(async () => {
        started.push(index);
        running += 1;
        peak = Math.max(peak, running);
        try {
          await gate.promise;
        } finally {
          running -= 1;
        }
        return index;
      }),
    );

    expect(started).toEqual([0, 1]);
    expect(drain.pending).toBe(5);

    gates[0]?.resolve();
    await flush();
    expect(started).toEqual([0, 1, 2]);

    gates[1]?.resolve();
    await flush();
    expect(started).toEqual([0, 1, 2, 3]);

    for (const gate of gates) {
      gate.resolve();
    }
    await flush();

    expect(await Promise.all(results)).toEqual([
      { status: 'done', value: 0 },
      { status: 'done', value: 1 },
      { status: 'done', value: 2 },
      { status: 'done', value: 3 },
      { status: 'done', value: 4 },
    ]);
    expect(peak).toBe(2);
    expect(started).toEqual([0, 1, 2, 3, 4]);
    expect(drain.pending).toBe(0);
  });

  it('clamps the concurrency option into [1, 2] and keeps the start order', async () => {
    for (const [option, expected] of [
      [9, 2],
      [0, 1],
    ] as const) {
      const drain = new UploadDrain({ concurrency: option });
      const gates = [0, 1, 2].map(() => deferred<void>());
      const started: number[] = [];
      let running = 0;
      let peak = 0;
      const results = gates.map((gate, index) =>
        drain.submit(async () => {
          started.push(index);
          running += 1;
          peak = Math.max(peak, running);
          try {
            await gate.promise;
          } finally {
            running -= 1;
          }
          return index;
        }),
      );

      expect(started).toHaveLength(expected);
      for (const gate of gates) {
        gate.resolve();
      }
      await flush();
      await Promise.all(results);

      expect(peak).toBe(expected);
      expect(started).toEqual([0, 1, 2]);
    }
  });

  it('reports a rejected or synchronously throwing job as failed and keeps the queue moving', async () => {
    const drain = new UploadDrain({ concurrency: 1 });
    const refused = new Error('upload refused');
    const started: number[] = [];
    const outcomes = await Promise.all([
      drain.submit(() => {
        started.push(0);
        return Promise.reject(refused);
      }),
      drain.submit(() => {
        started.push(1);
        throw new Error('sync boom');
      }),
      drain.submit(() => {
        started.push(2);
        return Promise.resolve(3);
      }),
    ]);

    expect(started).toEqual([0, 1, 2]);
    expect(outcomes[0]).toEqual({ status: 'failed', error: refused });
    expect(outcomes[1]).toEqual({ status: 'failed', error: expect.any(Error) as unknown });
    expect(outcomes[2]).toEqual({ status: 'done', value: 3 });
    expect(drain.pending).toBe(0);
  });

  it('finishes immediately once every job settled and clears the deadline timer', async () => {
    const clock = createFakeClock();
    const drain = new UploadDrain({ timers: clock.timers, now: () => clock.now() });

    await expect(drain.submit(() => Promise.resolve('saved'))).resolves.toEqual({
      status: 'done',
      value: 'saved',
    });
    expect(drain.pending).toBe(0);

    const finish = drain.finish();
    await expect(finish).resolves.toEqual({ timedOut: false, abandoned: 0 });
    expect(clock.setDelays).toEqual([DEFAULT_DRAIN_DEADLINE_MS]);
    expect(clock.cleared).toHaveLength(1);
    expect(clock.scheduledCount()).toBe(0);
  });

  it('waits for a running job and clears its timer once it settles before the deadline', async () => {
    const clock = createFakeClock();
    const drain = new UploadDrain({ timers: clock.timers, now: () => clock.now() });
    const gate = deferred<void>();
    const job = drain.submit(async () => {
      await gate.promise;
      return 'uploaded';
    });

    const finish = drain.finish();
    expect(clock.scheduledCount()).toBe(1);

    gate.resolve();
    await expect(job).resolves.toEqual({ status: 'done', value: 'uploaded' });
    await expect(finish).resolves.toEqual({ timedOut: false, abandoned: 0 });
    expect(clock.cleared).toHaveLength(1);
    expect(clock.scheduledCount()).toBe(0);
  });

  it('resolves at the deadline, aborts the signal and abandons running and queued jobs', async () => {
    const clock = createFakeClock();
    const drain = new UploadDrain({ timers: clock.timers, now: () => clock.now() });
    const signals: AbortSignal[] = [];
    const started: number[] = [];
    const hung = deferred<void>();
    const first = drain.submit(async (context) => {
      signals.push(context.signal);
      started.push(0);
      await hung.promise;
      return 'late';
    });
    const second = drain.submit(async (context) => {
      signals.push(context.signal);
      started.push(1);
      await hung.promise;
      return 'late';
    });
    const third = drain.submit(() => {
      started.push(2);
      return Promise.resolve('never');
    });
    expect(started).toEqual([0, 1]);

    const finish = drain.finish();
    expect(drain.pending).toBe(3);

    await clock.advance(DEFAULT_DRAIN_DEADLINE_MS - 1);
    let finished = false;
    void finish.then(() => {
      finished = true;
    });
    await flush();
    expect(finished).toBe(false);
    expect(signals.every((signal) => signal.aborted)).toBe(false);

    await clock.advance(1);
    await expect(finish).resolves.toEqual({ timedOut: true, abandoned: 3 });
    expect(finished).toBe(true);
    await expect(first).resolves.toEqual({ status: 'deadline' });
    await expect(second).resolves.toEqual({ status: 'deadline' });
    await expect(third).resolves.toEqual({ status: 'deadline' });
    expect(started).toEqual([0, 1]);
    expect(signals.every((signal) => signal.aborted)).toBe(true);
    expect(drain.pending).toBe(0);
  });

  it('ignores a job that settles after the deadline instead of rejecting', async () => {
    const clock = createFakeClock();
    const drain = new UploadDrain({ timers: clock.timers, now: () => clock.now() });
    const unhandled: unknown[] = [];
    const onUnhandled = (reason: unknown): void => {
      unhandled.push(reason);
    };
    process.on('unhandledRejection', onUnhandled);
    try {
      const hung = deferred<void>();
      const outcome = drain.submit(async () => {
        await hung.promise;
        return 'late';
      });
      const finish = drain.finish();
      await clock.advance(DEFAULT_DRAIN_DEADLINE_MS);
      await expect(outcome).resolves.toEqual({ status: 'deadline' });
      await expect(finish).resolves.toEqual({ timedOut: true, abandoned: 1 });

      hung.reject(new Error('the server refused the upload after the deadline'));
      await flush();
      await flush();

      expect(unhandled).toEqual([]);
    } finally {
      process.off('unhandledRejection', onUnhandled);
    }
  });

  it('answers a submit after the deadline with a deadline outcome and never calls the job', async () => {
    const clock = createFakeClock();
    const drain = new UploadDrain({ timers: clock.timers, now: () => clock.now() });
    const started: string[] = [];
    const hung = deferred<void>();
    void drain.submit(async () => {
      started.push('hung');
      await hung.promise;
      return 'hung';
    });
    const finish = drain.finish();
    await clock.advance(DEFAULT_DRAIN_DEADLINE_MS);
    await finish;

    const late = drain.submit(() => {
      started.push('late');
      return Promise.resolve('late');
    });
    await expect(late).resolves.toEqual({ status: 'deadline' });
    expect(started).toEqual(['hung']);
  });

  it('returns the same promise from a second finish() and seals submits once resolved', async () => {
    const clock = createFakeClock();
    const drain = new UploadDrain({ timers: clock.timers, now: () => clock.now() });
    await expect(drain.submit(() => Promise.resolve(1))).resolves.toEqual({
      status: 'done',
      value: 1,
    });

    const first = drain.finish();
    const second = drain.finish();
    expect(second).toBe(first);
    await expect(first).resolves.toEqual({ timedOut: false, abandoned: 0 });
    await expect(second).resolves.toEqual({ timedOut: false, abandoned: 0 });

    const sealed = drain.submit(() => Promise.resolve(2));
    await expect(sealed).resolves.toEqual({ status: 'deadline' });
  });

  it('still runs a job submitted after finish() but before the deadline', async () => {
    const clock = createFakeClock();
    const drain = new UploadDrain({ timers: clock.timers, now: () => clock.now() });
    const gate = deferred<void>();
    const first = drain.submit(async () => {
      await gate.promise;
      return 1;
    });
    const finish = drain.finish();

    const second = drain.submit(() => Promise.resolve(2));
    await expect(second).resolves.toEqual({ status: 'done', value: 2 });

    gate.resolve();
    await expect(first).resolves.toEqual({ status: 'done', value: 1 });
    await expect(finish).resolves.toEqual({ timedOut: false, abandoned: 0 });
  });

  it('reports the remaining time from Infinity to the clamped remainder', async () => {
    const clock = createFakeClock();
    const drain = new UploadDrain({
      timers: clock.timers,
      now: () => clock.now(),
      deadlineMs: 5_000,
    });
    const contexts: DrainJobContext[] = [];
    const gate = deferred<void>();
    const job = drain.submit(async (context) => {
      contexts.push(context);
      await gate.promise;
      return 'uploaded';
    });
    const context = contexts[0];
    if (context === undefined) {
      throw new Error('the drain did not start the job');
    }

    expect(context.remainingMs()).toBe(Number.POSITIVE_INFINITY);
    expect(context.signal.aborted).toBe(false);

    const finish = drain.finish();
    expect(context.remainingMs()).toBe(5_000);
    await clock.advance(2_000);
    expect(context.remainingMs()).toBe(3_000);

    gate.resolve();
    await expect(job).resolves.toEqual({ status: 'done', value: 'uploaded' });
    await expect(finish).resolves.toEqual({ timedOut: false, abandoned: 0 });
  });

  it('falls back to the 120 s default deadline for an invalid option', async () => {
    for (const deadlineMs of [0, -5, Number.NaN, Number.POSITIVE_INFINITY]) {
      const clock = createFakeClock();
      const drain = new UploadDrain({ timers: clock.timers, now: () => clock.now(), deadlineMs });
      const hung = deferred<void>();
      void drain.submit(async () => {
        await hung.promise;
        return 'late';
      });

      const finish = drain.finish();
      expect(clock.setDelays).toEqual([120_000]);

      await clock.advance(DEFAULT_DRAIN_DEADLINE_MS - 1);
      let finished = false;
      void finish.then(() => {
        finished = true;
      });
      await flush();
      expect(finished).toBe(false);

      await clock.advance(1);
      await expect(finish).resolves.toEqual({ timedOut: true, abandoned: 1 });
    }
  });

  it('unrefs the deadline timer so it never keeps the process alive', async () => {
    let unrefs = 0;
    const clock = createFakeClock(() => {
      unrefs += 1;
    });
    const drain = new UploadDrain({ timers: clock.timers, now: () => clock.now() });
    const hung = deferred<void>();
    void drain.submit(async () => {
      await hung.promise;
      return 'late';
    });

    const finish = drain.finish();
    expect(unrefs).toBe(1);

    await clock.advance(DEFAULT_DRAIN_DEADLINE_MS);
    await expect(finish).resolves.toEqual({ timedOut: true, abandoned: 1 });
  });

  it('accepts a timer handle without unref', async () => {
    const timers: DrainTimers = { set: () => 42, clear: () => undefined };
    const drain = new UploadDrain({ timers, now: () => 0 });

    void drain.submit(() => Promise.resolve('saved'));
    await expect(drain.finish()).resolves.toEqual({ timedOut: false, abandoned: 0 });
  });
});
