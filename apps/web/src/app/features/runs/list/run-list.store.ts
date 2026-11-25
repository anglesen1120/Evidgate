// Scope narrows only the current server page; State, ref and decision travel on every request.

import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { RunPage, RunSummary } from '@evidgate/sdk';

import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { RunDataService, type RunListQuery } from '../data/run-data.service';

export const RUN_LIST_PAGE_LIMIT = 20;
const PAGE_SIZES = [20, 50, 100];

/** One run state filter value: one of the wire's seven, or `''` for "every state". */
export type RunStateFilter = RunSummary['state'] | '';

/** Pending selects runs without a live evaluation; empty selects every decision. */
export type RunDecisionFilter = NonNullable<RunListQuery['decision']> | '';

/** Labels are independent; original requires no parent and neither label. */
export type RunScopeFilter = '' | 'original' | 'rerun' | 'reprocess';

/** Scope filters loaded rows; State, refName and decision filter the server list. */
export interface RunListFilters {
  readonly state: RunStateFilter;
  readonly decision: RunDecisionFilter;
  readonly scope: RunScopeFilter;
  readonly refName: string;
}

/** No filter at all — a project's whole run list. */
export const EMPTY_RUN_LIST_FILTERS: RunListFilters = {
  state: '',
  decision: '',
  scope: '',
  refName: '',
};

/** The list's read state: `idle` before the first read, which the page renders as loading. */
export type RunListStatus = 'idle' | 'loading' | 'ready' | 'error';

/** The states the wire declares, as a value list `init` can validate a URL against. */
const RUN_STATES: readonly RunSummary['state'][] = [
  'created',
  'queued',
  'running',
  'collecting',
  'complete',
  'incomplete',
  'canceled',
];

/** The decision values `init` accepts in the URL. */
const RUN_DECISIONS: readonly RunDecisionFilter[] = ['pending', 'PASS', 'FAIL', 'INCONCLUSIVE'];

/** The scope values `init` accepts in the URL. */
const RUN_SCOPES: readonly RunScopeFilter[] = ['original', 'rerun', 'reprocess'];

/**
 * The longest `ref_name` the contract accepts (1..255 code points); a longer value in the URL is
 * dropped rather than sent, so a pasted string cannot turn the whole read into a `422`.
 */
const REF_NAME_MAX_CODE_POINTS = 255;

/** Control characters are the one class the server refuses in a `ref_name` (00 §7, 05.2.2). */
const CONTROL_CHARACTER = /\p{Cc}/u;

export interface RunListState {
  filters: RunListFilters;
  rows: readonly RunSummary[];
  nextCursor: string | null;
  requestCursors: readonly (string | null)[];
  page: number;
  pageSize: number;
  status: RunListStatus;
  loadError: ApiError | null;
  pagingError: ApiError | null;
  busy: boolean;
  refRejected: boolean;
}

const NO_ROWS: readonly RunSummary[] = [];

/**
 * The fixed failure reported when a rejection is not the server's answer — an adapter
 * cancellation or a bug. Same shape and reasoning as `AuditStore`'s constant: `unexpected` is the
 * classification table's "no UI state for this", `status: 0` says no response exists, and no text is
 * taken from the thrown value.
 */
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message: 'The request did not complete, so nothing was confirmed. Try again.',
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The URL query as the filter set: a value that is not one of the declared members is dropped, so a
 * stale or hand-edited link narrows nothing instead of being sent (a `state=nope` would be a `422`)
 * or silently rendered as a filter the screen does not have.
 */
function filtersFromQuery(query: Record<string, string | null | undefined>): RunListFilters {
  const state = query['state'];
  const decision = query['decision'] === 'fail' ? 'FAIL' : query['decision'];
  const scope = query['scope'];

  return {
    state: RUN_STATES.includes(state as RunSummary['state']) ? (state as RunStateFilter) : '',
    decision: RUN_DECISIONS.includes(decision as RunDecisionFilter)
      ? (decision as RunDecisionFilter)
      : '',
    scope: RUN_SCOPES.includes(scope as RunScopeFilter) ? (scope as RunScopeFilter) : '',
    // A link is not a form submission, so a refused value narrows nothing without a message.
    refName: normalizeRefName(query['ref']).refName,
  };
}

/** True for a `ref_name` the contract accepts: 1..255 code points, no control character. */
function isValidRefName(value: string | null | undefined): value is string {
  if (typeof value !== 'string' || value === '') {
    return false;
  }
  // `[…value]` counts code points, not UTF-16 units, which is what the contract's "1..255 code
  // points" means; format characters (Cf) are legal and deliberately not rejected here.
  if ([...value].length > REF_NAME_MAX_CODE_POINTS) {
    return false;
  }
  return !CONTROL_CHARACTER.test(value);
}

