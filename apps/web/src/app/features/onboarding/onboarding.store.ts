// 09.2.1-T1.1 (plan 09.2.1 §6, §10 T1.1; decisions (d) and (f)): the wizard's view state as a
// SignalStore. It holds no progress of its own — `OnboardingProgressService` derives that from the
// server on every `load` (T63) — and it holds no secret.
//
// Three behaviours the shell depends on:
//
// 1. `load` never throws. Every failure becomes `status: 'error'` plus one of the fixed
//    `failure` values the shell renders as the T8 patterns (forbidden / not-found / error), so a
//    page that refreshed after a successful create is not pushed back into a retry.
// 2. A superseded load never publishes. Two `load`s can overlap (a route change while a read is
//    still in flight); the newer one increments the sequence, and the older answer — success or
//    failure — is dropped, because it describes a project nobody is looking at any more.
// 3. The viewed step is gated by the derived one: `selectStep` cannot open a step the server has
//    not reached, and `next` cannot pass a step whose done-condition is unmet (T1.3's disabled
//    `onboarding-next`). Nothing here advances the wizard: only a new `load` moves it.

import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { ProjectDto } from '@evidgate/sdk';

import { isApiRequestError } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import {
  OnboardingProgressService,
  type OnboardingProgress,
} from './data/onboarding-progress.service';
import {
  NO_STEPS_DONE,
  STEP_NUMBERS,
  viewStepFor,
  type StepNumber,
  type StepsDone,
  type WizardStep,
} from './data/step-derivation';

/** Whether the resume read is in flight, published, or failed. */
export type OnboardingStatus = 'loading' | 'ready' | 'error';

/**
 * The failure the shell renders as a fixed pattern, or `null`. `'unavailable'` covers everything
 * that is not a refusal the server expressed about the resource — a transport failure, a 5xx, a
 * rate limit, a `401` (the shell owns re-authentication), and any programming error.
 */
export type OnboardingFailure = 'forbidden' | 'not-found' | 'unavailable' | null;

/** The store's state. Mutable by design: `patchState` replaces members, never mutates them. */
export interface OnboardingViewState {
  status: OnboardingStatus;
  failure: OnboardingFailure;
  /** The last published derivation; `null` before the first successful load and after a failure. */
  progress: OnboardingProgress | null;
  /** The step whose panel is rendered; gated by `currentStep()`. */
  viewStep: StepNumber;
}

function initialState(): OnboardingViewState {
  return { status: 'loading', failure: null, progress: null, viewStep: 1 };
}

export const OnboardingStore = signalStore(
  withState<OnboardingViewState>(initialState),
  withComputed(({ progress, viewStep }) => ({
    /** The first step the server says is not done, or `'enabled'`; `1` until a load answers. */
    currentStep: computed<WizardStep>(() => progress()?.currentStep ?? 1),
    done: computed<StepsDone>(() => progress()?.done ?? NO_STEPS_DONE),
    /** The project the wizard resumes on; `null` only for an org with no project yet. */
    project: computed<ProjectDto | null>(() => progress()?.project ?? null),
    /** True when the viewed step's done-condition holds and there is a step after it. */
    canGoNext: computed<boolean>(() => {
      const step = viewStep();
      return step < 5 && (progress()?.done[step] ?? false);
    }),
    canGoBack: computed<boolean>(() => viewStep() > 1),
  })),
  withMethods((store) => {
    const progressService = inject(OnboardingProgressService);
    const session = inject(SessionStore);
    /**
     * The sequence number of the newest `load`, per store instance (the route provides one store
     * per activation). An answer whose sequence is no longer the newest is dropped.
     */
    let newestLoad = 0;

    async function load(orgId: string, projectId: string | null): Promise<void> {
      newestLoad += 1;
      const loadSequence = newestLoad;
      patchState(store, { status: 'loading', failure: null });
      try {
        const progress = await progressService.load(orgId, projectId);
        if (loadSequence !== newestLoad) {
          return;
        }
        patchState(store, {
          status: 'ready',
          failure: null,
          progress,
          viewStep: viewStepFor(progress.currentStep),
        });
      } catch (error) {
        if (loadSequence !== newestLoad) {
          return;
        }
        // The previous derivation is dropped with the failure: keeping it would render the steps
        // of a project this load could not read, which is the wrong-step answer §6 forbids.
        patchState(store, {
          status: 'error',
          failure: failureFor(error),
          progress: null,
        });
      }
    }

    return {
      /**
       * Resumes the wizard for `orgId` and the route's optional `projectId`: publishes the
       * derivation and opens the first step that is not done. Never rejects (see the header).
       */
      load,

      /** Opens a step the server has already reached; a not-yet-reached step is ignored. */
      selectStep(step: StepNumber): void {
        const current = store.currentStep();
        if (current !== 'enabled' && step > current) {
          return;
        }
        patchState(store, { viewStep: step });
      },

      /** Moves to the next step when the viewed one is done; a no-op otherwise. */
      next(): void {
        if (!store.canGoNext()) {
          return;
        }
        const following = STEP_NUMBERS[STEP_NUMBERS.indexOf(store.viewStep()) + 1];
        if (following !== undefined) {
          patchState(store, { viewStep: following });
        }
      },

      /** Moves to the previous step; a no-op on the first one. */
      back(): void {
        if (!store.canGoBack()) {
          return;
        }
        const previous = STEP_NUMBERS[STEP_NUMBERS.indexOf(store.viewStep()) - 1];
        if (previous !== undefined) {
          patchState(store, { viewStep: previous });
        }
      },

      /**
       * Adopts the project step 1 just created — the shell's project cache must hold it, so no
       * navigation re-reads the list (T4 delta N2) — and reloads the derivation for it, which
       * resumes on step 2. A failed reload lands in `status: 'error'`; the successful create is
       * never retried because of a read that came after it.
       */
      async projectCreated(project: ProjectDto): Promise<void> {
        session.adoptCreatedProject(project);
        await load(project.org_id, project.id);
      },
    };
  }),
);

/**
 * One typed failure → the pattern the shell renders. Exactly the two refusals the server expresses
 * about the resource keep their meaning (00 §9.2 `patternForError`); everything else — including
 * `unauthenticated`, which the shell's own session handling owns — is `unavailable`.
 */
function failureFor(error: unknown): OnboardingFailure {
  if (!isApiRequestError(error)) {
    return 'unavailable';
  }
  const kind = error.error.kind;
  if (kind === 'forbidden') {
    return 'forbidden';
  }
  return kind === 'not_found' ? 'not-found' : 'unavailable';
}
