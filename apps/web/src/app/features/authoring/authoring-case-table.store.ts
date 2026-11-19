import { computed, inject } from '@angular/core';
import type { ManualCaseListItemDto, ManualCaseListResponse } from '@evidgate/sdk';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { isApiRequestError, type ApiError } from '../../core/http/api-error';
import type { ManualCaseQuery } from '../../core/data/authoring-reads.service';
import { createRetryAfterWait } from '../../core/http/retry';
import { takeKeysetPage } from '../../shared/keyset-walk';
import { AuthoringDataService } from './authoring-data.service';
import {
  isCaseUuid,
  manualCaseQuery,
  queryTooLong,
  type CaseQueryParams,
} from './authoring-case-filters';

interface CaseTableState {
  projectId: string | null;
  query: ManualCaseQuery;
  routeCursor: string | null;
  scopeKey: string;
  rows: readonly ManualCaseListItemDto[];
  nextCursor: string | null;
  status: 'loading' | 'ready' | 'error';
  error: ApiError | null;
  loadingMore: boolean;
  stale: boolean;
  /** True while the loaded list is at its retained-row bound and the server had more. */
  browseCapped: boolean;
  /** True when the route names a checklist id that cannot be a UUID: no read was made. */
  invalidLink: boolean;
  /** The read that failed, so Retry replays it instead of restarting the walk. */
  failedRead: { readonly cursor: string | null; readonly append: boolean } | null;
}
const failure = (code: string, message: string): ApiError => ({
  kind: 'unexpected',
  status: 0,
  code,
  message,
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
});
const UNRESOLVED = failure(
  'request_unresolved',
  $localize`:@@authoring.case-table.unresolved:The case list request did not complete. Retry explicitly.`,
);
const REFUSALS = {
  cursorRepeated: failure(
    'cursor_repeated',
    $localize`:@@authoring.case-table.cursor-repeated:The server repeated the case cursor. Reload the list.`,
  ),
  pageWithoutNewRows: failure(
    'page_without_new_rows',
    $localize`:@@authoring.case-table.page-repeated:The next page had no new cases. Reload the list.`,
  ),
};

/** A route naming a checklist id that cannot be a UUID: the request is refused, never widened. */
const INVALID_ITEM: ApiError = {
  ...failure(
    'invalid_item_id',
    $localize`:@@authoring.case-table.invalid-item-link:This link's checklist ID is not a UUID, so no case list was requested. Open the checklist from the feature tree.`,
  ),
  kind: 'validation',
  retryable: false,
};

/**
 * The most cases one list keeps in hand. W7 promises cursor paging past 200 rows, so the bound
 * is high enough for a working scope; reaching it is an ordinary bounded outcome the list
 * announces, not a failure, because the only recovery is a narrower filter or another scope.
 */
export const CASE_ROWS_CAP = 10_000;

