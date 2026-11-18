import { computed, inject } from '@angular/core';
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
import { SessionStore } from '../../core/session/session.store';
import { takeKeysetPage, type KeysetRefusals } from '../../shared/keyset-walk';

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
/** One row's owner change: in flight, or the server's typed refusal of the last attempt. */
export interface CaseOwnerOp {
  readonly pending: boolean;
  readonly failure: ApiError | null;
}
interface CaseListState {
  readonly projectId: string | null;
  /** The server state a page belongs to: filters and page size, never the cursor. */
  readonly query: CaseListQuery;
  readonly search: string;
  readonly phase: 'loading' | 'ready' | 'error';
  readonly loadError: ApiError | null;
  readonly rows: readonly CaseDto[];
  readonly nextCursor: string | null;
  readonly paging: boolean;
  readonly pagingError: ApiError | null;
  readonly ownerOps: Readonly<Record<string, CaseOwnerOp>>;
}
const NO_CASES: readonly CaseDto[] = [];
const NO_OWNER_OPS: Readonly<Record<string, CaseOwnerOp>> = {};
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
/** The PATCH is refused before it is sent: the caller lacks `case:write`. */
const WRITE_FORBIDDEN: ApiError = {
  kind: 'forbidden',
  status: 403,
  code: 'case_write_forbidden',
  message: $localize`:@@catalog.owner-forbidden:Your role cannot change a case owner.`,
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
/** `UpdateCaseRequest.owner_membership_id` is a string: the contract has no unassign. */
const UNASSIGN_UNSUPPORTED: ApiError = {
  kind: 'validation',
  status: 0,
  code: 'owner_required',
  message: $localize`:@@catalog.owner-required:A case owner is required; the contract cannot clear it.`,
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
const PAGING_REFUSALS: KeysetRefusals = {
  cursorRepeated: {
    kind: 'unexpected',
    status: 0,
    code: 'cursor_repeated',
    message: $localize`:@@catalog.cursor-repeated:The catalog handed back the page it was asked for. Reload the list.`,
    requestId: null,
    retryable: true,
    details: [],
    retryAfterSeconds: null,
  },
  pageWithoutNewRows: {
    kind: 'unexpected',
    status: 0,
    code: 'page_without_new_rows',
    message: $localize`:@@catalog.page-repeated:The next catalog page held no new case. Reload the list.`,
    requestId: null,
    retryable: true,
    details: [],
    retryAfterSeconds: null,
  },
};

function typedFailure(error: unknown): ApiError {
  return isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE;
}

function sameServerState(left: CaseListQuery, right: CaseListQuery): boolean {
  return (
    left.limit === right.limit && left.status === right.status && left.framework === right.framework
  );
}

/** The page size a `?limit=` value names, or the default when absent or unsupported. */
function pageSizeFrom(raw: string | undefined): CasePageSize {
  const parsed = Number(raw ?? '');
  return CASE_PAGE_SIZES.find((size) => size === parsed) ?? DEFAULT_CASE_PAGE_SIZE;
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
    paging: false,
    pagingError: null,
    ownerOps: NO_OWNER_OPS,
  }),
  withProps((store, data = inject(CaseListDataService), session = inject(SessionStore)) => {
    /** Bumped by every reset: a read of an older generation never writes state. */
    let generation = 0;
    let inFlight: AbortController | null = null;
    /** Keys the loaded rows already show, so a shifted page cannot repeat one. */
    let seenIds = new Set<string>();
    const ownerSnapshots = new Map<string, CaseDto>();

    const applyOwnerOp = (caseId: string, op: CaseOwnerOp): void => {
      patchState(store, { ownerOps: { ...store.ownerOps(), [caseId]: op } });
    };
    const replaceRow = (caseId: string, row: CaseDto): void => {
      const rows = store.rows();
      const index = rows.findIndex((candidate) => candidate.id === caseId);
      if (index < 0) return;
      patchState(store, {
        rows: rows.map((candidate, position) => (position === index ? row : candidate)),
      });
    };
    const startLoad = (projectId: string, query: CaseListQuery): void => {
      generation += 1;
      const sequence = generation;
      inFlight?.abort();
      const controller = new AbortController();
      inFlight = controller;
      seenIds = new Set<string>();
      ownerSnapshots.clear();
      patchState(store, {
        projectId,
        query,
        phase: 'loading',
        loadError: null,
        rows: NO_CASES,
        nextCursor: null,
        paging: false,
        pagingError: null,
        ownerOps: NO_OWNER_OPS,
      });
      void data.list(projectId, query, controller.signal).then(
        (page) => {
          if (sequence !== generation) return;
          seenIds = new Set(page.items.map((row) => row.id));
          patchState(store, {
            phase: 'ready',
            loadError: null,
            rows: page.items,
            nextCursor: page.next_cursor,
          });
        },
        (error: unknown) => {
          if (sequence !== generation) return;
          patchState(store, {
            phase: 'error',
            loadError: typedFailure(error),
            rows: NO_CASES,
            nextCursor: null,
          });
        },
      );
    };
    const followPage = async (): Promise<void> => {
      const projectId = store.projectId();
      const cursor = store.nextCursor();
      if (projectId === null || cursor === null) return;
      if (store.paging() || store.phase() !== 'ready') return;
      const sequence = generation;
      const controller = new AbortController();
      patchState(store, { paging: true, pagingError: null });
      let page: CaseListResponse;
      try {
        page = await data.list(projectId, { ...store.query(), cursor }, controller.signal);
      } catch (error) {
        if (sequence !== generation) return;
        patchState(store, { paging: false, pagingError: typedFailure(error) });
        return;
      }
      if (sequence !== generation) return;
      const step = takeKeysetPage({
        cursor,
        items: page.items,
        nextCursor: page.next_cursor,
        seen: seenIds,
        keyOf: (row) => row.id,
        refusals: PAGING_REFUSALS,
      });
      if (step.outcome === 'refused') {
        patchState(store, { paging: false, pagingError: step.error });
        return;
      }
      seenIds = step.seen;
      patchState(store, {
        paging: false,
        pagingError: null,
        rows: [...store.rows(), ...step.fresh],
        nextCursor: step.nextCursor,
      });
    };
    const assign = async (caseId: string, membershipId: string | null): Promise<void> => {
      if (!session.has('case:write')) {
        applyOwnerOp(caseId, { pending: false, failure: WRITE_FORBIDDEN });
        return;
      }
      if (membershipId === null) {
        applyOwnerOp(caseId, { pending: false, failure: UNASSIGN_UNSUPPORTED });
        return;
      }
      if (store.ownerOps()[caseId]?.pending) return;
      const row = store.rows().find((candidate) => candidate.id === caseId);
      if (row === undefined) return;
      const sequence = generation;
      ownerSnapshots.set(caseId, row);
      applyOwnerOp(caseId, { pending: true, failure: null });
      replaceRow(caseId, { ...row, owner_membership_id: membershipId });
      try {
        const confirmed = await data.update(
          caseId,
          { owner_membership_id: membershipId },
          new AbortController().signal,
          `"${row.version}"`,
        );
        if (sequence !== generation) return;
        ownerSnapshots.delete(caseId);
        replaceRow(caseId, confirmed);
        applyOwnerOp(caseId, { pending: false, failure: null });
      } catch (error) {
        if (sequence !== generation) return;
        replaceRow(caseId, ownerSnapshots.get(caseId) ?? row);
        ownerSnapshots.delete(caseId);
        applyOwnerOp(caseId, { pending: false, failure: typedFailure(error) });
      }
    };
    return {
      _startLoad: startLoad,
      _followPage: followPage,
      _assignOwner: assign,
      _canAssign: () => session.has('case:write'),
    };
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
        limit: pageSizeFrom(raw['limit'] ?? undefined),
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
    /** Follows the opaque cursor once and appends the page; rows stay visible meanwhile. */
    loadMore(): Promise<void> {
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
    ownerPending(caseId: string): boolean {
      return store.ownerOps()[caseId]?.pending ?? false;
    },
    ownerFailure(caseId: string): ApiError | null {
      return store.ownerOps()[caseId]?.failure ?? null;
    },
  })),
);
