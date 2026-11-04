// The Policy tab's two project-level reads: the activation in force and the project's head
// revision. They run beside the revision list, never before it — neither answer can fail the page,
// and a failed one must not hide the rows the list already served.
//
// Four rules shape this store:
//
// 1. Each read has its own generation, bumped by every start and clear. A list refresh bumps the
//    store's own counter, so sharing it would strand a read in `loading` forever.
// 2. `404` is absence, not a failure: no activation and no revision are real states of a project.
// 3. `403` is its own state. The user may read the tab but not this fact, which is not an error to
//    retry; every other refusal is an error with the server's own message.
// 4. Only a served activation is a value. Absence and refusal clear the activation the mode chip
//    renders, so a stale mode can never outlive the project it belonged to.

import { inject, Injectable, signal } from '@angular/core';
import type { PolicyActivationStateDto, PolicyRevisionSummaryDto } from '@evidgate/sdk';

import { PolicyDataService } from '../../../core/data/policy-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { settledRead } from '../../../core/http/settled-read';

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
  /** Bumped by every start and clear: an answer of an older generation writes nothing. */
  private generation = 0;

  readonly activation = this.activationSignal.asReadonly();
  readonly head = this.headSignal.asReadonly();

  /**
   * Reads the project's activation and head revision. `applyActivation` receives the served
   * activation, or `null` for every other outcome — the mode chip is the only consumer of the
   * value, and it renders "unavailable" rather than a mode the server never sent.
   */
  start(
    projectId: string,
    applyActivation: (activation: PolicyActivationStateDto | null) => void,
  ): void {
    const generation = ++this.generation;
    this.activationSignal.set(nothing('loading'));
    this.headSignal.set(nothing('loading'));
    applyActivation(null);
    void this.readActivation(projectId, generation, applyActivation);
    void this.readHead(projectId, generation);
  }

  /** Abandons both reads and returns to `idle`; an answer still on its way writes nothing. */
  clear(): void {
    this.generation += 1;
    this.activationSignal.set(nothing('idle'));
    this.headSignal.set(nothing('idle'));
  }

  /** Never rejects: the read's own outcome is the state, and the caller does not await it. */
  private async readActivation(
    projectId: string,
    generation: number,
    applyActivation: (activation: PolicyActivationStateDto | null) => void,
  ): Promise<void> {
    const read = await settledRead(() => this.data.getActivation(projectId));
    if (generation !== this.generation) {
      return;
    }
    const view: PolicyReadView<PolicyActivationStateDto> = read.ok
      ? { phase: 'ready', value: read.value, error: null }
      : refusal<PolicyActivationStateDto>(read.error);
    this.activationSignal.set(view);
    applyActivation(view.value);
  }

  /** Never rejects: same contract as {@link readActivation}. */
  private async readHead(projectId: string, generation: number): Promise<void> {
    const read = await settledRead(() => this.data.getHead(projectId));
    if (generation !== this.generation) {
      return;
    }
    const view: PolicyReadView<PolicyRevisionSummaryDto> = read.ok
      ? { phase: 'ready', value: read.value.revision, error: null }
      : refusal<PolicyRevisionSummaryDto>(read.error);
    this.headSignal.set(view);
  }
}