export const AuthoringCaseTableStore = signalStore(
  withState<CaseTableState>({
    projectId: null,
    query: { limit: 50 },
    routeCursor: null,
    scopeKey: '',
    rows: [],
    nextCursor: null,
    status: 'loading',
    error: null,
    loadingMore: false,
    stale: false,
    browseCapped: false,
    invalidLink: false,
    failedRead: null,
  }),
  withProps(() => ({
    _wait: createRetryAfterWait(),
    _run: { controller: null as AbortController | null },
  })),
  withComputed((store) => ({
    retryReady: computed(() => store._wait.ready()),
    filtered: computed(() => {
      const query = store.query();
      return Boolean(
        query.item_id || query.state || query.priority || query.covered !== undefined || query.q,
      );
    }),
  })),
  withMethods((store, data = inject(AuthoringDataService)) => {
    function abort(): void {
      store._run.controller?.abort();
      store._run.controller = null;
    }
    function acceptPage(
      page: ManualCaseListResponse,
      cursor: string | null,
      append: boolean,
    ): string | null {
      const step = takeKeysetPage({
        cursor,
        items: page.items,
        nextCursor: page.next_cursor,
        seen: new Set(append ? store.rows().map((row) => row.case_id) : []),
        keyOf: (row) => row.case_id,
        refusals: REFUSALS,
      });
      if (step.outcome === 'refused') {
        const retained = store.rows().length > 0;
        patchState(store, {
          error: step.error,
          status: retained ? 'ready' : 'error',
          stale: retained,
          // A page that cannot advance the walk leaves no cursor to follow: the control that
          // could only repeat the same request is gone, and Retry has no failed read to replay.
          nextCursor: null,
          failedRead: null,
        });
        return null;
      }
      const combined = append ? [...store.rows(), ...step.fresh] : step.fresh;
      const rows = combined.length > CASE_ROWS_CAP ? combined.slice(0, CASE_ROWS_CAP) : combined;
      const capped =
        rows.length < combined.length || (step.nextCursor !== null && rows.length >= CASE_ROWS_CAP);
      patchState(store, {
        rows,
        nextCursor: capped ? null : step.nextCursor,
        browseCapped: capped,
        routeCursor: cursor,
        status: 'ready',
        stale: false,
        error: null,
        failedRead: null,
      });
      store._wait.clear();
      return capped ? null : cursor;
    }
    async function read(append: boolean, cursorOverride?: string): Promise<string | null> {
      const projectId = store.projectId();
      if (!projectId || !store._wait.ready() || store.loadingMore()) return null;
      if (store.invalidLink() || store.browseCapped()) return null;
      const cursor = cursorOverride ?? (append ? store.nextCursor() : store.routeCursor());
      if (append && !cursor) return null;
      // Every read restates the search guard: a URL-supplied or stale value never reaches the wire.
      if (queryTooLong(store.query().q ?? '')) {
        const retained = store.rows().length > 0;
        patchState(store, {
          error: { ...UNRESOLVED, kind: 'validation', code: 'query_too_long', retryable: false },
          status: retained ? 'ready' : 'error',
          stale: retained,
          failedRead: null,
        });
        return null;
      }
      abort();
      const controller = new AbortController();
      store._run.controller = controller;
      const query = { ...store.query(), ...(cursor ? { cursor } : {}) };
      patchState(store, {
        error: null,
        loadingMore: append,
        ...(append ? {} : { status: 'loading' as const }),
      });
      try {
        const page = await data.listManualCases(projectId, query, controller.signal);
        if (controller.signal.aborted || store._run.controller !== controller) return null;
        return acceptPage(page, cursor, append);
      } catch (error: unknown) {
        if (controller.signal.aborted || store._run.controller !== controller) return null;
        const typed = isApiRequestError(error) ? error.error : UNRESOLVED;
        const denied = typed.kind === 'forbidden' || typed.kind === 'not_found';
        const retained = !denied && store.rows().length > 0;
        patchState(store, {
          error: typed,
          rows: denied ? [] : store.rows(),
          nextCursor: denied ? null : store.nextCursor(),
          status: retained ? 'ready' : 'error',
          stale: retained,
          failedRead: denied ? null : { cursor, append },
        });
        store._wait.start(typed.retryAfterSeconds);
        return null;
      } finally {
        if (store._run.controller === controller) {
          store._run.controller = null;
          patchState(store, { loadingMore: false });
        }
      }
    }
    return {
      async init(projectId: string, raw: CaseQueryParams): Promise<void> {
        const query = manualCaseQuery(raw);
        const { cursor, ...scope } = query;
        const namedItem = raw['item_id'] ?? '';
        const invalidLink = namedItem !== '' && !isCaseUuid(namedItem);
        // A refused link is a scope of its own: the unfiltered list an earlier visit loaded must
        // not stand in for a filter the route asked for.
        const scopeKey = JSON.stringify([projectId, scope, invalidLink ? namedItem : null]);
        if (scopeKey === store.scopeKey() && (cursor ?? null) === store.routeCursor()) return;
        abort();
        store._wait.clear();
        patchState(store, {
          projectId,
          query: scope,
          routeCursor: cursor ?? null,
          scopeKey,
          rows: [],
          nextCursor: null,
          loadingMore: false,
          stale: false,
          browseCapped: false,
          invalidLink,
          error: invalidLink ? INVALID_ITEM : null,
          status: invalidLink ? 'error' : 'loading',
          failedRead: null,
        });
        await read(false);
      },
      async reload(): Promise<void> {
        // Explicit refresh restarts the walk; keeping a page cursor would drop the pages before it.
        patchState(store, { routeCursor: null, browseCapped: false });
        await read(false);
      },
      retry: (): Promise<string | null> => {
        const failed = store.failedRead();
        if (failed === null) return Promise.resolve(null);
        return failed.append && failed.cursor !== null ? read(true, failed.cursor) : read(false);
      },
      loadMore: () => read(true),
      cancel: abort,
    };
  }),
  withHooks({
    onDestroy(store) {
      store.cancel();
      store._wait.clear();
    },
  }),
);
