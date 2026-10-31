import { computed, inject } from '@angular/core';
import type { SuiteRevisionDto } from '@evidgate/sdk';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { SuitesDataService } from '../../../core/data/suites-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { compositionView } from './suite-composition.model';

export type SuiteCompositionStatus =
  'idle' | 'loading' | 'ready' | 'not-found' | 'forbidden' | 'unavailable' | 'error';

interface SuiteCompositionState {
  revision: SuiteRevisionDto | null;
  status: SuiteCompositionStatus;
  loadError: ApiError | null;
}

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

function failureStatus(error: ApiError): SuiteCompositionStatus {
  switch (error.kind) {
    case 'not_found':
      return 'not-found';
    case 'forbidden':
      return 'forbidden';
    case 'unavailable':
      return 'unavailable';
    default:
      return 'error';
  }
}

export const SuiteCompositionStore = signalStore(
  withState<SuiteCompositionState>({ revision: null, status: 'idle', loadError: null }),
  withComputed(({ revision }) => ({
    view: computed(() => {
      const value = revision();
      return value === null ? null : compositionView(value);
    }),
  })),
  withMethods((store) => {
    const data = inject(SuitesDataService);
    // A reused route can start a second read before the first settles. Only the latest read
    // may replace its revision or error, including when the older request fails last.
    let generation = 0;
    return {
      async load(revisionId: string): Promise<void> {
        const read = ++generation;
        patchState(store, { revision: null, status: 'loading', loadError: null });
        try {
          const revision = await data.getRevision(revisionId);
          if (read !== generation) return;
          patchState(store, { revision, status: 'ready' });
        } catch (failure: unknown) {
          if (read !== generation) return;
          const error = isApiRequestError(failure) ? failure.error : UNRESOLVED_FAILURE;
          patchState(store, {
            revision: null,
            status: failureStatus(error),
            loadError: error,
          });
        }
      },
    };
  }),
);
