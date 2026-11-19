import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import type { CaseDto } from '@evidgate/sdk';
import type { ApiError } from '../../core/http/api-error';
import { CaseListDataService, type CaseListQuery } from '../../core/data/case-list-data.service';
import { SessionStore } from '../../core/session/session.store';
import { createCaseOwner, type CaseOwnerOp } from './case-list-owner';
import { createCasePaging, pageSizeFrom, sameServerState } from './case-list-paging';

export type { CaseOwnerOp } from './case-list-owner';

export interface CaseListFilters {
  readonly status: CaseDto['status'] | '';
  readonly framework: string;
  readonly search: string;
}
export const EMPTY_CASE_FILTERS: CaseListFilters = { status: '', framework: '', search: '' };
export type CatalogQuery = Record<string, string | null | undefined>;
/** The page sizes the contract's `limit` and the UI26 page-size control both offer. */
export const CASE_PAGE_SIZES = [25, 50, 100, 200] as const;
export type CasePageSize = (typeof CASE_PAGE_SIZES)[number];
export const DEFAULT_CASE_PAGE_SIZE: CasePageSize = 50;
interface CaseListState {
  readonly projectId: string | null;
  /** The server state a page belongs to: filters and page size, never the cursor. */
  readonly query: CaseListQuery;
  readonly search: string;
  readonly phase: 'loading' | 'ready' | 'error';
  readonly loadError: ApiError | null;
  readonly rows: readonly CaseDto[];
  readonly nextCursor: string | null;
  readonly browseCapped: boolean;
  readonly paging: boolean;
  readonly pagingError: ApiError | null;
  readonly ownerOps: Readonly<Record<string, CaseOwnerOp>>;
}
const NO_CASES: readonly CaseDto[] = [];
const NO_OWNER_OPS: Readonly<Record<string, CaseOwnerOp>> = {};

/** The page size a `?limit=` value names, or the default when absent or unsupported. */
function acceptedPageSize(raw: string | undefined): CasePageSize {
  const size = pageSizeFrom(raw, DEFAULT_CASE_PAGE_SIZE);
  return CASE_PAGE_SIZES.find((candidate) => candidate === size) ?? DEFAULT_CASE_PAGE_SIZE;
}

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
  withState<CaseListState>({
    projectId: null,
    query: { limit: DEFAULT_CASE_PAGE_SIZE },
    search: '',
    phase: 'loading',
    loadError: null,
    rows: NO_CASES,
    nextCursor: null,
    browseCapped: false,
    paging: false,
    pagingError: null,
    ownerOps: NO_OWNER_OPS,
  }),
  withProps((store, data = inject(CaseListDataService), session = inject(SessionStore)) => {
    const paging = createCasePaging(
      {
        projectId: () => store.projectId(),
        query: () => store.query(),
        phase: () => store.phase(),
        nextCursor: () => store.nextCursor(),
        paging: () => store.paging(),
        rows: () => store.rows(),
        patch: (patch) => patchState(store, patch),
      },
      data,
    );
    const owner = createCaseOwner(
      {
        rows: () => store.rows(),
        ownerOps: () => store.ownerOps(),
        epoch: () => paging.epoch(),
        canWrite: () => session.has('case:write'),
        patchOwnerOps: (ownerOps) => patchState(store, { ownerOps }),
        replaceRow: (caseId, row) => {
          const rows = store.rows();
          const index = rows.findIndex((candidate) => candidate.id === caseId);
          if (index < 0) return;
          patchState(store, {
            rows: rows.map((candidate, position) => (position === index ? row : candidate)),
          });
        },
      },
      data,
    );
    return {
      _startLoad: (projectId: string, query: CaseListQuery): void => {
        owner.reset();
        paging.start(projectId, query);
      },
      _followPage: () => paging.follow(),
      _reloadOwner: (caseId: string) => owner.reload(caseId),
      _abortReads: (): void => paging.abort(),
      _assignOwner: (caseId: string, membershipId: string | null) =>
        owner.assign(caseId, membershipId),
      _canAssign: () => session.has('case:write'),
    };
  }),
  withHooks({
    onDestroy(store) {
      store._abortReads();
    },
  }),
  withComputed((store) => ({
    status: computed<'loading' | 'ready' | 'error'>(() => store.phase()),
    pageSize: computed(() => store.query().limit),
    canAssign: computed(() => store._canAssign()),
    hasMore: computed(() => store.phase() === 'ready' && store.nextCursor() !== null),
    loadingMore: computed(() => store.paging()),
    loadMoreFailure: computed(() => store.pagingError()),
    filters: computed<CaseListFilters>(() => ({
      status: store.query().status ?? '',
      framework: store.query().framework ?? '',
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
    /**
     * Applies the route's server filters and page size (never a cursor: pages accumulate).
     * A route that only changes the local search keeps the loaded rows and the cursor.
     */
    init(projectId: string, raw: CatalogQuery): void {
      const filters = caseFiltersFromQuery(raw);
      const query: CaseListQuery = {
        limit: acceptedPageSize(raw['limit'] ?? undefined),
        ...(filters.status ? { status: filters.status } : {}),
        ...(filters.framework ? { framework: filters.framework } : {}),
      };
      const current = store.projectId();
      const unchanged =
        current === projectId && store.phase() !== 'error' && sameServerState(store.query(), query);
      patchState(store, { search: filters.search });
      if (!unchanged) store._startLoad(projectId, query);
    },
    /** Re-reads the first page of the current server state, keeping the local search text. */
    reload(): void {
      const projectId = store.projectId();
      if (projectId === null) return;
      store._startLoad(projectId, store.query());
    },
    /**
     * Follows the opaque cursor once and appends the page; rows stay visible meanwhile.
     * Resolves `true` only when the appended rows are the ones in state: a reset, a filter
     * change or a refusal in between reports `false` so the caller moves no focus.
     */
    loadMore(): Promise<boolean> {
      return store._followPage();
    },
    /** A supported page size resets the cursor and the loaded rows, as UI26 requires. */
    setPageSize(size: number): void {
      const projectId = store.projectId();
      if (projectId === null || size === store.query().limit) return;
      const accepted = CASE_PAGE_SIZES.find((candidate) => candidate === size);
      if (accepted === undefined) return;
      store._startLoad(projectId, { ...store.query(), limit: accepted });
    },
    assignOwner(caseId: string, membershipId: string | null): Promise<void> {
      return store._assignOwner(caseId, membershipId);
    },
    /** Re-reads one case after a failed write; the other loaded rows and pages stay. */
    reloadOwner(caseId: string): Promise<void> {
      return store._reloadOwner(caseId);
    },
    ownerPending(caseId: string): boolean {
      return store.ownerOps()[caseId]?.pending ?? false;
    },
    ownerFailure(caseId: string): ApiError | null {
      return store.ownerOps()[caseId]?.failure ?? null;
    },
  })),
);
