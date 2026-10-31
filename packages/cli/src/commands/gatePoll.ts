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
  | { readonly kind: 'transient'; readonly error: CliError; readonly retryAfterMs?: number };

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
   * One request. `budgetMs` bounds the retries that request may spend, so a single request
   * never outlives the deadline; `undefined` leaves the request its own default budget.
   */
  readonly next: (budgetMs: number | undefined) => Promise<PollStep>;
}

/**
 * Polls until a body carries a decision, the wait is spent, or the last answer was transient.
 * A spent wait on a pending body returns that body (exit code 2); a spent wait on a transient
 * failure throws it, so the caller reports the real failure (exit code 3) rather than a
 * pending verdict.
 */
export async function pollDecision(options: PollOptions): Promise<GateOutcome> {
  const now = options.deps.now ?? Date.now;
  const sleep = options.deps.sleep ?? ((milliseconds: number) => delay(milliseconds));
  const random = options.deps.random ?? Math.random;
  const deadlineAt = now() + options.waitMs;
  let sleptMs = 0;
  let attempt = 1;
  let step = await options.next(budget(options, deadlineAt, now(), sleptMs));
  for (;;) {
    if (step.kind === 'body' && step.outcome.decision !== null) {
      return step.outcome;
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
    // The last pause was clamped to what was left, so another request would start past the
    // deadline: report the state we have instead of overshooting it.
    if (pause >= left) {
      break;
    }
    attempt += 1;
    step = await options.next(budget(options, deadlineAt, now(), sleptMs));
  }
  if (step.kind === 'transient') {
    throw step.error;
  }
  return step.outcome;
}

/**
 * The retries one request may spend. Without `--wait` the request keeps its own budget (the
 * call's bounded retry); with it, the request gets only what is left, so nothing can outlive
 * the deadline.
 */
function budget(
  options: PollOptions,
  deadlineAt: number,
  now: number,
  sleptMs: number,
): number | undefined {
  if (options.waitMs <= 0) {
    return undefined;
  }
  return Math.max(Math.min(deadlineAt - now, options.waitMs - sleptMs), 1);
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