/**
 * The ref filter a submitted (or URL-carried) value stands for.
 *
 * The value is trimmed first, because surrounding whitespace is never part of a ref name and a
 * pasted value carries it; then it is accepted only when {@link isValidRefName} accepts it. An
 * invalid value narrows nothing — `refName: ''`, so it is neither sent nor written to the URL — and
 * `rejected` says that it was refused, so the form can explain why instead of looking as if the
 * field had been ignored. A blank value is not a rejection: clearing the field is a valid request
 * to stop filtering by ref.
 */
function normalizeRefName(value: string | null | undefined): {
  refName: string;
  rejected: boolean;
} {
  const trimmed = typeof value === 'string' ? value.trim() : '';
  if (trimmed === '') {
    return { refName: '', rejected: false };
  }
  return isValidRefName(trimmed)
    ? { refName: trimmed, rejected: false }
    : { refName: '', rejected: true };
}

/** Server filters travel with every page, including short or empty cursor pages. */
function pageQuery(filters: RunListFilters, limit: number): RunListQuery {
  return {
    limit,
    ...(filters.state === '' ? {} : { state: filters.state }),
    ...(filters.refName === '' ? {} : { refName: filters.refName }),
    ...(filters.decision === '' ? {} : { decision: filters.decision }),
  };
}

// Scope labels are independent: one run may match both rerun and reprocess.
function matchesFilters(filters: RunListFilters, row: RunSummary): boolean {
  if (filters.scope !== '') {
    const hasRerun = typeof row.rerun_label === 'string' && row.rerun_label !== '';
    const hasReprocess = typeof row.reprocess_label === 'string' && row.reprocess_label !== '';
    const isOriginal =
      (row.parent_run_id == null || row.parent_run_id === '') && !hasRerun && !hasReprocess;
    const inScope =
      filters.scope === 'rerun'
        ? hasRerun
        : filters.scope === 'reprocess'
          ? hasReprocess
          : isOriginal;
    if (!inScope) {
      return false;
    }
  }
  return true;
}

/** The filtered view of `rows`, the one thing every filter case is measured on. */
function visibleOf(filters: RunListFilters, rows: readonly RunSummary[]): RunSummary[] {
  return rows.filter((row) => matchesFilters(filters, row));
}

