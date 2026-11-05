// The Policy tab's two project-level reads: the activation in force and the project's head
// revision. They run beside the revision list, never before it — neither answer can fail the page,
// and a failed one must not hide the rows the list already served.
//
// Four rules shape this store:
//
// 1. Each read has its own generation, bumped by every start, clear and head refresh. A head
//    re-read after a publish must discard the head answer still on its way without stranding a
//    pending activation read, and a list refresh has its own counter in the store.
// 2. `404` is absence, not a failure: no activation and no revision are real states of a project.
// 3. `403` is its own state. The user may read the tab but not this fact, which is not an error to
//    retry; every other refusal is an error with the server's own message.
// 4. Only a served activation is a value. Absence and refusal clear the activation the mode chip
//    renders, so a stale mode can never outlive the project it belonged to.
//
// The explicit retry is the only way either read runs again: it is inert while a read is in
// flight, and a served `Retry-After` keeps it closed until that interval ends. A later failure
// never cancels or shortens a wait already running, and one timer carries it, starting no read.

import { computed, inject, Injectable, signal } from '@angular/core';
import type { PolicyActivationStateDto, PolicyRevisionSummaryDto } from '@evidgate/sdk';

import { PolicyDataService } from '../../../core/data/policy-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { settledRead } from '../../../core/http/settled-read';

/** Largest delay a browser or Node timer accepts; a longer server interval is clamped to it. */
const MAX_TIMER_DELAY_MS = 2 ** 31 - 1;

/** What one project-level read settled to. `empty` is an absent object, which is not a failure. */
export type PolicyReadPhase = 'idle' | 'loading' | 'ready' | 'empty' | 'forbidden' | 'error';

/** One read's state: the phase the summary renders, the served value, and the typed refusal. */
export interface PolicyReadView<T> {
  readonly phase: PolicyReadPhase;
  /** The served object; only a `ready` view carries one. */
  readonly value: T | null;
  /** The server's refusal for `forbidden`/`error`; `null` for every other phase, and for a
   * rejection that was not the server's answer at all. */
  readonly error: ApiError | null;
}

function nothing<T>(phase: PolicyReadPhase): PolicyReadView<T> {
  return { phase, value: null, error: null };
}

/** The state a refusal settles to: `not_found` is absence, `forbidden` is its own state, the
 * rest is an error, and a rejection that never reached the server is an error without copy. */
function refusal<T>(error: unknown): PolicyReadView<T> {
  if (!isApiRequestError(error)) {
    return nothing('error');
  }
  switch (error.error.kind) {
    case 'not_found':
      return nothing('empty');
    case 'forbidden':
      return { phase: 'forbidden', value: null, error: error.error };
    default:
      return { phase: 'error', value: null, error: error.error };
  }
}

/**
 * The activation and head reads of one project, owned by the page that asked. Both start together
 * and settle independently, so a slow or refused activation never delays the head or the list.
 */
@Injectable()
export class PolicyProjectReads {
  private readonly data = inject(PolicyDataService);

  private readonly activationSignal = signal<PolicyReadView<PolicyActivationStateDto>>(
    nothing('idle'),
  );
  private readonly headSignal = signal<PolicyReadView<PolicyRevisionSummaryDto>>(nothing('idle'));
  /**
   * One generation per read, bumped by every start, clear and head refresh: a head re-read must
   * discard the head answer still on its way without stranding a pending activation read.
   */
  private activationGeneration = 0;
  private headGeneration = 0;
  /** The project both reads were last started for; `null` while the view is closed. */
  private project: string | null = null;
  /** The store's activation seam, as handed over by {@link start}. */
  private applyActivation: (activation: PolicyActivationStateDto | null) => void = () => undefined;

  /** Epoch ms at which a server `Retry-After` wait ends; `null` when a retry may run now. */
  private readonly waitDeadline = signal<number | null>(null);
  private readonly retryAfterState = signal<number | null>(null);
  /** Cancels the pending wait; `null` when no timer is armed. */
  private cancelWait: (() => void) | null = null;

  readonly activation = this.activationSignal.asReadonly();
  readonly head = this.headSignal.asReadonly();
  /** True while either read is in flight: every retry control is inert then. */
  readonly busy = computed(
    () => this.activationSignal().phase === 'loading' || this.headSignal().phase === 'loading',
  );
  /**
   * The `Retry-After` that set the current deadline, in whole seconds; a later shorter interval
   * leaves it unchanged. `null` until a failure serves one; start, retry and clear reset it.
   */
  readonly retryAfterSeconds = this.retryAfterState.asReadonly();
  /** True until a server `Retry-After` wait is over; nothing retries on its own while it runs. */
  readonly waiting = computed(() => {
    const deadline = this.waitDeadline();
    return deadline !== null && deadline > Date.now();
  });

