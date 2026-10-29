import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { RunAttemptSummary, RunStateDto } from '@evidgate/sdk';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { ApiRequestError, isApiRequestError, type ApiError } from '../../../core/http/api-error';
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
  /** The opaque `next_cursor` of the last page; `null` once the walk is complete or refused. */
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

/**
 * The two ways a keyset walk reads itself into a loop: the server hands back the cursor it was
 * given, or a follow-up page adds no attempt that is not already loaded. Both end the walk with
 * a TYPED failure — a plain `Error` would reach `toApiError` as `UNRESOLVED_FAILURE` and leave
 * the panel with empty copy — and neither is retryable, because the same request would be
 * refused the same way. The cursor is dropped with the failure, so Load more cannot re-request it.
 */
const ATTEMPTS_CURSOR_REPEATED: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'attempts_cursor_repeated',
  message: 'The attempts list returned the same page cursor again.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
const ATTEMPTS_PAGE_WITHOUT_NEW_ROWS: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'attempts_page_without_new_rows',
  message: 'The attempts list returned a follow-up page with no new attempts.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
const ATTEMPTS_WALK_REFUSALS: ReadonlySet<string> = new Set([
  ATTEMPTS_CURSOR_REPEATED.code,
  ATTEMPTS_PAGE_WITHOUT_NEW_ROWS.code,
]);

/**
 * The incoming page without the attempts already loaded, in page order, with the first copy
 * kept. A write between two keyset reads shifts the window, and a server repeating an entry is
 * not forbidden, so a row can arrive twice; a cursor cycle (`A → B → A`) serves a whole earlier
 * page again. Rendering it twice would give one attempt two rows. Every attempt_id seen —
 * loaded before this read or new here — is added to `seen`, which the caller keeps for the walk.
 */
function takeFreshAttempts(
  seen: Set<string>,
  incoming: readonly RunAttemptSummary[],
): RunAttemptSummary[] {
  const fresh: RunAttemptSummary[] = [];
  for (const attempt of incoming) {
    if (seen.has(attempt.attempt_id)) continue;
    seen.add(attempt.attempt_id);
    fresh.push(attempt);
  }
  return fresh;
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
    /** `attempt_id`s already shown; only an accepted page updates it, `load()` clears it. */
    let loadedAttemptIds = new Set<string>();
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
        // A cursor the server repeats would loop forever: refuse it as a typed failure.
        if (page.next_cursor !== null && page.next_cursor === cursor) {
          throw new ApiRequestError(ATTEMPTS_CURSOR_REPEATED);
        }
        const seen = new Set(first ? [] : loadedAttemptIds);
        const fresh = takeFreshAttempts(seen, page.items);
        // A follow-up page that adds no attempt cannot advance the walk: refuse it the same way.
        if (!first && fresh.length === 0) {
          throw new ApiRequestError(ATTEMPTS_PAGE_WITHOUT_NEW_ROWS);
        }
        loadedAttemptIds = seen;
        const rows = fresh.map(toAttemptRow);
        patchState(store, {
          attempts: first ? rows : [...(store.attempts() ?? []), ...rows],
          attemptsStatus: 'ready',
          attemptsCursor: page.next_cursor,
          attemptsLoadingMore: false,
        });
      } catch (failure: unknown) {
        if (read !== attemptsGeneration) return;
        const error = toApiError(failure);
        patchState(store, {
          attemptsStatus: first ? 'error' : 'ready',
          attemptsError: error,
          attemptsLoadingMore: false,
          // A refused walk ends here: the rows stay, and Load more goes away with the cursor.
          ...(ATTEMPTS_WALK_REFUSALS.has(error.code) ? { attemptsCursor: null } : {}),
        });
      }
    }
    return {
      async load(runId: string): Promise<void> {
        const read = ++generation;
        ++attemptsGeneration;
        loadedAttemptIds = new Set<string>();
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
