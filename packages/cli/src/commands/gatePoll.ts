// The `gate --wait` loop: ask again with a jittered backoff until the run decides or the one
// deadline is spent, never sleeping past it. It knows nothing about HTTP — the caller turns
// one request into a step — so the deadline arithmetic is the only thing under test here.
import { setTimeout as delay } from 'node:timers/promises';

import { maxRetryAfterMs } from '../callApi.js';
import type { CliError } from '../errors.js';
import { backoffDelay } from '../sender.js';

import type { GateOutcome } from './gateDecision.js';

/** The shortest pause between two polls, so a fast responder cannot spin the loop. */
const pollFloorMs = 1_000;
/** The longest jittered backoff; a server-named wait may exceed it, the deadline may not. */
const pollCapMs = 10_000;

/**
 * One poll's result: a parsed body (pending or decided), or a failure the caller classified
 * as worth another attempt. A fatal failure is thrown by `next` instead, never returned.
 */
export type PollStep =
  | { readonly kind: 'body'; readonly outcome: GateOutcome }
  | {
      readonly kind: 'transient';
      readonly error: CliError;
      readonly retryAfterMs?: number;
      /**
       * The wait's own deadline ended this attempt before the run answered it. Only such a
       * cut may leave an earlier pending answer standing; any other final failure is the
       * infrastructure error the wait ended on.
       */
      readonly endedByDeadline?: boolean;
    };

/** The seams a spec injects so polling needs no real time: no clock, no sleep, no jitter. */
export interface PollDeps {
  readonly sleep?: (milliseconds: number) => Promise<void>;
  readonly random?: () => number;
  readonly now?: () => number;
}

export interface PollOptions {
  /** How long the whole poll may take; `0` means one request and no waiting. */
  readonly waitMs: number;
  readonly deps: PollDeps;
  /**
   * One request. `deadlineAt` is the absolute end of the wait, not a relative budget, so a
   * caller that must re-authenticate can bound the replay by the same instant instead of
   * granting it a fresh window; `undefined` (no `--wait`) leaves the request its own default.
   */
  readonly next: (deadlineAt: number | undefined) => Promise<PollStep>;
}

/**
 * Polls until a body carries a decision, the wait is spent, or the last answer was transient.
 * A spent wait on a pending body returns that body (exit code 2). A spent wait on a transient
 * failure throws it (exit code 3), even when an earlier poll reported the run pending — that
 * pending answer only stands when the deadline itself cut the last request off before it
 * answered, because then the run gave no later state to report.
 */
export async function pollDecision(options: PollOptions): Promise<GateOutcome> {
  const now = options.deps.now ?? Date.now;
  const sleep = options.deps.sleep ?? ((milliseconds: number) => delay(milliseconds));
  const random = options.deps.random ?? Math.random;
  const deadlineAt = now() + options.waitMs;
  const pollDeadline = options.waitMs <= 0 ? undefined : deadlineAt;
  let sleptMs = 0;
  let attempt = 1;
  let lastPending: GateOutcome | undefined;
  let step = await options.next(pollDeadline);
  for (;;) {
    if (step.kind === 'body' && step.outcome.decision !== null) {
      return step.outcome;
    }
    if (step.kind === 'body') {
      lastPending = step.outcome;
    }
    if (options.waitMs <= 0) {
      break;
    }
    const left = Math.min(deadlineAt - now(), options.waitMs - sleptMs);
    if (left <= 0) {
      break;
    }
    const pause = Math.min(pauseMs(attempt, step, random()), left);
    await sleep(pause);
    sleptMs += pause;
    attempt += 1;
    // The request is made even when the pause was clamped to what was left: a decision that
    // landed in the last second still counts, and the request gets the minimum budget for it.
    step = await options.next(pollDeadline);
    if (pause >= left) {
      break;
    }
  }
  if (step.kind === 'transient') {
    if (lastPending !== undefined && step.endedByDeadline === true) {
      return lastPending;
    }
    throw step.error;
  }
  return step.outcome;
}

/** The jittered backoff, floored so the loop always sleeps, capped so it stays responsive. */
function pauseMs(attempt: number, step: PollStep, sample: number): number {
  const backoff = Math.min(Math.max(backoffDelay(attempt, sample), pollFloorMs), pollCapMs);
  // A server-named wait is not bounded by the poll cap: a `429` asking for 30 s must be
  // obeyed for 30 s, and only the cap `callApi` already applies lowers it. The caller clamps
  // the result to the deadline, so a long header still cannot overshoot.
  if (step.kind !== 'transient' || step.retryAfterMs === undefined) {
    return backoff;
  }
  return Math.max(backoff, Math.min(step.retryAfterMs, maxRetryAfterMs));
}
