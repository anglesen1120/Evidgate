// 09.2.1-T1.1/T2 (plan 09.2.1 §6, §10 T1.1/T2; decisions (d) and (f); AC2, AC7): the wizard's
// view state as a SignalStore. It holds no progress of its own — `OnboardingProgressService`
// derives that from the server on every `load` or `refresh` (T63) — and it holds no secret: step
// 2's one-time values live in the page's component-scoped `RevealSlot`, never here.
//
// Six behaviours the shell depends on:
//
// 1. `load` never throws. Every failure becomes `status: 'error'` plus one of the fixed
//    `failure` values the shell renders as the T8 patterns (forbidden / not-found / error), so a
//    page that refreshed after a successful create is not pushed back into a retry.
// 2. A superseded load never publishes. Two `load`s can overlap (a route change while a read is
//    still in flight); the newer one increments the sequence, and the older answer — success or
//    failure — is dropped, because it describes a project nobody is looking at any more.
// 3. The viewed step is gated by the derived one: `selectStep` cannot open a step the server has
//    not reached, and `next` cannot pass a step whose done-condition is unmet (T1.3's disabled
//    `onboarding-next`). Nothing here advances the wizard except the derivation: only a new `load`
//    moves it.
// 4. This store is a route-level singleton, not a per-activation one: the route's `providers`
//    keep one instance for the app's lifetime, so a state that is still `ready` says nothing
//    about the activation on screen. The only thing reusable across activations is the one-shot
//    create marker: `projectCreated` sets it after its own successful load, and `consumeCreated`
//    takes it exactly once, for exactly that scope. That spares the activation the create's URL
//    change causes a second fan-out (S71 F1), while every other activation — a revisit, a
//    different org or project, a Retry — loads and re-derives from the server (S71 N1).
//    `clearCreated` drops the marker for a create whose URL change never happened (N2).
// 5. `focusToken` counts the step moves the *user* caused (`next`, `back`, `selectStep`, and the
//    step a create opens). The shell moves focus to the step heading on a new token only, so the
//    first ready render of a page load and a Retry leave focus alone (S71 F2).
// 6. `refresh` re-derives without touching the view: it patches `progress` only, never sets
//    `status: 'loading'`, never changes `viewStep` or `focusToken`, and on failure keeps the
//    derivation it had while publishing `refreshFailure`. That is what step 2 needs after a
//    create: the page stays mounted — with the one-time secret it is revealing — while `done[2]`
//    flips and `canGoNext` enables Next (AC2). It owns its sequence, separate from `load`'s
//    (F4): a refresh never supersedes a load, so a refresh asked for while the resume is still in
//    flight cannot leave the store loading forever; a refresh drops its own answer when a newer
//    refresh started, or when a newer load did (that read describes a page nobody is on any
//    more). And a step move — `selectStep`, `next`, `back` — clears `refreshFailure`: the notice
//    belongs to the step the refresh failed on, so a step 2 that is re-entered after a failure
//    must not show a banner for it.

import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { CiBindingView, IngestTokenView, ProjectDto } from '@evidgate/sdk';

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

/** The route scope a create published: the one-shot marker `consumeCreated` takes. */
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
  /**
   * Why the last `refresh` failed, or `null`. Separate from `failure` because a refresh keeps the
   * page and its derivation: the panel renders this beside the rows it still shows instead of the
   * full-page pattern a failed `load` earns.
   */
  refreshFailure: OnboardingFailure;
  /** The step whose panel is rendered; gated by `currentStep()`. */
  viewStep: StepNumber;
  /** Counts the step moves a user caused; the shell's cue to move focus (see the header). */
  focusToken: number;
}

function initialState(): OnboardingViewState {
  return {
    status: 'loading',
    failure: null,
    progress: null,
    refreshFailure: null,
    viewStep: 1,
    focusToken: 0,
  };
}

/** The empty step 2 answer for a store that has no derivation yet — shared, and never mutated. */
const NO_BINDINGS: readonly CiBindingView[] = [];
const NO_TOKENS: readonly IngestTokenView[] = [];

