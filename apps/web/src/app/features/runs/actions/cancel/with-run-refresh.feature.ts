import { DestroyRef, inject } from '@angular/core';
import type { RunStateDto } from '@evidgate/sdk';
import { patchState, signalStoreFeature, type, withMethods } from '@ngrx/signals';
import { RunsDataService } from '../../../../core/data/runs-data.service';
import { isApiRequestError, type ApiError } from '../../../../core/http/api-error';
import { refusesAccess } from '../../detail/decision/decision-refusal';
import { manifestFromRun, type ManifestSummary } from '../../detail/manifest-summary';
import type { RunDetailStatus } from '../../detail/run-detail.store';
import { isTerminalRun } from './cancel-run-state';

export function withRunRefresh() {
  return signalStoreFeature(
    {
      state: type<{
        run: RunStateDto | null;
        manifest: ManifestSummary;
        status: RunDetailStatus;
        loadError: ApiError | null;
      }>(),
    },
    withMethods((store) => {
      const data = inject(RunsDataService);
      const destroyRef = inject(DestroyRef);
      return {
        async refreshRun(runId: string): Promise<void> {
          if (store.run()?.run_id !== runId) return;
          try {
            const run = await data.get(runId);
            const current = store.run();
            if (destroyRef.destroyed || current?.run_id !== runId) return;
            if (
              run.version < current.version ||
              (isTerminalRun(current.state) && !isTerminalRun(run.state))
            )
              return;
            patchState(store, { run, manifest: manifestFromRun(run) });
          } catch (failure: unknown) {
            if (
              !destroyRef.destroyed &&
              store.run()?.run_id === runId &&
              isApiRequestError(failure) &&
              refusesAccess(failure.error)
            ) {
              patchState(store, {
                run: null,
                loadError: failure.error,
                status: failure.error.kind === 'not_found' ? 'not-found' : 'forbidden',
              });
            }
            throw failure;
          }
        },
      };
    }),
  );
}
