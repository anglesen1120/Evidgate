import { computed, DestroyRef, effect, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { isApiRequestError, type ApiError } from '../../../../core/http/api-error';
import { SessionStore } from '../../../../core/session/session.store';
import { NarrowMutationPolicy } from '../../../../shared/ui/narrow/narrow-mutation-policy';
import { clampTimerDelay } from '../../../../shared/time/timer-delay';
import { RunDataService } from '../../data/run-data.service';
import { RunDetailStore } from '../../detail/run-detail.store';
import { isTerminalRun } from './cancel-run-state';

export const CANCEL_POLL_INTERVAL_MS = 2000;
export const CANCEL_POLL_ATTEMPTS = 15;
type Phase =
  'idle' | 'submitting' | 'error' | 'polling' | 'canceled' | 'terminal' | 'waiting' | 'poll-error';
type Scope = { runId: string; orgId: string; projectId: string };
const sameScope = (left: Scope | null, right: Scope | null): boolean =>
  left !== null &&
  right !== null &&
  left.runId === right.runId &&
  left.orgId === right.orgId &&
  left.projectId === right.projectId;
const unknownFailure: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: '',
  message: '',
  requestId: null,
  retryable: false,
  retryAfterSeconds: null,
  details: [],
};

export const CancelRunStore = signalStore(
  withState<{
    origin: Scope | null;
    dialogOpen: boolean;
    actionPhase: Phase;
    actionError: ApiError | null;
    retryAllowed: boolean;
    sent: boolean;
  }>({
    origin: null,
    dialogOpen: false,
    actionPhase: 'idle',
    actionError: null,
    retryAllowed: false,
    sent: false,
  }),
  withProps(() => {
    const detail = inject(RunDetailStore);
    const session = inject(SessionStore);
    const viewport = inject(NarrowMutationPolicy);
    const scope = computed<Scope | null>(() => {
      if (!session.has('run:cancel')) return null;
      const runId = detail.run()?.run_id;
      const orgId = session.currentOrg()?.id;
      const projectId = session.currentProject()?.id;
      return runId && orgId && projectId ? { runId, orgId, projectId } : null;
    });
    return { detail, viewport, scope };
  }),
  withComputed((store) => ({
    phase: computed<Phase>(() =>
      sameScope(store.origin(), store.scope()) ? store.actionPhase() : 'idle',
    ),
    opened: computed(() => sameScope(store.origin(), store.scope()) && store.dialogOpen()),
    error: computed(() => (sameScope(store.origin(), store.scope()) ? store.actionError() : null)),
    retryReady: computed(() => sameScope(store.origin(), store.scope()) && store.retryAllowed()),
    hasSubmitted: computed(() => sameScope(store.origin(), store.scope()) && store.sent()),
  })),
  withComputed((store) => ({
    canCancel: computed(() => {
      const run = store.detail.run();
      const phase = store.phase();
      return (
        store.scope() !== null &&
        run !== null &&
        !isTerminalRun(run.state) &&
        store.viewport.canMutate() &&
        (phase === 'idle' || phase === 'error')
      );
    }),
  })),
  withMethods((store) => {
    const data = inject(RunDataService);
    const destroyRef = inject(DestroyRef);
    let controller: AbortController | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let key: string | null = null;
    let submittedReason: string | undefined;
    const stop = (): void => {
      controller?.abort();
      clearTimeout(timer);
      timer = undefined;
    };
    const owned = (scope: Scope): boolean =>
      !destroyRef.destroyed &&
      !controller?.signal.aborted &&
      sameScope(scope, store.scope()) &&
      sameScope(scope, store.origin());
    const waitToRetry = (error: ApiError): void => {
      const delay = Math.max(0, (error.retryAfterSeconds ?? 0) * 1000);
      patchState(store, { retryAllowed: error.retryable && delay === 0 });
      if (error.retryable && delay > 0) {
        const deadline = Date.now() + delay;
        const release = (): void => {
          const remaining = deadline - Date.now();
          if (remaining > 0) timer = setTimeout(release, clampTimerDelay(remaining));
          else patchState(store, { retryAllowed: true });
        };
        timer = setTimeout(release, clampTimerDelay(delay));
      }
    };
    const poll = async (scope: Scope, attempt: number): Promise<void> => {
      if (!owned(scope)) return;
      try {
        await store.detail.refreshRun(scope.runId);
        if (!owned(scope)) return;
        const state = store.detail.run()?.state;
        if (state && isTerminalRun(state)) {
          patchState(store, {
            actionPhase: state === 'canceled' ? 'canceled' : 'terminal',
            actionError: null,
          });
        } else if (attempt + 1 >= CANCEL_POLL_ATTEMPTS) {
          patchState(store, { actionPhase: 'waiting' });
        } else {
          timer = setTimeout(() => {
            void poll(scope, attempt + 1);
          }, CANCEL_POLL_INTERVAL_MS);
        }
      } catch (failure: unknown) {
        if (owned(scope)) {
          const error = isApiRequestError(failure) ? failure.error : unknownFailure;
          patchState(store, { actionPhase: 'poll-error', actionError: error });
          waitToRetry(error);
        }
      }
    };
    destroyRef.onDestroy(stop);
    effect(() => {
      const scope = store.scope();
      if (controller && !sameScope(store.origin(), scope)) stop();
    });
    return {
      open(): void {
        if (!store.canCancel() || !store.viewport.allowsMutation() || store.opened()) return;
        stop();
        controller = new AbortController();
        key = data.newIdempotencyKey();
        submittedReason = undefined;
        patchState(store, {
          origin: store.scope(),
          dialogOpen: true,
          actionPhase: 'idle',
          actionError: null,
          retryAllowed: false,
          sent: false,
        });
      },
      dismiss(): void {
        if (store.phase() === 'submitting') return;
        patchState(store, { dialogOpen: false });
      },
      async confirm(reason: string | undefined): Promise<void> {
        const scope = store.origin();
        if (
          !scope ||
          !key ||
          !owned(scope) ||
          !store.opened() ||
          !store.canCancel() ||
          !store.viewport.allowsMutation() ||
          (store.error() && !store.retryReady())
        )
          return;
        if (!store.sent()) submittedReason = reason;
        patchState(store, {
          actionPhase: 'submitting',
          actionError: null,
          sent: true,
          retryAllowed: false,
        });
        try {
          await data.cancelRun(scope.runId, submittedReason, key, controller?.signal);
          if (!owned(scope)) return;
          patchState(store, { actionPhase: 'polling', actionError: null });
          await poll(scope, 0);
        } catch (failure: unknown) {
          if (!owned(scope)) return;
          const error = isApiRequestError(failure) ? failure.error : unknownFailure;
          patchState(store, { actionPhase: 'error', actionError: error });
          waitToRetry(error);
        }
      },
      async checkState(): Promise<void> {
        const scope = store.origin();
        if (
          !scope ||
          !owned(scope) ||
          !['waiting', 'poll-error'].includes(store.phase()) ||
          (store.error() && !store.retryReady())
        )
          return;
        clearTimeout(timer);
        patchState(store, { actionPhase: 'polling', actionError: null, retryAllowed: false });
        await poll(scope, 0);
      },
    };
  }),
);
