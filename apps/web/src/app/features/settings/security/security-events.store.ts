import { computed, inject, linkedSignal, resource } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import {
  SecurityEventsDataService,
  type SecurityEventsQuery,
} from '../../../core/data/security-events-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { securityEventVisible } from './security-events.config';

export interface SecurityFilters {
  readonly projectId: string;
  readonly type: NonNullable<SecurityEventsQuery['type']> | '';
  readonly severity: NonNullable<SecurityEventsQuery['severity']> | '';
  readonly from: string;
  readonly to: string;
}
export const EMPTY_SECURITY_FILTERS: SecurityFilters = {
  projectId: '',
  type: '',
  severity: '',
  from: '',
  to: '',
};
const MAX_CURSOR_PAGES = 200;
const UNRESOLVED: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message: $localize`:@@settings.security.unresolved:The request did not complete. Try again.`,
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

function queryFor(
  filters: SecurityFilters,
  limit: number,
  cursor: string | undefined,
): SecurityEventsQuery {
  return {
    limit,
    ...(cursor ? { cursor } : {}),
    ...(filters.projectId ? { project_id: filters.projectId } : {}),
    ...(filters.type ? { type: filters.type } : {}),
    ...(filters.severity ? { severity: filters.severity } : {}),
    ...(filters.from ? { from: filters.from } : {}),
    ...(filters.to ? { to: filters.to } : {}),
  };
}

export const SecurityEventsStore = signalStore(
  withState({ pageSize: 20 }),
  withProps(() => {
    const session = inject(SessionStore);
    return {
      _selection: linkedSignal(() => ({
        orgId: session.currentOrg()?.id ?? null,
        filters: EMPTY_SECURITY_FILTERS,
        pageIndex: 0,
        cursors: [undefined] as readonly (string | undefined)[],
      })),
    };
  }),
  withProps((store) => {
    const data = inject(SecurityEventsDataService);
    return {
      _page: resource({
        params: () => {
          const selection = store._selection();
          return selection.orgId
            ? {
                selection,
                pageSize: store.pageSize(),
                orgId: selection.orgId,
                query: queryFor(
                  selection.filters,
                  store.pageSize(),
                  selection.cursors[selection.pageIndex],
                ),
              }
            : undefined;
        },
        loader: async ({ params, abortSignal }) => ({
          selection: params.selection,
          pageSize: params.pageSize,
          data: await data.list(params.orgId, params.query, abortSignal),
        }),
      }),
    };
  }),
  withProps((store) => {
    type LoadedPage = ReturnType<typeof store._page.value>;
    type PageSource = {
      selection: ReturnType<typeof store._selection>;
      pageSize: number;
      page: LoadedPage;
    };
    return {
      _displayed: linkedSignal<PageSource, LoadedPage>({
        source: () => ({
          selection: store._selection(),
          pageSize: store.pageSize(),
          page: store._page.hasValue() ? store._page.value() : undefined,
        }),
        computation: (source, previous) => {
          const page = source.page;
          if (
            page &&
            page.selection.orgId === source.selection.orgId &&
            page.selection.filters === source.selection.filters &&
            page.pageSize === source.pageSize
          )
            return page;
          const retained = previous?.value;
          return retained &&
            retained.selection.orgId === source.selection.orgId &&
            retained.selection.filters === source.selection.filters &&
            retained.pageSize === source.pageSize
            ? retained
            : undefined;
        },
      }),
    };
  }),
  withComputed((store) => ({
    orgId: computed(() => store._selection().orgId),
    filters: computed(() => store._selection().filters),
    pageIndex: computed(
      () => store._displayed()?.selection.pageIndex ?? store._selection().pageIndex,
    ),
    hasPage: computed(() => store._displayed() !== undefined),
    rawCount: computed(() => store._displayed()?.data.items.length ?? 0),
    rows: computed(() =>
      (store._displayed()?.data.items ?? []).filter((row) => securityEventVisible(row.type)),
    ),
    nextCursor: computed(() => store._displayed()?.data.next_cursor ?? null),
    phase: computed<'loading' | 'ready' | 'error'>(() =>
      !store._selection().orgId || store._page.isLoading()
        ? 'loading'
        : store._page.error()
          ? 'error'
          : 'ready',
    ),
    loadError: computed(() => {
      const error = store._page.error();
      return error ? (isApiRequestError(error) ? error.error : UNRESOLVED) : null;
    }),
    filtered: computed(() =>
      Object.values(store._selection().filters).some((value) => value !== ''),
    ),
    capped: computed(
      () =>
        (store._displayed()?.selection.pageIndex ?? store._selection().pageIndex) + 1 >=
        MAX_CURSOR_PAGES,
    ),
    canNext: computed(() => {
      const selection = store._displayed()?.selection ?? store._selection();
      const cursor = store._displayed()?.data.next_cursor;
      return (
        !!cursor &&
        selection.pageIndex + 1 < MAX_CURSOR_PAGES &&
        !selection.cursors.slice(0, selection.pageIndex + 1).includes(cursor)
      );
    }),
  })),
  withMethods((store) => ({
    apply(filters: SecurityFilters): void {
      store._selection.update((value) => ({
        ...value,
        filters: { ...filters },
        pageIndex: 0,
        cursors: [undefined],
      }));
    },
    setPageSize(pageSize: number): void {
      if (![20, 50, 100].includes(pageSize) || pageSize === store.pageSize()) return;
      patchState(store, { pageSize });
      store._selection.update((value) => ({ ...value, pageIndex: 0, cursors: [undefined] }));
    },
    next(): void {
      const cursor = store.nextCursor();
      if (!cursor || store.phase() !== 'ready' || !store.canNext()) return;
      store._selection.update((value) => ({
        ...value,
        cursors: [...value.cursors.slice(0, value.pageIndex + 1), cursor],
        pageIndex: value.pageIndex + 1,
      }));
    },
    previous(): void {
      if (store.pageIndex() > 0 && store.phase() === 'ready')
        store._selection.update((value) => ({ ...value, pageIndex: value.pageIndex - 1 }));
    },
    reload(): void {
      store._page.reload();
    },
  })),
);
