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
  caseFiltersFromQuery,
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
        });
        return null;
      }
      patchState(store, {
        rows: append ? [...store.rows(), ...step.fresh] : step.fresh,
        nextCursor: step.nextCursor,
        routeCursor: cursor,
        status: 'ready',
        stale: false,
        error: null,
      });
      store._wait.clear();
      return cursor;
    }
    async function read(append: boolean): Promise<string | null> {
      const projectId = store.projectId();
      if (!projectId || !store._wait.ready() || store.loadingMore()) return null;
      const cursor = append ? store.nextCursor() : store.routeCursor();
      if (append && !cursor) return null;
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
        const scopeKey = JSON.stringify([projectId, scope]);
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
          error: null,
        });
        if (queryTooLong(caseFiltersFromQuery(raw).q)) {
          patchState(store, {
            status: 'error',
            error: { ...UNRESOLVED, kind: 'validation', code: 'query_too_long', retryable: false },
          });
          return;
        }
        await read(false);
      },
      async reload(): Promise<void> {
        await read(false);
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
