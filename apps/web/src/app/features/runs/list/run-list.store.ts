// 09.3.1-T1 (plan 09.3.1 §2 AC9, §4 runs row, §5 run-list row, §8 "the list is a cursor page",
// §10 T1.1; plan Notes lines 226-228; COORDINATOR-DECISION S101-C1): the Runs list's view state, as
// a `@ngrx/signals` SignalStore — the package the web contract names (`plans/features/README.md`
// §12 table, ADR-0008 (d)), shaped like `features/settings/audit/audit.store.ts`.
//
// One instance per page (`providers: [RunListStore]` on the run-list page, never `providedIn:
// 'root'`): a project switch must not inherit another project's rows, cursor or filters.
//
// What shapes every method:
//
// 1. **`ref_name` is the only server filter.** `GET /v1/projects/{p}/runs` accepts `cursor`,
//    `limit`, `ref_name`, `pr_number`, `head_sha`, `trigger_event` and `is_fork`; only `ref_name` is
//    indexed, so `pr_number`/`head_sha`/`trigger_event`/`is_fork` are not exposed at all (plan Note
//    line 228) and nothing here can send them. `state`, `decision` and the rerun/reprocess scope are
//    URL signals applied to the rows already loaded — never a verdict computation, never a request
//    parameter the server could filter on.
// 2. **The list is a cursor page, never "load all"** (00 §9.1, W7). `init`/`reload` read the first
//    page at `RUN_LIST_PAGE_LIMIT`; `loadMore` follows `next_cursor`. A repeated cursor is a
//    contract violation and a `throw` — the same refusal as `AuditStore` and `MembersStore` — so a
//    broken cursor surfaces as a bug instead of an endless walk.
// 3. **Client-side filters top up, bounded** (COORDINATOR-DECISION S101-C1). A filtered view can
//    be short while the server still names pages, so after a page lands the store keeps following
//    pages while the filtered view is below one page — at most `RUN_LIST_AUTO_PAGES` extra pages or
//    `RUN_LIST_AUTO_ROWS` scanned rows per trigger (a filter change or a `loadMore` click), then it
//    stops and publishes `autoLoadCapped`. That is a bounded list, not an error: `hasMore` stays
//    true so the page can offer its "Load more" control, and `loadedCount` lets it say "filtered
//    from N loaded runs". `browseCapped` is the same signal under the shared name.
// 4. **Newest call wins.** Two reads overlap easily (a project switch, a ref filter applied while
//    the first page is still in flight). Every restart bumps a per-instance sequence, and an answer
//    whose sequence is no longer the newest — success or failure — publishes nothing. A page that
//    arrives after a restart is dropped without being appended.
// 5. **The server's failure is the server's.** A typed `ApiError` reaches `loadError` (or
//    `pagingError`) verbatim: no client-side mapping, no invented message. A rejection that is not
//    the server's answer — an adapter cancellation, a bug — becomes the fixed
//    `UNRESOLVED_FAILURE` of `AuditStore`: `unexpected`, `status: 0`, no text taken from the thrown
//    value, so the screen is never left loading forever. `loadMore` re-throws it, because a paging
//    bug is not a refusal an operator can dismiss.
// 6. **A missing decision is `pending`, never `PASS`.** `RunSummary.decision` is optional and
//    nullable while the evaluation is pending (00 §9.3). The store keeps it as it arrived and the
//    `pending` filter matches `null`/absent rows; nothing here derives a verdict from state or
//    outcome.
// 7. **Completeness has no field on this wire.** `RunSummary` carries no completeness member, so no
//    completeness filter is offered (there is nothing to filter on) and the page renders
//    `EvidenceBadge`'s explicit unavailable copy in that column instead of a success tone.
// 8. **Format characters are escaped for display only.** `ref_name` may carry Unicode Cf characters
//    (the U+202E bidi override among them, plan Note line 226). The store keeps the served value;
//    `escapeFormatChars` (`./ref-name-display.ts`) is what the template renders.

import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { RunPage, RunSummary } from '@evidgate/sdk';

import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { RunDataService } from '../data/run-data.service';

/** Rows per request (the wire's default page size, 00 §9.1). */
export const RUN_LIST_PAGE_LIMIT = 50;

/**
 * How many further pages one trigger (a filter change, a `loadMore` click) may follow to fill the
 * filtered view (COORDINATOR-DECISION S101-C1). Bounded on purpose: a filter that matches nothing
 * must not walk a 100,000-row project.
 */
export const RUN_LIST_AUTO_PAGES = 5;

/**
 * The row half of the same bound: one trigger follows pages until it has scanned this many rows,
 * whichever of the two bounds is reached first. A server that answers 200-row pages reaches this
 * before {@link RUN_LIST_AUTO_PAGES}.
 */
export const RUN_LIST_AUTO_ROWS = 1_000;

/** One run state filter value: one of the wire's seven, or `''` for "every state". */
export type RunStateFilter = RunSummary['state'] | '';

/**
 * One decision filter value: the three verdicts, `pending` for a run whose decision is absent or
 * `null` (00 §9.3), or `''` for "every decision".
 */
export type RunDecisionFilter = 'PASS' | 'FAIL' | 'INCONCLUSIVE' | 'pending' | '';

/**
 * One rerun-scope filter value. The three members are independent label tests, as the wire defines
 * them: `rerun` is a row whose `rerun_label` is set, `reprocess` a row whose `reprocess_label` is
 * set (a row may carry both, and then it matches both), and `original` is a row with no
 * `parent_run_id` and neither label. A row with a parent and no label matches none of the three —
 * its provenance is unknown, so it is not silently reported as one of them.
 */