  /**
   * Reads the project's activation and head revision. `applyActivation` receives the served
   * activation, or `null` for every other outcome — the mode chip is the only consumer of the
   * value, and it renders "unavailable" rather than a mode the server never sent.
   */
  start(
    projectId: string,
    applyActivation: (activation: PolicyActivationStateDto | null) => void,
  ): void {
    this.project = projectId;
    this.applyActivation = applyActivation;
    this.clearWait();
    this.runBoth();
  }

  /**
   * Re-reads the head revision of the project the view was opened for, and nothing else. The
   * publish path calls it after an answer: the head it just wrote is the one the summary must
   * show, and the head answer still on its way is obsolete. A publish that answered for another
   * project changes nothing here. Never rejects, never awaited.
   */
  refreshHead(projectId: string): void {
    if (this.project !== projectId) {
      return;
    }
    const generation = ++this.headGeneration;
    this.headSignal.set(nothing('loading'));
    void this.readHead(projectId, generation);
  }

  /**
   * The explicit retry of both reads: a no-op while a read is in flight or a `Retry-After` wait
   * is still running, so a rate-limited read is never hammered. Nothing here retries on its own.
   */
  retry(): void {
    if (this.busy() || this.waiting() || this.project === null) {
      return;
    }
    this.clearWait();
    this.runBoth();
  }

  /** Abandons both reads and returns to `idle`; an answer still on its way writes nothing. */
  clear(): void {
    this.activationGeneration += 1;
    this.headGeneration += 1;
    this.project = null;
    this.clearWait();
    this.activationSignal.set(nothing('idle'));
    this.headSignal.set(nothing('idle'));
  }

  /** Starts both reads of the captured project, each with its own generation. */
  private runBoth(): void {
    const project = this.project;
    if (project === null) {
      return;
    }
    const activationGeneration = ++this.activationGeneration;
    const headGeneration = ++this.headGeneration;
    this.activationSignal.set(nothing('loading'));
    this.headSignal.set(nothing('loading'));
    this.applyActivation(null);
    void this.readActivation(project, activationGeneration, this.applyActivation);
    void this.readHead(project, headGeneration);
  }

  /**
   * One timer for the failure's `Retry-After`, never a poll and never a retry: it only reopens
   * the control when the server's interval has passed. The two reads fail independently, so a
   * failure never cancels a wait the other one armed: an answer without a usable interval leaves
   * the wait alone, and a shorter interval never shortens a longer one already in force.
   *
   * Returns whether it armed or extended the wait.
   */
  private noteRetryAfter(error: ApiError | null): boolean {
    const seconds = error?.retryAfterSeconds ?? null;
    if (error === null || seconds === null || !Number.isFinite(seconds) || seconds <= 0) {
      return false;
    }
    // A delay above the timer maximum fires immediately in browsers and warns in Node, which
    // would turn a hostile interval into no wait at all; clamp it instead.
    const delayMs = Math.min(seconds * 1000, MAX_TIMER_DELAY_MS);
    const deadline = Date.now() + delayMs;
    const current = this.waitDeadline();
    if (current !== null && current >= deadline) {
      return false;
    }
    // Replacing the timer is what makes an extension safe: the earlier one would end the wait at
    // its own, shorter deadline.
    this.cancelWait?.();
    this.retryAfterState.set(seconds);
    this.waitDeadline.set(deadline);
    const timer = setTimeout(() => {
      this.cancelWait = null;
      this.waitDeadline.set(null);
    }, delayMs);
    this.cancelWait = () => clearTimeout(timer);
    return true;
  }

  private clearWait(): void {
    this.cancelWait?.();
    this.cancelWait = null;
    this.waitDeadline.set(null);
    this.retryAfterState.set(null);
  }

  /** Never rejects: the read's own outcome is the state, and the caller does not await it. */
  private async readActivation(
    projectId: string,
    generation: number,
    applyActivation: (activation: PolicyActivationStateDto | null) => void,
  ): Promise<void> {
    const read = await settledRead(() => this.data.getActivation(projectId));
    if (generation !== this.activationGeneration) {
      return;
    }
    const view: PolicyReadView<PolicyActivationStateDto> = read.ok
      ? { phase: 'ready', value: read.value, error: null }
      : refusal<PolicyActivationStateDto>(read.error);
    this.activationSignal.set(view);
    if (view.phase === 'error') {
      this.noteRetryAfter(view.error);
    }
    applyActivation(view.value);
  }

  /** Never rejects: same contract as {@link readActivation}. */
  private async readHead(projectId: string, generation: number): Promise<void> {
    const read = await settledRead(() => this.data.getHead(projectId));
    if (generation !== this.headGeneration) {
      return;
    }
    const view: PolicyReadView<PolicyRevisionSummaryDto> = read.ok
      ? { phase: 'ready', value: read.value.revision, error: null }
      : refusal<PolicyRevisionSummaryDto>(read.error);
    this.headSignal.set(view);
    if (view.phase === 'error') {
      this.noteRetryAfter(view.error);
    }
  }
}
