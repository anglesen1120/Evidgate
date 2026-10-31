// The bounded upload drain behind `onTestEnd`/`onEnd`.
//
// Why a drain at all: events spool immediately on the event hot path, and artifact uploads run
// off it; a run may only be held open for them so long (`onEnd` drain deadline, default 120 s,
// configurable at the reporter seam). Playwright's `onEnd` is the last chance to wait, so the
// reporter needs one object that (a) keeps the number of in-flight uploads small, (b) never
// loses a test result because an upload failed, and (c) can be cut off at the deadline without
// leaving an unhandled rejection behind.
//
// Three properties this module fixes, each asserted by `drain.test.ts`:
// - bounded concurrency: at most two uploads in flight, whatever the caller passes (the
//   cap is clamped into [1, 2], so a misconfigured option cannot open a hundred sockets);
// - FIFO START order: a later attachment's job is never started before an earlier one's, so
//   the trace the reporter queued first is not left unstarted while a screenshot runs. The
//   requests themselves may still interleave — a job's own first awaits (the path guard, the
//   uploader load) decide when its PUT goes out — so this module promises start order only;
// - the deadline stops the WAIT, it does not cancel work in flight: at `deadlineMs` the
//   shared `AbortSignal` aborts, every running AND queued job resolves `{status:'deadline'}`,
//   a queued job is never started, and a job whose real promise settles later is ignored (its
//   rejection is swallowed). A job that already made its request is ABANDONED at the next
//   checkpoint that reads the signal, not cancelled mid-request. A late rejection escaping
//   here would fail an otherwise green Playwright run.
//
// A leaf module on purpose: it imports no reporter file and no `@evidgate/cli` type, so it
// can bound `uploadFile()` and nothing else. `timers`/`now` are injected seams, so the
// spec advances a virtual clock instead of waiting out a real 120 s deadline.
import { clearTimeout, setTimeout } from 'node:timers';

/**
 * Default upload concurrency: two in flight. The reporter runs inside the test process, so it
 * must not starve the tests.
 */
export const DEFAULT_UPLOAD_CONCURRENCY = 2;

/** Hard ceiling on concurrency: clamped rather than trusted, so a bad option opens no more. */
export const MAX_UPLOAD_CONCURRENCY = 2;

/** The `onEnd` drain deadline: 120 s, configurable at the seam. */
export const DEFAULT_DRAIN_DEADLINE_MS = 120_000;

/**
 * How one submitted job ended. A failed upload is an outcome, never a rejection: the
 * reporter turns `failed`/`deadline` into a `run.log` warning and keeps the test result.
 */
export type JobOutcome<T> =
  { status: 'done'; value: T } | { status: 'failed'; error: unknown } | { status: 'deadline' };

/** What a job may read: the deadline signal and the time it has left. */
export interface DrainJobContext {
  /**
   * Aborted at the drain deadline. Read it at the job's own checkpoints: the deadline stops
   * the wait, so a request already in flight is abandoned at the next checkpoint, never
   * cancelled mid-request.
   */
  readonly signal: AbortSignal;
  /** Milliseconds until the deadline; `Infinity` before `finish()` was called. */
  remainingMs(): number;
}

/** The timer seam: a spec injects a virtual clock and never waits. */
export interface DrainTimers {
  set(milliseconds: number, fn: () => void): unknown;
  clear(handle: unknown): void;
}

/** The drain's options; every field has a default, so `{}` is the production shape. */
export interface UploadDrainOptions {
  /** Requested parallelism; clamped into [1, {@link MAX_UPLOAD_CONCURRENCY}], default 2. */
  concurrency?: number;
  /** Whole-drain deadline from `finish()`, default {@link DEFAULT_DRAIN_DEADLINE_MS}. */
  deadlineMs?: number;
  /** Timer seam; defaults to Node's `setTimeout`/`clearTimeout`. */
  timers?: DrainTimers;
  /** Clock seam; defaults to `Date.now`. */
  now?: () => number;
}

/** The result of {@link UploadDrain.finish}. */
export interface DrainFinishResult {
  /** True when the deadline fired before every job settled. */
  timedOut: boolean;
  /** Jobs (running and queued) that the deadline had to abandon. */
  abandoned: number;
}

