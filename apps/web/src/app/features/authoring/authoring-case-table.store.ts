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
import { RETAINED_ROWS_CAP, retainKeysetRows } from '../../shared/keyset-retention';
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
  /** True when the route names a checklist or category id that cannot be a UUID: no read was made. */
  invalidLink: boolean;
  /** Which route value was refused, so the notice can name the filter the recovery clears. */
  invalidLinkField: 'item' | 'node' | null;
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
const refusal = (code: string, message: string): ApiError => ({
  ...failure(code, message),
  retryable: false,
});
const REFUSALS = {
  cursorRepeated: refusal(
    'cursor_repeated',
    $localize`:@@authoring.case-table.cursor-repeated:The server repeated the case cursor, so paging stopped. Refresh to reload this list, or narrow the filters.`,
  ),
  pageWithoutNewRows: refusal(
    'page_without_new_rows',
    $localize`:@@authoring.case-table.page-repeated:The next page carried no new cases, so paging stopped. Refresh to reload this list, or narrow the filters.`,
  ),
};

/** A route naming an id that cannot be a UUID: the request is refused, never widened. */
const refusedLink = (code: string, message: string): ApiError => ({
  ...refusal(code, message),
  kind: 'validation',
});

/** A route naming a checklist id that cannot be a UUID: the request is refused, never widened. */
const INVALID_ITEM = refusedLink(
  'invalid_item_id',
  $localize`:@@authoring.case-table.invalid-item-link:This link's checklist ID is not a UUID, so no case list was requested. Clear the checklist filter or open the checklist from the feature tree.`,
);

/** A route naming a category id that cannot be a UUID: same refusal, instead of the whole scope. */
const INVALID_NODE = refusedLink(
  'invalid_node_id',
  $localize`:@@authoring.case-table.invalid-node-link:This link's category ID is not a UUID, so no case list was requested. Clear the category filter or choose a category in the feature tree.`,
);

/** The server rejects an oversized search: refused like a link, with no failed read to replay. */
const QUERY_TOO_LONG = refusedLink(
  'query_too_long',
  $localize`:@@authoring.case-table.query-too-long:This search is longer than the server accepts, so no case list was requested. Shorten the search and apply filters again.`,
);

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
    invalidLinkField: null,
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
    const cap = inject(RETAINED_ROWS_CAP);
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
          // The refusals are not refresh failures: the retained rows are the same accepted
          // results, so the stale-refresh notice would describe a request that never happened.
          stale: false,
          // A page that cannot advance the walk leaves no cursor to follow: the control that
          // could only repeat the same request is gone, and Retry has no failed read to replay.
          nextCursor: null,
          failedRead: null,
        });
        return null;
      }
      const combined = append ? [...store.rows(), ...step.fresh] : step.fresh;
      const retained = retainKeysetRows(combined, step.nextCursor, cap);
      patchState(store, {
        rows: retained.rows,
        nextCursor: retained.nextCursor,
        browseCapped: retained.capped,
        routeCursor: cursor,
        status: 'ready',
        stale: false,
        error: null,
        failedRead: null,
      });
      store._wait.clear();
      // The accepted page keeps its request cursor even when it lands on the cap: the caller
      // still owns the URL step and the focus handoff for the rows it just received, while
      // `nextCursor: null` alone says the walk cannot continue.
      return cursor;
    }
    async function read(append: boolean, cursorOverride?: string): Promise<string | null> {
      const projectId = store.projectId();
      if (!projectId || !store._wait.ready() || store.loadingMore()) return null;
      if (store.invalidLink()) return null;
      // The cap only ends the walk forward: a first-page read still replaces the capped list.
      if (append && store.browseCapped()) return null;
      const cursor = cursorOverride ?? (append ? store.nextCursor() : store.routeCursor());
      if (append && !cursor) return null;
      // Every read restates the search guard: a URL-supplied or stale value never reaches the wire.
      if (queryTooLong(store.query().q ?? '')) {
        const retained = store.rows().length > 0;
        patchState(store, {
          error: QUERY_TOO_LONG,
          status: retained ? 'ready' : 'error',
          stale: false,
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
          browseCapped: denied ? false : store.browseCapped(),
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
        const namedNode = raw['node_id'] ?? '';
        // A malformed value would be dropped from the request and widen it to the whole scope,
        // which is the opposite of what the link asked for: refuse it as its own scope instead.
        const invalidLinkField =
          namedItem !== '' && !isCaseUuid(namedItem)
            ? 'item'
            : namedNode !== '' && !isCaseUuid(namedNode)
              ? 'node'
              : null;
        const invalidLink = invalidLinkField !== null;
        const refused = invalidLinkField === 'item' ? INVALID_ITEM : INVALID_NODE;
        const scopeKey = JSON.stringify([
          projectId,
          scope,
          invalidLink ? [namedItem, namedNode] : null,
        ]);
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
          invalidLinkField,
          error: invalidLink ? refused : null,
          status: invalidLink ? 'error' : 'loading',
          failedRead: null,
        });
        await read(false);
      },
      async reload(): Promise<void> {
        // Explicit refresh restarts the walk; keeping a page cursor would drop the pages before it.
        // The cap notice stays until the replacement page lands, so a failed refresh does not
        // describe the same retained rows as an uncapped list.
        patchState(store, { routeCursor: null });
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