export type RunScopeFilter = '' | 'original' | 'rerun' | 'reprocess';

/**
 * The list's filter set as the store holds it. `refName` is the one member the server filters on;
 * the other three are URL signals applied client-side (header 1).
 */
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
const RUN_DECISIONS: readonly RunDecisionFilter[] = ['PASS', 'FAIL', 'INCONCLUSIVE', 'pending'];

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
  /** True while a followed page is on its way; the "Load more" control is disabled meanwhile. */
  loadingMore: boolean;
  /**
   * True when a trigger stopped at its page/row bound with a cursor still named and the filtered
   * view still short (header 3). `hasMore` stays true, so the page offers its control and its
   * "filtered from N loaded runs" note instead of presenting a short list as the whole project.
   */
  autoLoadCapped: boolean;
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
  const refName = query['ref'];

  return {
    state: RUN_STATES.includes(state as RunSummary['state']) ? (state as RunStateFilter) : '',
    decision: RUN_DECISIONS.includes(decision as RunDecisionFilter)
      ? (decision as RunDecisionFilter)
      : '',
    scope: RUN_SCOPES.includes(scope as RunScopeFilter) ? (scope as RunScopeFilter) : '',
    refName: isValidRefName(refName) ? refName : '',
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

/** The request query of one read: the page size, plus `refName` when the screen holds one. */
function pageQuery(filters: RunListFilters): { limit: number; refName?: string } {
  return filters.refName === ''
    ? { limit: RUN_LIST_PAGE_LIMIT }
    : { limit: RUN_LIST_PAGE_LIMIT, refName: filters.refName };
}

/**
 * One row against the client-side filters. The decision arm treats a missing member and `null`
 * alike: both are `pending`, and a `pending` filter never matches a row that carries a verdict
 * (header 6). The scope arm tests the selected label directly, because the two labels are
 * independent on the wire: a row carrying both a `rerun_label` and a `reprocess_label` matches
 * either scope, and `original` needs the absence of the parent AND of both labels.
 */
function matchesFilters(filters: RunListFilters, row: RunSummary): boolean {
  if (filters.state !== '' && row.state !== filters.state) {
    return false;
  }
  if (filters.decision !== '') {
    const decision = row.decision ?? null;
    if (filters.decision === 'pending' ? decision !== null : decision !== filters.decision) {
      return false;
    }
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
  }),
  withComputed(({ rows, nextCursor, status, filters, autoLoadCapped }) => {
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
      /** True when a ready list has nothing to show, filtered or not. */
      isEmpty: computed(() => status() === 'ready' && visibleRows().length === 0),
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

    /**
     * Follows pages until the filtered view holds one page, the server stops naming a cursor, or the
     * trigger's bound is reached (header 3). `sequence` is the restart this walk belongs to: after
     * any `await` an older sequence publishes nothing and requests no further page.
     *
     * `forceFirst` is the Load more click: a user who asked for the next page gets it even when the
     * filtered view already holds a page, so the first follow is unconditional and the top-up checks
     * start with the second one. An automatic top-up (a fresh read or a filter change) passes
     * `false` and stops as soon as the view is full.
     *
     * A repeated cursor throws — a contract violation, deliberately not converted into an
     * `ApiError`. A typed refusal keeps the rows in hand and lands in `pagingError`; a rejection
     * that is not the server's answer is re-thrown.
     */
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

    /**
     * Reads the first page of the CURRENT filters and then tops the filtered view up (header 3).
     * The rows are cleared as the read starts, because a row of the previous filter must never be
     * rendered as a result of the new one.
     *
     * Never rejects for a server answer: a typed failure is the `error` status. A repeated cursor
     * (a server contract violation) and a rejection that is not the server's answer still reject.
     */
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
      /**
       * Opens the list for `projectId` with the filters the URL query carries.
       *
       * Unknown or invalid member values are dropped and never sent (a `state` outside the wire's
       * seven, an empty or over-long `ref`, a control character in it). Calling it again is how the
       * page follows a route query change: the previous rows, cursor and paging state are replaced.
       */
      async init(
        projectId: string,
        query: Record<string, string | null | undefined> = {},
      ): Promise<void> {
        ownerProject = projectId;
        patchState(store, { filters: filtersFromQuery(query) });
        await restart();
      },

      /**
       * Applies a filter patch and keeps the list consistent with it.
       *
       * A `refName` change is a SERVER filter: the rows and the cursor belong to the previous
       * value, so the list restarts from the first page. Every other member is a client-side filter
       * over the rows already loaded: the rows stay, the view re-filters immediately, and pages are
       * followed only if the filtered view is short (header 3). With no project loaded yet (a page
       * that has not called `init`) only the filter state changes.
       */
      async setFilters(patch: Partial<RunListFilters>): Promise<void> {
        const applied = { ...store.filters(), ...patch };
        if (applied.refName !== store.filters().refName) {
          patchState(store, { filters: applied });
          await restart();
          return;
        }
        patchState(store, { filters: applied, autoLoadCapped: false });
        const projectId = ownerProject;
        // A page already in flight keeps the walk: it re-reads these filters on its next iteration,
        // so starting a second one would request the same cursor twice and append the page twice.
        if (projectId === null || store.status() !== 'ready' || store.loadingMore()) {
          return;
        }
        await fill(projectId, newestRead);
      },

      /**
       * Follows the next page (or pages) for the CURRENT filters, so the table keeps the rows it
       * already shows while more are on their way. Ignored while a page is in flight, before the
       * first read, and when the server named no cursor. Each click is a fresh trigger with its own
       * bound (header 3), which is why a capped list can still be extended by one click at a time.
       */
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
