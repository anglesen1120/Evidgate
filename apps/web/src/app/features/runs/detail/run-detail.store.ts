import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import type { RunStateDto } from '@evidgate/sdk';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { UNAVAILABLE_MANIFEST, type ManifestSummary } from './manifest-summary';

export type RunDetailStatus = 'idle' | 'loading' | 'ready' | 'not-found' | 'forbidden' | 'error';
interface RunDetailState {
  run: RunStateDto | null;
  manifest: ManifestSummary;
  status: RunDetailStatus;
  loadError: ApiError | null;
}

/** Page-local, newest-read-wins. Neither a failed read nor route reuse retains old run data. */
export const RunDetailStore = signalStore(
  withState<RunDetailState>({
    run: null,
    manifest: UNAVAILABLE_MANIFEST,
    status: 'idle',
    loadError: null,
  }),
  withMethods((store) => {
    const data = inject(RunsDataService);
    let generation = 0;
    return {
      async load(runId: string): Promise<void> {
        const read = ++generation;
        patchState(store, {
          run: null,
          manifest: UNAVAILABLE_MANIFEST,
          status: 'loading',
          loadError: null,
        });
        try {
          const run = await data.get(runId);
          if (read !== generation) return;
          // PANEL-CHOICE A / D-S103-Q1: absence on the wire is unavailable, never inferred.
          patchState(store, { run, manifest: UNAVAILABLE_MANIFEST, status: 'ready' });
        } catch (failure: unknown) {
          if (read !== generation) return;
          const error: ApiError = isApiRequestError(failure)
            ? failure.error
            : {
                kind: 'unexpected',
                status: 0,
                code: '',
                message: '',
                requestId: null,
                retryable: false,
                details: [],
                retryAfterSeconds: null,
              };
          patchState(store, {
            run: null,
            manifest: UNAVAILABLE_MANIFEST,
            status:
              error.kind === 'not_found'
                ? 'not-found'
                : error.kind === 'forbidden'
                  ? 'forbidden'
                  : 'error',
            loadError: error,
          });
        }
      },
    };
  }),
);