/** One submitted job, erased to `unknown` so the queue stays homogeneous. */
interface Job {
  started: boolean;
  settled: boolean;
  run: (context: DrainJobContext) => Promise<unknown>;
  resolve: (outcome: JobOutcome<unknown>) => void;
}

const defaultTimers: DrainTimers = {
  set: (milliseconds, fn) => setTimeout(fn, milliseconds),
  clear: (handle) => {
    if (handle === undefined || handle === null) {
      return;
    }
    // The only object handle this module creates is Node's `Timeout` (`set` above); a
    // number/string is accepted by `clearTimeout` as well.
    const timeout = handle as NodeJS.Timeout;
    clearTimeout(timeout);
  },
};

/**
 * A timer handle that can detach itself from the event loop (Node's `Timeout`, and any fake
 * handle a spec passes in). A plain number has no `unref`, and that is fine.
 */
function isUnrefable(handle: unknown): handle is { unref(): void } {
  if (typeof handle !== 'object' || handle === null || !('unref' in handle)) {
    return false;
  }
  return typeof handle.unref === 'function';
}

/**
 * Calls `unref()` when the handle has one, so a pending deadline never holds the process
 * open: the reporter runs inside Playwright's process, which must be free to exit.
 */
function unrefTimer(handle: unknown): void {
  if (isUnrefable(handle)) {
    handle.unref();
  }
}

function clampConcurrency(value: number | undefined): number {
  if (value === undefined || !Number.isFinite(value)) {
    return DEFAULT_UPLOAD_CONCURRENCY;
  }
  const floored = Math.floor(value);
  if (floored < 1) {
    return 1;
  }
  return Math.min(floored, MAX_UPLOAD_CONCURRENCY);
}

function clampDeadline(value: number | undefined): number {
  if (value === undefined || !Number.isFinite(value) || value <= 0) {
    return DEFAULT_DRAIN_DEADLINE_MS;
  }
  return value;
}

/**
 * A bounded-concurrency upload queue with FIFO START order and one shared deadline. Jobs start
 * in submission order; their requests may interleave.
 *
 * Lifecycle: `submit()` any number of times, then `finish()` once, near `onEnd`. Every
 * submitted job resolves to a {@link JobOutcome}; nothing here ever rejects.
 */
export class UploadDrain {
  private readonly concurrency: number;
  private readonly deadlineMs: number;
  private readonly timers: DrainTimers;
  private readonly now: () => number;
  private readonly controller: AbortController = new AbortController();
  private readonly queue: Job[] = [];
  private readonly live: Set<Job> = new Set<Job>();
  private running = 0;
  private unsettled = 0;
  private finishPromise: Promise<DrainFinishResult> | null = null;
  private resolveFinish: ((result: DrainFinishResult) => void) | null = null;
  private finishResolved = false;
  private deadlineAt: number | null = null;
  private timerHandle: unknown = null;
  private timerArmed = false;
  private deadlineFired = false;

  constructor(options: UploadDrainOptions = {}) {
    this.concurrency = clampConcurrency(options.concurrency);
    this.deadlineMs = clampDeadline(options.deadlineMs);
    this.timers = options.timers ?? defaultTimers;
    this.now = options.now ?? Date.now;
  }

  /** Jobs submitted but not settled yet (running + queued). */
  get pending(): number {
    return this.unsettled;
  }

  /**
   * Queues one job and resolves with its outcome; the returned promise never rejects. A
   * job submitted after the deadline fired (or after `finish()` already resolved) is not
   * called at all and answers `{status:'deadline'}`.
   */
  submit<T>(job: (context: DrainJobContext) => Promise<T>): Promise<JobOutcome<T>> {
    if (this.finishResolved || this.deadlineFired) {
      return Promise.resolve<JobOutcome<T>>({ status: 'deadline' });
    }
    // `Promise.withResolvers` is ES2024 and the workspace targets ES2022 (tsconfig.base), so
    // the executor is how the settle handle escapes to the queue record.
    let resolve: (outcome: JobOutcome<T>) => void = () => undefined;
    const promise = new Promise<JobOutcome<T>>((settle) => {
      resolve = settle;
    });
    const record: Job = {
      started: false,
      settled: false,
      run: job,
      resolve: resolve as (outcome: JobOutcome<unknown>) => void,
    };
    this.queue.push(record);
    this.live.add(record);
    this.unsettled += 1;
    this.pump();
    return promise;
  }

