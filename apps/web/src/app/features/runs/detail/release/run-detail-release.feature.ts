import { inject } from '@angular/core';
import { patchState, signalStoreFeature, withMethods, withState } from '@ngrx/signals';
import type { ReleaseDetail } from '@evidgate/sdk';

import { ReleasesDataService } from '../../../../core/data/releases-data.service';
import { isApiRequestError, type ApiError } from '../../../../core/http/api-error';
import type { ReleaseStatus } from './release-summary';

/** The release region of the run page; its failure never becomes a run-page failure. */
interface ReleaseState {
  release: ReleaseDetail | null;
  releaseStatus: ReleaseStatus;
  releaseError: ApiError | null;
}

const NO_RELEASE: ReleaseState = { release: null, releaseStatus: 'idle', releaseError: null };

const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: '',
  message: '',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/** The region states a caller can act on; everything else is the retryable error panel. */
function statusFor(error: ApiError): ReleaseStatus {
  if (error.kind === 'not_found') return 'not-found';
  return error.kind === 'forbidden' ? 'forbidden' : 'error';
}

/**
 * The release read a run page performs when it knows a release id. A run created with
 * `release_id` carries no release id in `GET /v1/runs/{r}`, so no caller reads this today; the
 * region still owns its own state so wiring it later cannot couple it to the run read.
 *
 * One accepted answer at a time: a newer `loadRelease` or `resetRelease` invalidates the one in
 * flight, so a slow response for a previous release or a previous run can never land.
 */
export function withRunDetailRelease() {
  return signalStoreFeature(
    withState<ReleaseState>({ ...NO_RELEASE }),
    withMethods((store) => {
      const data = inject(ReleasesDataService);
      let generation = 0;
      return {
        /** Read one release by id; the previous release is dropped before the request starts. */
        async loadRelease(releaseId: string): Promise<void> {
          const read = ++generation;
          patchState(store, { release: null, releaseStatus: 'loading', releaseError: null });
          try {
            const release = await data.get(releaseId);
            if (read !== generation) return;
            patchState(store, { release, releaseStatus: 'ready' });
          } catch (failure: unknown) {
            if (read !== generation) return;
            const error = isApiRequestError(failure) ? failure.error : UNRESOLVED_FAILURE;
            patchState(store, {
              release: null,
              releaseStatus: statusFor(error),
              releaseError: error,
            });
          }
        },
        /** Forget the region and any answer in flight; a run change calls it. */
        resetRelease(): void {
          ++generation;
          patchState(store, { ...NO_RELEASE });
        },
      };
    }),
  );
}
