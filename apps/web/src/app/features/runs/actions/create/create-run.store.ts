import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { isApiRequestError, type ApiError } from '../../../../core/http/api-error';
import type { CreatedRun, CreateRunInput } from '../../data/run-actions.types';
import { RunDataService } from '../../data/run-data.service';

export const CreateRunStore = signalStore(
  withState<{ submitting: boolean; error: ApiError | null }>({ submitting: false, error: null }),
  withMethods((store, data = inject(RunDataService)) => {
    let intent: { body: string; key: string } | null = null;
    return {
      clearError(): void {
        patchState(store, { error: null });
      },
      async create(
        projectId: string,
        input: CreateRunInput,
        signal: AbortSignal,
      ): Promise<CreatedRun | null> {
        if (store.submitting() || signal.aborted) return null;
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
          return null;
        } finally {
          patchState(store, { submitting: false });
        }
      },
    };
  }),
);
