import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { RunStateDto } from '@evidgate/sdk';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { manifestFromRun, UNAVAILABLE_MANIFEST, type ManifestSummary } from './manifest-summary';
import { toAttemptRow, type AttemptRow } from './attempts/attempt-summary';

/** Rows per attempts request: the contract's maximum, so a normal run is one read. */
export const ATTEMPTS_PAGE_LIMIT = 200;

export type AttemptsStatus = 'idle' | 'loading' | 'ready' | 'error';
export type RunDetailStatus = 'idle' | 'loading' | 'ready' | 'not-found' | 'forbidden' | 'error';
interface RunDetailState {
  run: RunStateDto | null;
  manifest: ManifestSummary;
  status: RunDetailStatus;
  loadError: ApiError | null;
  /** `null` until the first attempts page lands: the table renders loading/error/unavailable. */
  attempts: AttemptRow[] | null;
  attemptsStatus: AttemptsStatus;
  attemptsError: ApiError | null;
  /** The opaque `next_cursor` of the last page; `null` once the keyset walk is complete. */
  attemptsCursor: string | null;
  attemptsLoadingMore: boolean;
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
function toApiError(failure: unknown): ApiError {
  return isApiRequestError(failure) ? failure.error : UNRESOLVED_FAILURE;
}

/** Page-local, newest-read-wins. Neither a failed read nor route reuse retains old run data. */
export const RunDetailStore = signalStore(
  withState<RunDetailState>({
    run: null,
    manifest: UNAVAILABLE_MANIFEST,
    status: 'idle',
    loadError: null,
    attempts: null,
    attemptsStatus: 'idle',
    attemptsError: null,
    attemptsCursor: null,
    attemptsLoadingMore: false,
  }),
  withComputed((store) => ({ hasMoreAttempts: computed(() => store.attemptsCursor() !== null) })),
  withMethods((store) => {
    const data = inject(RunsDataService);
    let generation = 0;
    let attemptsGeneration = 0;
    /** One read of the attempts keyset: the first page replaces, a later page appends. */
    async function readAttempts(runId: string, cursor: string | null): Promise<void> {
      const read = ++attemptsGeneration;
      const first = cursor === null;
      patchState(
        store,
        first
          ? { attempts: null, attemptsStatus: 'loading', attemptsError: null, attemptsCursor: null }
          : { attemptsLoadingMore: true, attemptsError: null },
      );
      try {
        const page = await data.listAttempts(runId, {
          limit: ATTEMPTS_PAGE_LIMIT,
          ...(cursor === null ? {} : { cursor }),
        });
        if (read !== attemptsGeneration) return;
        // A cursor the server repeats would loop forever: refuse it as a failure.
        if (page.next_cursor !== null && page.next_cursor === cursor) {
          throw new Error('repeated attempts cursor');
        }
        const rows = page.items.map(toAttemptRow);
        patchState(store, {
          attempts: first ? rows : [...(store.attempts() ?? []), ...rows],
          attemptsStatus: 'ready',
          attemptsCursor: page.next_cursor,
          attemptsLoadingMore: false,
        });
      } catch (failure: unknown) {
        if (read !== attemptsGeneration) return;
        patchState(store, {
          attemptsStatus: first ? 'error' : 'ready',
          attemptsError: toApiError(failure),
          attemptsLoadingMore: false,
        });
      }
    }
    return {
      async load(runId: string): Promise<void> {
        const read = ++generation;
        ++attemptsGeneration;
        patchState(store, {
          run: null,
          manifest: UNAVAILABLE_MANIFEST,
          status: 'loading',
          loadError: null,
          attempts: null,
          attemptsStatus: 'idle',
          attemptsError: null,
          attemptsCursor: null,
          attemptsLoadingMore: false,
        });
        try {
          const run = await data.get(runId);
          if (read !== generation) return;
          patchState(store, { run, manifest: manifestFromRun(run), status: 'ready' });
          void readAttempts(runId, null);
        } catch (failure: unknown) {
          if (read !== generation) return;
          const error = toApiError(failure);
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
      /** Re-read the first attempts page after a failure; the run itself is not re-read. */
      async retryAttempts(): Promise<void> {
        const run = store.run();
        if (run !== null) await readAttempts(run.run_id, null);
      },
      /** Follow `next_cursor` once; never loops, and refuses while a read is in flight. */
      async loadMoreAttempts(): Promise<void> {
        const run = store.run();
        const cursor = store.attemptsCursor();
        if (run === null || cursor === null || store.attemptsLoadingMore()) return;
        await readAttempts(run.run_id, cursor);
      },
    };
  }),
);