  /**
   * Starts the deadline clock and resolves when every submitted job settled, or at the
   * deadline. Idempotent: the second call returns the first promise.
   */
  finish(): Promise<DrainFinishResult> {
    if (this.finishPromise !== null) {
      return this.finishPromise;
    }
    const promise = new Promise<DrainFinishResult>((resolve) => {
      this.resolveFinish = resolve;
    });
    this.finishPromise = promise;
    this.deadlineAt = this.now() + this.deadlineMs;
    this.armTimer();
    if (this.unsettled === 0) {
      this.clearTimer();
      this.finishResolved = true;
      this.settleFinish({ timedOut: false, abandoned: 0 });
    }
    return promise;
  }

  private pump(): void {
    while (!this.deadlineFired && this.running < this.concurrency && this.queue.length > 0) {
      const record = this.queue.shift();
      if (record === undefined) {
        return;
      }
      record.started = true;
      this.running += 1;
      this.start(record);
    }
  }

  private start(record: Job): void {
    const context: DrainJobContext = {
      signal: this.controller.signal,
      remainingMs: () => this.remainingMs(),
    };
    try {
      const result = record.run(context);
      // The rejection handler is attached in the same turn the job starts, so a job the
      // deadline abandons can never surface as an unhandled rejection.
      void Promise.resolve(result).then(
        (value) => {
          this.settle(record, { status: 'done', value });
        },
        (error: unknown) => {
          this.settle(record, { status: 'failed', error });
        },
      );
    } catch (error) {
      // A job that throws before returning a promise is a failed outcome, not a drain failure.
      this.settle(record, { status: 'failed', error });
    }
  }

  /** Settles a job at most once; a job the deadline already abandoned is ignored here. */
  private settle(record: Job, outcome: JobOutcome<unknown>): void {
    if (record.settled) {
      return;
    }
    record.settled = true;
    this.live.delete(record);
    this.unsettled -= 1;
    if (record.started) {
      this.running -= 1;
    }
    record.resolve(outcome);
    if (!this.deadlineFired) {
      this.settleFinishIfDrained();
      this.pump();
    }
  }

  private settleFinishIfDrained(): void {
    if (this.finishPromise === null || this.finishResolved || this.unsettled > 0) {
      return;
    }
    this.clearTimer();
    this.finishResolved = true;
    this.settleFinish({ timedOut: false, abandoned: 0 });
  }

  private settleFinish(result: DrainFinishResult): void {
    const resolve = this.resolveFinish;
    this.resolveFinish = null;
    resolve?.(result);
  }

  private armTimer(): void {
    const handle = this.timers.set(this.deadlineMs, () => {
      this.onDeadline();
    });
    this.timerHandle = handle;
    this.timerArmed = true;
    unrefTimer(handle);
  }

  private clearTimer(): void {
    if (!this.timerArmed) {
      return;
    }
    const handle = this.timerHandle;
    this.timerHandle = null;
    this.timerArmed = false;
    this.timers.clear(handle);
  }

  /**
   * The deadline: abort the shared signal — which stops the WAIT, it cannot cancel a request a
   * job already started — answer every unsettled job `{status:'deadline'}` (queued ones are
   * never started) and resolve `finish()` as timed out.
   */
  private onDeadline(): void {
    this.timerArmed = false;
    this.timerHandle = null;
    if (this.deadlineFired || this.finishResolved) {
      return;
    }
    this.deadlineFired = true;
    this.controller.abort();
    const abandoned = this.unsettled;
    this.queue.length = 0;
    for (const record of this.live) {
      if (record.settled) {
        continue;
      }
      record.settled = true;
      record.resolve({ status: 'deadline' });
    }
    this.live.clear();
    this.unsettled = 0;
    this.running = 0;
    this.finishResolved = true;
    this.settleFinish({ timedOut: true, abandoned });
  }

  private remainingMs(): number {
    if (this.deadlineAt === null) {
      return Number.POSITIVE_INFINITY;
    }
    return Math.max(0, this.deadlineAt - this.now());
  }
}