export const RunListStore = signalStore(
  withState<RunListState>({
    filters: EMPTY_RUN_LIST_FILTERS,
    rows: NO_ROWS,
    nextCursor: null,
    requestCursors: [null],
    page: 1,
    pageSize: RUN_LIST_PAGE_LIMIT,
    status: 'idle',
    loadError: null,
    pagingError: null,
    busy: false,
    refRejected: false,
  }),
  withComputed(({ rows, nextCursor, status, filters }) => ({
    visibleRows: computed(() => visibleOf(filters(), rows())),
    loadedCount: computed(() => rows().length),
    hasNext: computed(() => nextCursor() !== null),
    isEmpty: computed(() => status() === 'ready' && visibleOf(filters(), rows()).length === 0),
    isFiltered: computed(() => Object.values(filters()).some(Boolean)),
  })),
  withMethods((store) => {
    const data = inject(RunDataService);
    let newestRead = 0;
    let ownerProject: string | null = null;
    let pendingTarget: { page: number; cursor: string | null } | null = null;

    async function read(target: { page: number; cursor: string | null }): Promise<void> {
      const projectId = ownerProject;
      if (projectId === null || store.busy()) return;
      const sequence = ++newestRead;
      pendingTarget = target;
      const first = store.status() !== 'ready';
      patchState(store, { busy: true, pagingError: null, loadError: null });
      const history = [...store.requestCursors().slice(0, target.page - 1), target.cursor];
      let page: RunPage;
      try {
        page = await data.listRuns(projectId, {
          ...pageQuery(store.filters(), store.pageSize()),
          ...(target.cursor === null ? {} : { cursor: target.cursor }),
        });
        if (sequence !== newestRead) return;
        if (page.next_cursor !== null && history.includes(page.next_cursor)) {
          throw new Error('The run list repeated a forward cursor.');
        }
      } catch (error) {
        if (sequence !== newestRead) return;
        const failure = isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE;
        patchState(
          store,
          first
            ? { status: 'error', loadError: failure, rows: NO_ROWS, busy: false }
            : { pagingError: failure, busy: false },
        );
        return;
      }
      if (sequence !== newestRead) return;
      pendingTarget = null;
      patchState(store, {
        status: 'ready',
        rows: page.items,
        nextCursor: page.next_cursor,
        requestCursors: history,
        page: target.page,
        busy: false,
        pagingError: null,
      });
    }

    async function restart(cursor: string | null = null, retainRows = true): Promise<void> {
      newestRead += 1;
      pendingTarget = null;
      patchState(store, {
        status: 'loading',
        loadError: null,
        pagingError: null,
        busy: false,
        rows: retainRows ? store.rows() : NO_ROWS,
        nextCursor: null,
        requestCursors: [cursor],
        page: 1,
      });
      await read({ page: 1, cursor });
    }

    return {
      async init(
        projectId: string,
        query: Record<string, string | null | undefined> = {},
      ): Promise<void> {
        const filters = filtersFromQuery(query);
        const size = Number(query['pageSize']);
        const pageSize = PAGE_SIZES.includes(size) ? size : RUN_LIST_PAGE_LIMIT;
        const cursor = query['cursor'] || null;
        if (
          store.busy() &&
          ownerProject === projectId &&
          pageSize === store.pageSize() &&
          cursor === store.requestCursors()[store.page() - 1] &&
          Object.entries(filters).every(
            ([key, value]) => store.filters()[key as keyof RunListFilters] === value,
          )
        )
          return;
        const sameProject = ownerProject === projectId;
        ownerProject = projectId;
        patchState(store, { filters, pageSize, refRejected: false });
        await restart(cursor, sameProject);
      },

      async setFilters(patch: Partial<RunListFilters>): Promise<void> {
        const next = { ...store.filters(), ...patch };
        const ref = normalizeRefName(next.refName);
        const applied = { ...next, refName: ref.refName };
        const changed = Object.entries(applied).some(
          ([key, value]) => store.filters()[key as keyof RunListFilters] !== value,
        );
        const serverChanged =
          applied.state !== store.filters().state ||
          applied.refName !== store.filters().refName ||
          applied.decision !== store.filters().decision;
        const recoverCursor = store.status() === 'error' && store.requestCursors()[0] !== null;
        const clearCursor =
          patch.state === '' &&
          patch.decision === '' &&
          patch.scope === '' &&
          patch.refName === '' &&
          (store.page() > 1 || store.requestCursors()[0] !== null || store.busy());
        patchState(store, { filters: applied, refRejected: ref.rejected });
        if (
          recoverCursor ||
          clearCursor ||
          (changed &&
            (serverChanged || store.page() > 1 || store.busy() || store.status() !== 'ready'))
        ) {
          await restart();
        }
      },

      async setPageSize(size: number): Promise<void> {
        if (!PAGE_SIZES.includes(size)) return;
        if (size === store.pageSize() && store.page() === 1) return;
        patchState(store, { pageSize: size });
        await restart();
      },

      async next(): Promise<void> {
        const cursor = store.nextCursor();
        if (store.status() !== 'ready' || cursor === null) return;
        await read({ page: store.page() + 1, cursor });
      },

      async previous(): Promise<void> {
        if (store.status() !== 'ready' || store.page() === 1) return;
        await read({
          page: store.page() - 1,
          cursor: store.requestCursors()[store.page() - 2] ?? null,
        });
      },

      async retryPage(): Promise<void> {
        if (pendingTarget !== null) await read(pendingTarget);
      },

      async reload(): Promise<void> {
        if (store.busy()) return;
        const cursor = store.requestCursors()[store.page() - 1] ?? null;
        await restart(store.status() === 'error' ? null : cursor);
      },

      /**
       * The filters as URL query members: only the non-empty ones, so an untouched list has an
       * empty query and a shared link carries exactly the narrowing it showed. `refName` is `ref`
       * on the URL; the other keys are the filter names the page's chips use.
       */
      toQuery(): Record<string, string> {
        const filters = store.filters();
        const query: Record<string, string> = {};
        if (filters.state !== '') {
          query['state'] = filters.state;
        }
        if (filters.decision !== '') {
          query['decision'] = filters.decision === 'FAIL' ? 'fail' : filters.decision;
        }
        if (filters.scope !== '') {
          query['scope'] = filters.scope;
        }
        if (filters.refName !== '') {
          query['ref'] = filters.refName;
        }
        const cursor = store.requestCursors()[store.page() - 1];
        if (cursor) query['cursor'] = cursor;
        if (store.pageSize() !== RUN_LIST_PAGE_LIMIT) query['pageSize'] = String(store.pageSize());
        return query;
      },
    };
  }),
);