export const OnboardingStore = signalStore(
  withState<OnboardingViewState>(initialState),
  withComputed(({ progress, viewStep }) => ({
    /** The first step the server says is not done, or `'enabled'`; `1` until a load answers. */
    currentStep: computed<WizardStep>(() => progress()?.currentStep ?? 1),
    done: computed<StepsDone>(() => progress()?.done ?? NO_STEPS_DONE),
    /** The project the wizard resumes on; `null` only for an org with no project yet. */
    project: computed<ProjectDto | null>(() => progress()?.project ?? null),
    /**
     * Step 2's rows, as the server answered them: binding metadata and token metadata, never a
     * secret. Empty before the first derivation and for a project that has neither.
     */
    bindings: computed<readonly CiBindingView[]>(() => progress()?.bindings ?? NO_BINDINGS),
    tokens: computed<readonly IngestTokenView[]>(() => progress()?.tokens ?? NO_TOKENS),
    /**
     * Whether step 2's list is the whole list or the first page of a longer one. False before a
     * derivation answers: no rows have been read, so nothing is being left out either.
     */
    bindingsTruncated: computed<boolean>(() => progress()?.bindingsTruncated ?? false),
    tokensTruncated: computed<boolean>(() => progress()?.tokensTruncated ?? false),
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
     * The sequence number of the newest `load`, per store instance (this store is a route-level
     * singleton, so two activations can overlap across a route change). A load's answer — success
     * or failure — is dropped when a newer load started meanwhile.
     */
    let newestLoad = 0;
    /**
     * The sequence number of the newest `refresh`, deliberately separate from `newestLoad` (F4).
     * Sharing one counter made a refresh that started during an in-flight load supersede that
     * load: the load dropped its answer, nothing else published, and the store stayed `loading`
     * for the rest of the activation. A refresh is an in-place re-derivation of a page that is
     * already on screen, so it never outranks the resume that decides what is on screen.
     */
    let newestRefresh = 0;
    /**
     * The scope a create just published, for the one activation its URL change causes. It is not
     * state: nothing renders it, and it must never outlive the next `load` (S71 N1).
     */
    let createdScope: OnboardingScope | null = null;

    async function load(orgId: string, projectId: string | null): Promise<void> {
      newestLoad += 1;
      const loadSequence = newestLoad;
      // Any load invalidates the create marker: a revisit, a route change or a Retry is a fresh
      // read, and the activation after it must not inherit a skip meant for another one.
      createdScope = null;
      patchState(store, { status: 'loading', failure: null, refreshFailure: null });
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

    /** Publish a step the user moved, with the focus cue the shell reads (see the header, 5). */
    function showStep(step: StepNumber): void {
      if (step === store.viewStep()) {
        return;
      }
      // A move dismisses a refresh notice: it reports a read that failed for the step that was on
      // screen, and a step re-entered later must not open with a banner for that earlier read.
      patchState(store, {
        viewStep: step,
        focusToken: store.focusToken() + 1,
        refreshFailure: null,
      });
    }

    /**
     * Re-derives the position without disturbing the view (header, 6). Its answer is dropped when
     * a newer refresh started, or when a load started after it did: this is a re-derivation of the
     * page on screen, so a resume that began later describes a page nobody is on any more.
     */
    async function refresh(orgId: string, projectId: string): Promise<void> {
      newestRefresh += 1;
      const refreshSequence = newestRefresh;
      // The load counter this refresh found. A load that starts afterwards bumps it, which is
      // what makes this answer stale — and the reverse is not true: this refresh leaves `newestLoad`
      // alone, so the in-flight load it overlaps still publishes (F4).
      const loadAtStart = newestLoad;
      try {
        const progress = await progressService.load(orgId, projectId);
        if (refreshSequence !== newestRefresh || loadAtStart !== newestLoad) {
          return;
        }
        // Only the derivation and its own failure marker: `status`, `viewStep`, `focusToken` and
        // the create marker belong to the activation on screen, which a refresh does not change.
        patchState(store, { progress, refreshFailure: null });
      } catch (error) {
        if (refreshSequence !== newestRefresh || loadAtStart !== newestLoad) {
          return;
        }
        // The page keeps what it renders — including the one-time secret it is revealing — and
        // shows that the refresh did not land (the shell has no full-page pattern for this).
        patchState(store, { refreshFailure: failureFor(error) });
      }
    }

    return {
      /**
       * Resumes the wizard for `orgId` and the route's optional `projectId`: publishes the
       * derivation and opens the first step that is not done. Never rejects (see the header).
       */
      load,

      /**
       * Re-derives the position in place: keeps `status`, `viewStep` and `focusToken`, patches
       * the derivation on success, and publishes `refreshFailure` instead of throwing. Never
       * rejects (see the header, 6).
       */
      refresh,

      /**
       * Drops the one-shot create marker without consuming it. A create whose URL navigation did
       * not happen must not leave a skip behind for an activation nobody made (N2).
       */
      clearCreated(): void {
        createdScope = null;
      },

      /**
       * Take the one-shot create marker, if this is the activation the create caused. True only
       * for the exact `(orgId, projectId)` `projectCreated` published, with that derivation still
       * ready, and only once: the marker is cleared here, so the next activation of the same URL
       * reads the server again instead of rendering a derivation nobody refreshed (S71 N1).
       */
      consumeCreated(orgId: string, projectId: string | null): boolean {
        const created = createdScope;
        if (created === null || created.orgId !== orgId || created.projectId !== projectId) {
          return false;
        }
        createdScope = null;
        return store.status() === 'ready' && store.progress() !== null;
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
          // This one read is the create's, so the activation the URL change causes may take it
          // instead of reading again — once (S71 F1). The create also opened a step on the user's
          // behalf, so it cues a focus move even though the component that URL change activates
          // is constructed after this patch (S71 F2).
          createdScope = { orgId: project.org_id, projectId: project.id };
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
