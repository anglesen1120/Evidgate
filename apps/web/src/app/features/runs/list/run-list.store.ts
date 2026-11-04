// The list is a bounded cursor walk; refName is the only server-side filter exposed here.
// Null list decisions are unavailable, not evidence of a pending evaluation.
// Project-scoped instances discard superseded reads so scope changes cannot retain stale rows.

import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { RunPage, RunSummary } from '@evidgate/sdk';

import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { RunDataService } from '../data/run-data.service';

/** Rows per request (the wire's default page size, 00 §9.1). */
export const RUN_LIST_PAGE_LIMIT = 50;

/** Bound automatic top-ups so an unmatched filter cannot walk the entire project. */
export const RUN_LIST_AUTO_PAGES = 5;

/** Stop at the row bound even when the server returns larger pages. */
export const RUN_LIST_AUTO_ROWS = 1_000;

/** One run state filter value: one of the wire's seven, or `''` for "every state". */
export type RunStateFilter = RunSummary['state'] | '';

/** Only explicit verdicts can be filtered; a missing list decision is not pending. */
export type RunDecisionFilter = 'PASS' | 'FAIL' | 'INCONCLUSIVE' | '';

/** Labels are independent; original requires no parent and neither label. */
export type RunScopeFilter = '' | 'original' | 'rerun' | 'reprocess';

/** Only refName is sent to the server; other filters apply to loaded rows. */
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
const RUN_DECISIONS: readonly RunDecisionFilter[] = ['PASS', 'FAIL', 'INCONCLUSIVE'];

/** The scope values `init` accepts in the URL. */
const RUN_SCOPES: readonly RunScopeFilter[] = ['original', 'rerun', 'reprocess'];

/**
 * The longest `ref_name` the contract accepts (1..255 code points); a longer value in the URL is
 * dropped rather than sent, so a pasted string cannot turn the whole read into a `422`.
 */
const REF_NAME_MAX_CODE_POINTS = 255;

/** Control characters are the one class the server refuses in a `ref_name` (00 §7, 05.2.2). */
const CONTROL_CHARACTER = /\p{Cc}/u;

