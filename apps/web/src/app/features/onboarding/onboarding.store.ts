// 09.2.1-T1.1 (plan 09.2.1 §6, §10 T1.1; decisions (d) and (f)): the wizard's view state as a
// SignalStore. It holds no progress of its own — `OnboardingProgressService` derives that from the
// server on every `load` (T63) — and it holds no secret.
//
// Five behaviours the shell depends on:
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
// 4. A published derivation is tagged with the route scope it was read for, so `holds` can answer
//    "is this already the state of the page the user is on?". The shell consults it before
//    re-reading: the create's own `projectCreated` load is the single read after a creation
//    (S71 F1), while a fresh page load finds an empty store and always loads.
// 5. `focusToken` counts the step moves the *user* caused (`next`, `back`, `selectStep`, and the
//    step a create opens). The shell moves focus to the step heading on a new token only, so the
//    first ready render of a page load and a Retry leave focus alone (S71 F2).

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

/** The route scope a derivation is read for: the org, and the project address if the URL had one. */
export interface OnboardingScope {
  readonly orgId: string;
  readonly projectId: string | null;
}

/** The store's state. Mutable by design: `patchState` replaces members, never mutates them. */
export interface OnboardingViewState {
  status: OnboardingStatus;
  failure: OnboardingFailure;
  /** The last published derivation; `null` before the first successful load and after a failure. */
  progress: OnboardingProgress | null;
  /** The step whose panel is rendered; gated by `currentStep()`. */
  viewStep: StepNumber;
  /** The route scope `progress` was read for; `null` until a load publishes one. */
  scope: OnboardingScope | null;
  /** Counts the step moves a user caused; the shell's cue to move focus (see the header). */
  focusToken: number;
}

function initialState(): OnboardingViewState {
  return {
    status: 'loading',
    failure: null,
    progress: null,
    viewStep: 1,
    scope: null,
    focusToken: 0,
  };
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
          scope: { orgId, projectId },
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
          scope: null,
        });
      }
    }

    /** Publish a step the user moved, with the focus cue the shell reads (see the header, 5). */
    function showStep(step: StepNumber): void {
      if (step === store.viewStep()) {
        return;
      }
      patchState(store, { viewStep: step, focusToken: store.focusToken() + 1 });
    }

    return {
      /**
       * Resumes the wizard for `orgId` and the route's optional `projectId`: publishes the
       * derivation and opens the first step that is not done. Never rejects (see the header).
       */
      load,

      /**
       * True when the published derivation already describes exactly this route scope. The shell
       * asks before re-reading, so the load a create already ran is not issued a second time when
       * the URL it caused re-enters the shell (S71 F1). Only a `ready` store holds anything: an
       * error holds no derivation, and a load in flight has not published this scope yet.
       */
      holds(orgId: string, projectId: string | null): boolean {
        const scope = store.scope();
        return (
          store.status() === 'ready' &&
          store.progress() !== null &&
          scope !== null &&
          scope.orgId === orgId &&
          scope.projectId === projectId
        );
      },

      /** Opens a step the server has already reached; a not-yet-reached step is ignored. */
      selectStep(step: StepNumber): void {
        const current = store.currentStep();
        if (current !== 'enabled' && step > current) {
          return;
        }
        showStep(step);
      },

      /** Moves to the next step when the viewed one is done; a no-op otherwise. */
      next(): void {
        if (!store.canGoNext()) {
          return;
        }
        const following = STEP_NUMBERS[STEP_NUMBERS.indexOf(store.viewStep()) + 1];
        if (following !== undefined) {
          showStep(following);
        }
      },

      /** Moves to the previous step; a no-op on the first one. */
      back(): void {
        if (!store.canGoBack()) {
          return;
        }
        const previous = STEP_NUMBERS[STEP_NUMBERS.indexOf(store.viewStep()) - 1];
        if (previous !== undefined) {
          showStep(previous);
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
        if (store.status() === 'ready') {
          // The create opened a step on the user's behalf, so the shell moves focus there even
          // though the component the URL change activates is constructed after this patch.
          patchState(store, { focusToken: store.focusToken() + 1 });
        }
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
