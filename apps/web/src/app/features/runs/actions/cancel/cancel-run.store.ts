import { computed, DestroyRef, effect, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
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
type CancelIntent = {
  controller: AbortController | null;
  timer: ReturnType<typeof setTimeout> | undefined;
  key: string | null;
  submittedReason: string | undefined;
};
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
    const intent: CancelIntent = {
      controller: null,
      timer: undefined,
      key: null,
      submittedReason: undefined,
    };
    return {
      detail,
      viewport,
      scope,
      _intent: intent,
      _data: inject(RunDataService),
      _destroyRef: inject(DestroyRef),
    };
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
  withMethods((store) => ({
    _stop(): void {
      store._intent.controller?.abort();
      clearTimeout(store._intent.timer);
      store._intent.timer = undefined;
    },
    _owned(scope: Scope): boolean {
      return (
        !store._destroyRef.destroyed &&
        !store._intent.controller?.signal.aborted &&
        sameScope(scope, store.scope()) &&
        sameScope(scope, store.origin())
      );
    },
    _waitToRetry(error: ApiError): void {
      const delay = Math.max(0, (error.retryAfterSeconds ?? 0) * 1000);
      patchState(store, { retryAllowed: error.retryable && delay === 0 });
      if (error.retryable && delay > 0) {
        const deadline = Date.now() + delay;
        const release = (): void => {
          const remaining = deadline - Date.now();
          if (remaining > 0) store._intent.timer = setTimeout(release, clampTimerDelay(remaining));
          else patchState(store, { retryAllowed: true });
        };
        store._intent.timer = setTimeout(release, clampTimerDelay(delay));
      }
    },
  })),
  withMethods((store) => {
    const poll = async (scope: Scope, attempt: number): Promise<void> => {
      if (!store._owned(scope)) return;
      try {
        await store.detail.refreshRun(scope.runId);
        if (!store._owned(scope)) return;
        const state = store.detail.run()?.state;
        if (state && isTerminalRun(state)) {
          patchState(store, {
            actionPhase: state === 'canceled' ? 'canceled' : 'terminal',
            actionError: null,
          });
        } else if (attempt + 1 >= CANCEL_POLL_ATTEMPTS) {
          patchState(store, { actionPhase: 'waiting' });
        } else {
          store._intent.timer = setTimeout(() => {
            void poll(scope, attempt + 1);
          }, CANCEL_POLL_INTERVAL_MS);
        }
      } catch (failure: unknown) {
        if (store._owned(scope)) {
          const error = isApiRequestError(failure) ? failure.error : unknownFailure;
          patchState(store, { actionPhase: 'poll-error', actionError: error });
          store._waitToRetry(error);
        }
      }
    };
    return { _poll: poll };
  }),
  withMethods((store) => ({
    open(): void {
      if (!store.canCancel() || !store.viewport.allowsMutation() || store.opened()) return;
      store._stop();
      store._intent.controller = new AbortController();
      store._intent.key = store._data.newIdempotencyKey();
      store._intent.submittedReason = undefined;
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
  })),
  withMethods((store) => ({
    async confirm(reason: string | undefined): Promise<void> {
      const scope = store.origin();
      const key = store._intent.key;
      if (
        !scope ||
        !key ||
        !store._owned(scope) ||
        !store.opened() ||
        !store.canCancel() ||
        !store.viewport.allowsMutation() ||
        (store.error() && !store.retryReady())
      )
        return;
      if (!store.sent()) store._intent.submittedReason = reason;
      patchState(store, {
        actionPhase: 'submitting',
        actionError: null,
        sent: true,
        retryAllowed: false,
      });
      try {
        await store._data.cancelRun(
          scope.runId,
          store._intent.submittedReason,
          key,
          store._intent.controller?.signal,
        );
        if (!store._owned(scope)) return;
        patchState(store, { actionPhase: 'polling', actionError: null });
        await store._poll(scope, 0);
      } catch (failure: unknown) {
        if (!store._owned(scope)) return;
        const error = isApiRequestError(failure) ? failure.error : unknownFailure;
        patchState(store, { actionPhase: 'error', actionError: error });
        store._waitToRetry(error);
      }
    },
    async checkState(): Promise<void> {
      const scope = store.origin();
      if (
        !scope ||
        !store._owned(scope) ||
        !['waiting', 'poll-error'].includes(store.phase()) ||
        (store.error() && !store.retryReady())
      )
        return;
      clearTimeout(store._intent.timer);
      patchState(store, { actionPhase: 'polling', actionError: null, retryAllowed: false });
      await store._poll(scope, 0);
    },
  })),
  withHooks((store) => ({
    onInit(): void {
      effect(() => {
        const scope = store.scope();
        if (store._intent.controller && !sameScope(store.origin(), scope)) store._stop();
      });
    },
    onDestroy(): void {
      store._stop();
    },
  })),
);
