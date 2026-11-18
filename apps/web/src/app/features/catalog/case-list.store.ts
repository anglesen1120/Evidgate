import { computed, inject, resource } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import type { CaseDto, CaseListResponse } from '@evidgate/sdk';
import { isApiRequestError, type ApiError } from '../../core/http/api-error';
import { CaseListDataService, type CaseListQuery } from '../../core/data/case-list-data.service';

export interface CaseListFilters {
  readonly status: CaseDto['status'] | '';
  readonly framework: string;
  readonly search: string;
}
export const EMPTY_CASE_FILTERS: CaseListFilters = { status: '', framework: '', search: '' };
export type CatalogQuery = Record<string, string | null | undefined>;
interface CaseListParams {
  readonly projectId: string;
  readonly query: CaseListQuery;
}
type CaseRead =
  | { readonly page: CaseListResponse; readonly failure: null }
  | { readonly page: null; readonly failure: ApiError };
const NO_CASES: readonly CaseDto[] = [];
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message: $localize`:@@catalog.unresolved:The catalog request did not complete. Try again.`,
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

export function caseFiltersFromQuery(query: CatalogQuery): CaseListFilters {
  const status = query['status'];
  return {
    status: status === 'active' || status === 'missing' || status === 'archived' ? status : '',
    framework: query['framework']?.trim() ?? '',
    search: query['q']?.trim() ?? '',
  };
}

export function caseFiltersToQuery(filters: CaseListFilters): Record<string, string> {
  return {
    ...(filters.status ? { status: filters.status } : {}),
    ...(filters.framework ? { framework: filters.framework } : {}),
    ...(filters.search ? { q: filters.search } : {}),
  };
}

export const CaseListStore = signalStore(
  withState({ request: null as CaseListParams | null, search: '' }),
  withProps((store, data = inject(CaseListDataService)) => ({
    // The resource aborts superseded reads and ignores late resolutions, even if an adapter ignores abort.
    _read: resource<CaseRead, CaseListParams | undefined>({
      params: () => store.request() ?? undefined,
      loader: async ({ params, abortSignal }) => {
        try {
          return {
            page: await data.list(params.projectId, params.query, abortSignal),
            failure: null,
          };
        } catch (error) {
          return {
            page: null,
            failure: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
          };
        }
      },
    }),
  })),
  withComputed((store) => ({
    status: computed<'loading' | 'ready' | 'error'>(() => {
      if (!store._read.hasValue() || store._read.isLoading()) return 'loading';
      return store._read.value().failure ? 'error' : 'ready';
    }),
    loadError: computed(() =>
      store._read.hasValue() && !store._read.isLoading() ? store._read.value().failure : null,
    ),
    rows: computed(() =>
      store._read.hasValue() && !store._read.isLoading()
        ? (store._read.value().page?.items ?? NO_CASES)
        : NO_CASES,
    ),
    hasMore: computed(
      () =>
        store._read.hasValue() &&
        !store._read.isLoading() &&
        Boolean(store._read.value().page?.next_cursor),
    ),
    filters: computed<CaseListFilters>(() => ({
      status: store.request()?.query.status ?? '',
      framework: store.request()?.query.framework ?? '',
      search: store.search(),
    })),
  })),
  withComputed((store) => ({
    visibleRows: computed(() => {
      const search = store.search().toLocaleLowerCase();
      return search
        ? store
            .rows()
            .filter((row) =>
              [row.annotation_id, row.id, row.name].some((value) =>
                value.toLocaleLowerCase().includes(search),
              ),
            )
        : store.rows();
    }),
    isFiltered: computed(() => Object.values(store.filters()).some(Boolean)),
  })),
  withComputed((store) => ({ isEmpty: computed(() => store.visibleRows().length === 0) })),
  withMethods((store) => ({
    init(projectId: string, raw: CatalogQuery): void {
      const filters = caseFiltersFromQuery(raw);
      const rawLimit = raw['limit'] ?? '';
      const parsed = /^\d+$/.test(rawLimit) ? Number(rawLimit) : 50;
      const limit = Number.isSafeInteger(parsed) && parsed >= 1 && parsed <= 200 ? parsed : 50;
      const query: CaseListQuery = {
        limit,
        ...(filters.status ? { status: filters.status } : {}),
        ...(filters.framework ? { framework: filters.framework } : {}),
        ...(raw['cursor'] ? { cursor: raw['cursor'] } : {}),
      };
      const previous = store.request();
      // A local search never re-fetches or walks the cursor. Route back/forward restores server state.
      const unchanged =
        previous?.projectId === projectId &&
        previous.query.limit === query.limit &&
        previous.query.status === query.status &&
        previous.query.framework === query.framework &&
        previous.query.cursor === query.cursor;
      patchState(store, {
        search: filters.search,
        ...(unchanged ? {} : { request: { projectId, query } }),
      });
    },
    toQuery(): Record<string, string> {
      const request = store.request()?.query;
      return {
        ...caseFiltersToQuery(store.filters()),
        ...(request?.cursor ? { cursor: request.cursor } : {}),
        ...(request?.limit && request.limit !== 50 ? { limit: String(request.limit) } : {}),
      };
    },
    reload(): void {
      store._read.reload();
    },
  })),
);
