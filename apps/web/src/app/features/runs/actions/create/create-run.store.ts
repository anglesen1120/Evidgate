import { DestroyRef, inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { isApiRequestError, type ApiError } from '../../../../core/http/api-error';
import { createRetryAfterWait } from '../../../../core/http/retry';
import type { CreatedRun, CreateRunInput } from '../../data/run-actions.types';
import { RunDataService } from '../../data/run-data.service';

export const CreateRunStore = signalStore(
  withState<{ submitting: boolean; error: ApiError | null }>({ submitting: false, error: null }),
  withMethods((store, data = inject(RunDataService), destroyRef = inject(DestroyRef)) => {
    const retryWait = createRetryAfterWait();
    destroyRef.onDestroy(retryWait.clear);
    let intent: { body: string; key: string } | null = null;
    return {
      retryReady: retryWait.ready,
      clearError(): void {
        if (retryWait.ready()) patchState(store, { error: null });
      },
      async create(
        projectId: string,
        input: CreateRunInput,
        signal: AbortSignal,
      ): Promise<CreatedRun | null> {
        if (store.submitting() || !retryWait.ready() || signal.aborted) return null;
        const body = JSON.stringify(input);
        if (intent?.body !== body) intent = { body, key: data.newIdempotencyKey() };
        patchState(store, { submitting: true, error: null });
        try {
          const created = await data.createRun(projectId, input, intent.key, signal);
          return signal.aborted ? null : created;
        } catch (failure: unknown) {
          if (signal.aborted) return null;
          if (!isApiRequestError(failure)) throw failure;
          patchState(store, { error: failure.error });
          if (failure.error.kind === 'rate_limited' || failure.error.kind === 'unavailable')
            retryWait.start(failure.error.retryAfterSeconds);
          return null;
        } finally {
          patchState(store, { submitting: false });
        }
      },
    };
  }),
);