/** The store's state. Mutable by design: `patchState` replaces members, never mutates them. */
export interface RunListState {
  /** The applied filters; `setFilters` replaces the object, never mutates it. */
  filters: RunListFilters;
  /** Every row loaded so far, in the server's order; `visibleRows` is the filtered view. */
  rows: readonly RunSummary[];
  /** The cursor the next page needs, or `null` when the server stopped naming one. */
  nextCursor: string | null;
  /** The list's read state. */
  status: RunListStatus;
  /** The server's typed failure for the first page; `null` when it never failed. */
  loadError: ApiError | null;
  /** The server's typed failure for the last followed page, if any. */
  pagingError: ApiError | null;
  /**
   * True while a followed page is on its way; the page's paging controls stay enabled meanwhile
   * (a disabled control cannot hold focus) and this flag is what refuses a second follow.
   */
  loadingMore: boolean;
  /**
   * True when a trigger stopped at its page/row bound with a cursor still named and the filtered
   * view still short (header 3). `hasMore` stays true, so the page offers its control and its
   * "filtered from N loaded runs" note instead of presenting a short list as the whole project.
   */
  autoLoadCapped: boolean;
  /**
   * True when the last submitted ref name was refused (over 255 code points, or carrying a control
   * character). The value narrowed nothing — `filters.refName` is `''` and `toQuery` writes no
   * `ref` — so the page can explain the refusal instead of showing an unfiltered list as if the
   * field had been ignored.
   */
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
  const decision = query['decision'];
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

/** The request query of one read: the page size, plus `refName` when the screen holds one. */
function pageQuery(filters: RunListFilters): { limit: number; refName?: string } {
  return filters.refName === ''
    ? { limit: RUN_LIST_PAGE_LIMIT }
    : { limit: RUN_LIST_PAGE_LIMIT, refName: filters.refName };
}

// Scope labels are independent: one run may match both rerun and reprocess.
function matchesFilters(filters: RunListFilters, row: RunSummary): boolean {
  if (filters.state !== '' && row.state !== filters.state) {
    return false;
  }
  if (filters.decision !== '' && row.decision !== filters.decision) {
    return false;
  }
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
    status: 'idle',
    loadError: null,
    pagingError: null,
    loadingMore: false,
    autoLoadCapped: false,
    refRejected: false,
  }),
  withComputed(({ rows, nextCursor, status, filters, autoLoadCapped, loadingMore }) => {
    const visibleRows = computed(() => visibleOf(filters(), rows()));
    return {
      /** The rows the table renders: every loaded row that passes the client-side filters. */
      visibleRows,
      /** How many rows are loaded — the "filtered from N loaded runs" number. */
      loadedCount: computed(() => rows().length),
      /**
       * True when the server named another page. A capped trigger keeps this true on purpose
       * (header 3): the next page exists, the store just stopped walking on its own.
       */
      hasMore: computed(() => nextCursor() !== null),
      /**
       * True when a ready list has nothing to show, filtered or not — and no page is in flight.
       *
       * The `loadingMore` term matters: after the first page of a filtered read lands, the rows are
       * published as `ready` and the top-up walk starts, so a filtered view of a first page can be
       * empty while the walk is still fetching the rows that match. Without the term the empty
       * pattern would flash for that window and then be replaced by the table.
       */
      isEmpty: computed(() => status() === 'ready' && !loadingMore() && visibleRows().length === 0),
      /**
       * True when the filters narrow the loaded rows — the page must not present a short filtered
       * view as "this project has no runs".
       */
      isFiltered: computed(() => {
        const applied = filters();
        return (
          applied.state !== '' ||
          applied.decision !== '' ||
          applied.scope !== '' ||
          applied.refName !== ''
        );
      }),
      /** The shared name for the bounded trigger (header 3); the same signal as `autoLoadCapped`. */
      browseCapped: autoLoadCapped,
    };
  }),
  withMethods((store) => {
    const data = inject(RunDataService);

    /** Bumped by every restart; an answer of an older generation publishes nothing (header 4). */
    let newestRead = 0;
    /** The project the rows belong to; `setFilters`/`loadMore` read it from here. */
    let ownerProject: string | null = null;
    /** The cursors this list already followed since the last restart (header 2). */
    const followedCursors = new Set<string>();

    // Automatic top-ups stop at one page of matches; an explicit click always follows once.
    // Superseded walks stop publishing, and repeated cursors remain contract errors.
    async function fill(projectId: string, sequence: number, forceFirst = false): Promise<void> {
      let pages = 0;
      let scanned = 0;
      let capped = false;
      for (;;) {
        const cursor = store.nextCursor();
        if (cursor === null) {
          break;
        }
        if (!(forceFirst && pages === 0 && scanned === 0)) {
          if (visibleOf(store.filters(), store.rows()).length >= RUN_LIST_PAGE_LIMIT) {
            break;
          }
          if (pages >= RUN_LIST_AUTO_PAGES || scanned >= RUN_LIST_AUTO_ROWS) {
            capped = true;
            break;
          }
        }
        if (followedCursors.has(cursor)) {
          patchState(store, { loadingMore: false });
          throw new Error(`the run list repeated cursor "${cursor}"`);
        }
        pages += 1;
        patchState(store, { loadingMore: true, pagingError: null });

        let page: RunPage;
        try {
          page = await data.listRuns(projectId, { ...pageQuery(store.filters()), cursor });
        } catch (error) {
          if (sequence !== newestRead) {
            return;
          }
          if (!isApiRequestError(error)) {
            patchState(store, { loadingMore: false });
            throw error;
          }
          // The cursor stays unrecorded, so the control that shows `pagingError` can retry the
          // SAME page instead of tripping the repeated-cursor refusal below.
          patchState(store, { loadingMore: false, pagingError: error.error });
          return;
        }
        if (sequence !== newestRead) {
          return;
        }
        scanned += page.items.length;
        // Recorded only once the page is in hand: a refused cursor is not a followed one.
        followedCursors.add(cursor);
        patchState(store, {
          loadingMore: false,
          pagingError: null,
          rows: [...store.rows(), ...page.items],
          nextCursor: page.next_cursor,
        });
      }
      if (sequence !== newestRead) {
        return;
      }
      patchState(store, { autoLoadCapped: capped });
    }

    // Clear prior-filter rows before reading so old results never appear under new filters.
    async function restart(): Promise<void> {
      newestRead += 1;
      const sequence = newestRead;
      followedCursors.clear();
      patchState(store, {
        status: 'loading',
        loadError: null,
        rows: NO_ROWS,
        nextCursor: null,
        pagingError: null,
        loadingMore: false,
        autoLoadCapped: false,
      });

      const projectId = ownerProject;
      if (projectId === null) {
        patchState(store, { status: 'ready' });
        return;
      }

      let page: RunPage;
      try {
        page = await data.listRuns(projectId, pageQuery(store.filters()));
      } catch (error) {
        if (sequence !== newestRead) {
          return;
        }
        patchState(store, {
          status: 'error',
          loadError: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
          rows: NO_ROWS,
          nextCursor: null,
        });
        return;
      }
      if (sequence !== newestRead) {
        return;
      }
      patchState(store, {
        status: 'ready',
        rows: page.items,
        nextCursor: page.next_cursor,
      });
      await fill(projectId, sequence);
    }

    return {
      // A URL change replaces paging state and clears any previous form validation failure.
      async init(
        projectId: string,
        query: Record<string, string | null | undefined> = {},
      ): Promise<void> {
        ownerProject = projectId;
        patchState(store, { filters: filtersFromQuery(query), refRejected: false });
        await restart();
      },

      // Server-filter changes restart paging; local filters reuse rows and top up within bounds.
      async setFilters(patch: Partial<RunListFilters>): Promise<void> {
        const next = { ...store.filters(), ...patch };
        // The form path takes the value as typed: it is trimmed, and dropped (with `refRejected`
        // set) when the contract refuses it, so a pasted string cannot turn the read into a `422`
        // and the field is not silently ignored either. `toQuery` writes no `ref` afterwards.
        const ref = normalizeRefName(next.refName);
        const applied: RunListFilters = { ...next, refName: ref.refName };
        if (applied.refName !== store.filters().refName) {
          patchState(store, { filters: applied, refRejected: ref.rejected });
          await restart();
          return;
        }
        if (ref.rejected) {
          // The refused value narrowed nothing, so the applied filter did not change and there is
          // nothing to re-read: only the form's report changes.
          patchState(store, { refRejected: true });
          return;
        }
        // Reaching here means the value equals the stored, already-accepted ref filter and was not
        // refused, so a report left by an earlier submission is cleared — emptying the field or
        // "Clear filters" must not leave an error beside an empty input.
        patchState(store, { filters: applied, refRejected: false, autoLoadCapped: false });
        const projectId = ownerProject;
        // A page already in flight keeps the walk: it re-reads these filters on its next iteration,
        // so starting a second one would request the same cursor twice and append the page twice.
        if (projectId === null || store.status() !== 'ready' || store.loadingMore()) {
          return;
        }
        await fill(projectId, newestRead);
      },

      // Each explicit click receives a fresh bound, allowing deliberate browsing after a cap.
      async loadMore(): Promise<void> {
        const projectId = ownerProject;
        if (
          projectId === null ||
          store.status() !== 'ready' ||
          store.loadingMore() ||
          store.nextCursor() === null
        ) {
          return;
        }
        patchState(store, { autoLoadCapped: false });
        await fill(projectId, newestRead, true);
      },

      /** Re-reads the first page of the current filters — the screen's Retry. */
      async reload(): Promise<void> {
        await restart();
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
          query['decision'] = filters.decision;
        }
        if (filters.scope !== '') {
          query['scope'] = filters.scope;
        }
        if (filters.refName !== '') {
          query['ref'] = filters.refName;
        }
        return query;
      },
    };
  }),
);
