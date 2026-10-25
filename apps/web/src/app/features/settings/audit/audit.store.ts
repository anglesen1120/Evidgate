// 09.7.1-T6 (plan 09.7.1 §2 AC7/AC8, §4 audit row, §5 audit row, §7 "CSV export is client-side",
// §8 S5-adjacent "CSV export caps at 10,000 rows" and the hot-path row, §9 "Audit export cap",
// §10 T6.1/T6.2; COORDINATOR-DECISION S76): the Audit tab's view state, as a `@ngrx/signals`
// SignalStore — the package the web contract names (`plans/features/README.md` §12 table,
// ADR-0008 (d)), shaped like `features/onboarding/onboarding.store.ts`.
//
// One instance per page (`providers: [AuditStore]` on `AuditPage`, never `providedIn: 'root'`),
// because the store owns one org's cursor, its rows and its export state: a tab switch must not
// inherit another org's list.
//
// What shapes every method:
//
// 1. **The list is a cursor page, never "load all"** (00 §9.1, W7). `load` reads the first page
//    with `limit = AUDIT_PAGE_LIMIT`; `loadMore` follows `next_cursor` once and appends while the
//    rows already on screen stay put. A repeated cursor is a contract violation and a `throw` —
//    the same refusal as `IdentityDataService` and `MembersStore` — so a broken cursor costs the
//    visibility of the bug, not a hung tab. The list's own page bound is not a violation: W7
//    promises cursor paging past 200 rows, so the ceiling is the same 10,000 rows the export caps
//    at (`AUDIT_MAX_PAGES = 200` pages), and reaching it publishes `browseCapped` — no further
//    request is made, `hasMore` turns false, and the tab offers the filter/export hint instead of
//    a "more" control that could only fail. The shared `MAX_CURSOR_PAGES` (20) bounds a
//    whole-list read of a catalogue, org or project list of tens of rows and must not be reused
//    here.
// 2. **Newest call wins.** Two `load`s can overlap (an org switch, a filter apply while the first
//    page is still in flight). Each `load` bumps a per-instance sequence, and an answer whose
//    sequence is no longer the newest — success or failure — publishes nothing, because it
//    describes a filter or an org nobody is looking at any more.
// 3. **The server's failure is the server's.** A typed `ApiError` reaches `loadError` (or
//    `pagingError`) verbatim: no client-side mapping, no invented message. A rejection that is not
//    the server's answer — an adapter cancellation, a bug — becomes the fixed
//    `UNRESOLVED_FAILURE` of the Members store (`features/settings/members/members.store.ts`
//    §header 5): `unexpected`, `status: 0`, no text taken from the thrown value, so the tab is
//    never left loading forever and a thrown value's message (which may carry request data) is
//    never surfaced. `loadMore` re-throws it instead, because a paging bug is not a row-level
//    refusal an operator can dismiss.
// 4. **The export is bounded, generation-guarded and never held in state.** `exportCsv` runs the
//    repeated-cursor fetch at `limit = AUDIT_EXPORT_PAGE_LIMIT` with the CURRENT filters, stops as
//    soon as it holds `AUDIT_EXPORT_CAP` rows or the server stops naming a cursor, truncates to
//    exactly the cap, and returns the CSV text. Only a row counter is state (`exportState`) — the
//    rows live in the call's local array and are released with it (AC7: a 10,000-row export must
//    not become a 10,000-row signal a template can read). A repeated cursor is a failure
//    (`exportState` `failed`, no CSV) rather than a shorter document that would misrepresent the
//    org's log. A server whose pages are shorter than `AUDIT_EXPORT_PAGE_LIMIT` is NOT a failure:
//    the loop is bounded by rows *and* by `AUDIT_EXPORT_MAX_PAGES` safety pages, and hitting the
//    page bound with a cursor still named finishes as `capped: true` with the rows read so far —
//    the result's `rows` is the count the banner must print, which is below the cap in that case.
//    Every export carries a generation captured at its start and bumped by `load`, by
//    `resetExport`, by the next export and by the store's own destruction: after any `await` a
//    superseded loop publishes nothing and issues no further page, so an org switch (the page
//    calls `resetExport()` then `load`) can neither resurrect a stale banner nor keep reading the
//    previous org. A superseded call answers `null` — the same "no result" a refused export
//    answers — and `audit-export.component.ts` downloads only on non-null, so nothing lands on
//    the operator's disk.
// 5. **Nothing secret-shaped is state.** Rows are the server's audit metadata; the support grant
//    marker lives inside `metadata` and is read on demand (`support-access.ts`), never copied into
//    a signal of its own, and no field name here is secret-shaped (the Settings feature's
//    `no-secret-field-in-store` rule pins that).
//
// Recorded gap (plan §8 telemetry row: "audit export cap hits logged as an error-funnel event with
// ids only"): this store emits NO telemetry event when `capped` is true. The web telemetry facade
// has no named-event API and its exporter admits an empty event-name set and a closed attribute
// allow-list (`core/telemetry/telemetry-names.ts`, `attribute-allowlist.ts`) with no row-count
// key; adding one would mean widening the allow-list, which is a reviewed change of its own and
// outside this task's file scope.

import { computed, DestroyRef, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { AuditEntryDto, AuditListResponse } from '@evidgate/sdk';

import { AuditDataService, type AuditListQuery } from '../../../core/data/audit-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { toAuditCsv } from './audit-csv';

/** One actor-type filter value: one of the wire's five, or `''` for "every actor". */
export type AuditActorFilter = AuditEntryDto['actor_type'] | '';

/**
 * The tab's filter form as the store holds it. `from`/`to` are the inputs' ISO 8601 date-time
 * strings (or `''`): the store does not parse or normalise them, the server validates them and its
 * `422` is shown verbatim.
 */
export interface AuditFilters {
  readonly actorType: AuditActorFilter;
  readonly action: string;
  readonly objectType: string;
  readonly from: string;
  readonly to: string;
}

/** No filter at all — an org's whole audit log, newest first. */
export const EMPTY_AUDIT_FILTERS: AuditFilters = {
  actorType: '',
  action: '',
  objectType: '',
  from: '',
  to: '',
};

/** Rows per request for the table (the wire's default page size, 00 §9.1). */
export const AUDIT_PAGE_LIMIT = 50;
/** Rows per request for the export: the wire maximum, so the cap costs as few round trips as it can. */
export const AUDIT_EXPORT_PAGE_LIMIT = 200;
/** The export's row cap (AC7): above it the caller gets a capped banner, never a bigger download. */
export const AUDIT_EXPORT_CAP = 10_000;

/**
 * How many pages the table may hold on screen: `AUDIT_EXPORT_CAP / AUDIT_PAGE_LIMIT = 200`, the
 * same 10,000 rows the export caps at. The list needs a bound of its own rather than the shared
 * `MAX_CURSOR_PAGES` (20 pages, sized for a whole-list read of a catalogue or org list of tens of
 * rows): W7 promises cursor paging past 200 rows, and 1,000 rows made a large audit log
 * unreachable after its twentieth page while the tab showed a paging failure Retry could never
 * clear. Reaching this bound is a normal outcome — `browseCapped` — not a contract violation.
 */
export const AUDIT_MAX_PAGES = AUDIT_EXPORT_CAP / AUDIT_PAGE_LIMIT;

/**
 * The export loop's page bound: a safety net for a server whose pages are shorter than
 * `AUDIT_EXPORT_PAGE_LIMIT`, not the way the row cap is enforced. At 200 rows a page the row cap
 * ends the loop in 50 pages, so 200 pages only matters when pages come back short — 3,000 rows in
 * 50-row pages still finishes (60 pages) instead of failing at page 50 with a retryable-looking
 * error, and a server that never stops naming a cursor finishes as `capped` with the rows read so
 * far (200 pages) instead of a failure the operator can only retry into.
 */
export const AUDIT_EXPORT_MAX_PAGES = 200;

/** The list's read state: `idle` before the first read, which the page renders as loading. */
export type AuditPhase = 'idle' | 'loading' | 'ready' | 'error';

/** The export's state: idle, how many rows are in hand while running, then the outcome. */
export type AuditExportState =
  | { readonly phase: 'idle' }
  | { readonly phase: 'running'; readonly rows: number }
  | { readonly phase: 'done'; readonly rows: number; readonly capped: boolean }
  | { readonly phase: 'failed'; readonly error: ApiError | null };

/** The store's state. Mutable by design: `patchState` replaces members, never mutates them. */
export interface AuditState {
  /** The rows on screen, newest first as the server ordered them, page by page. */
  rows: readonly AuditEntryDto[];
  /** The filters the rows belong to; `loadMore` and `exportCsv` read them from here. */
  filters: AuditFilters;
  /** The cursor the next page needs, or `null` when the list ended. */
  nextCursor: string | null;
  /** The list's read state. */
  phase: AuditPhase;
  /** The server's typed failure for the list read; `null` when it never failed. */
  loadError: ApiError | null;
  /** True while one more page is on its way; the "more" control is disabled meanwhile. */
  paging: boolean;
  /** The server's typed failure for the last `loadMore`, if any. */
  pagingError: ApiError | null;
  /**
   * True when the list already holds {@link AUDIT_MAX_PAGES} pages and the server still names
   * another one: a bounded list, not an ended log. `hasMore` is false then, because the next page
   * cannot be read; the tab says so and points at the filter and the export.
   */
  browseCapped: boolean;
  /** The export's progress and outcome. The rows themselves are never stored. */
  exportState: AuditExportState;
}

const NO_ROWS: readonly AuditEntryDto[] = [];
const EXPORT_IDLE: AuditExportState = { phase: 'idle' };

/**
 * The fixed failure reported when a rejection is not the server's answer — an adapter
 * cancellation or a bug. Same shape and same reasoning as `MembersStore`'s constant: `unexpected`
 * is the classification table's "no UI state for this", `status: 0` says no response exists, and no
 * text is taken from the thrown value.
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
 * The filters as the wire query: keys with `''` are omitted rather than sent empty (an empty
 * `actor_type=` would be a `422`, and "no filter" is the absence of the parameter).
 */
function toQuery(filters: AuditFilters, limit: number): AuditListQuery {
  const query: AuditListQuery = { limit };
  if (filters.actorType !== '') {
    query.actor_type = filters.actorType;
  }
  if (filters.action !== '') {
    query.action = filters.action;
  }
  if (filters.objectType !== '') {
    query.object_type = filters.objectType;
  }
  if (filters.from !== '') {
    query.from = filters.from;
  }
  if (filters.to !== '') {
    query.to = filters.to;
  }
  return query;
}

export const AuditStore = signalStore(
  withState<AuditState>({
    rows: NO_ROWS,
    filters: EMPTY_AUDIT_FILTERS,
    nextCursor: null,
    phase: 'idle',
    loadError: null,
    paging: false,
    pagingError: null,
    browseCapped: false,
    exportState: EXPORT_IDLE,
  }),
  withComputed(({ nextCursor, browseCapped }) => ({
    /**
     * True while the server named another page and the list is below its own page bound, so the
     * "more" control can be offered. At the bound the cursor is still there but unreadable:
     * `browseCapped` says which, and the tab renders the hint instead of a control that cannot
     * fetch anything.
     */
    hasMore: computed(() => nextCursor() !== null && !browseCapped()),
  })),
  withMethods((store) => {
    const data = inject(AuditDataService);
    const lifetime = inject(DestroyRef);

    /** Bumped by every `load`; an answer of an older generation publishes nothing (header 2). */
    let newestLoad = 0;
    /**
     * Bumped by every `load`, `resetExport`, export start and store destruction: an export whose
     * generation is no longer the newest publishes nothing and requests nothing more (header 4).
     */
    let exportGeneration = 0;
    /** The org the state belongs to: `loadMore(orgId)` for another org is ignored. */
    let ownerOrg: string | null = null;
    /** The cursors this list already asked for, so a repeated one is a contract violation. */
    const followedCursors = new Set<string>();
    /** Pages of this list already followed, the first page included; bounded by the page cap. */
    let pagesRead = 1;

    // The owner is going away (an org switch unmounts the tab): stop a running export from
    // patching state or reading pages into a store nobody renders any more.
    lifetime.onDestroy(() => {
      exportGeneration += 1;
      newestLoad += 1;
    });

    return {
      /**
       * Reads the first page for `orgId` with `filters` (default: the filters already applied) and
       * publishes it as the whole list.
       *
       * Never rejects: the page calls it from an effect, so a rejection would be an unhandled
       * promise rejection. The rows are cleared as the read starts, because a row of the previous
       * filter must never be rendered as a result of the new one.
       */
      async load(orgId: string, filters?: AuditFilters): Promise<void> {
        newestLoad += 1;
        // A running export belongs to the list the caller is leaving: its loop sees this bump
        // after its next `await` and publishes nothing (header 4).
        exportGeneration += 1;
        const sequence = newestLoad;
        const applied = filters ?? store.filters();
        ownerOrg = orgId;
        followedCursors.clear();
        pagesRead = 1;
        // Only a *running* export is dropped: a finished banner describes rows the same filters
        // reproduce, and clearing it would take away the download the operator just made.
        const abandonedExport = store.exportState().phase === 'running';
        patchState(store, {
          phase: 'loading',
          loadError: null,
          filters: applied,
          rows: NO_ROWS,
          nextCursor: null,
          paging: false,
          pagingError: null,
          browseCapped: false,
          ...(abandonedExport ? { exportState: EXPORT_IDLE } : {}),
        });

        let page: AuditListResponse;
        try {
          page = await data.list(orgId, toQuery(applied, AUDIT_PAGE_LIMIT));
        } catch (error) {
          if (sequence !== newestLoad) {
            return;
          }
          patchState(store, {
            phase: 'error',
            loadError: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
            rows: NO_ROWS,
            nextCursor: null,
          });
          return;
        }
        if (sequence !== newestLoad) {
          return;
        }
        patchState(store, {
          phase: 'ready',
          loadError: null,
          rows: page.items,
          nextCursor: page.next_cursor,
        });
      },

      /**
       * Follows the list's `next_cursor` once and appends the page, so the table keeps the rows it
       * already shows while the next one is on its way.
       *
       * Ignored while another page is in flight, without a cursor, once the list is at its own
       * page bound ({@link AUDIT_MAX_PAGES}), or for an org this store is not showing. A cursor
       * already followed is a contract violation and throws (header 1). Reaching the bound is not:
       * the page publishes `browseCapped` instead, which turns `hasMore` false, so the tab stops
       * offering a page it cannot read and points at the filter and the export. A typed server
       * refusal keeps the rows and lands in `pagingError`.
       */
      async loadMore(orgId: string): Promise<void> {
        const cursor = store.nextCursor();
        if (store.paging() || cursor === null || store.browseCapped() || orgId !== ownerOrg) {
          return;
        }
        if (followedCursors.has(cursor)) {
          throw new Error(`the audit list repeated cursor "${cursor}"`);
        }

        const sequence = newestLoad;
        patchState(store, { paging: true, pagingError: null });
        let page: AuditListResponse;
        try {
          page = await data.list(orgId, toQuery(store.filters(), AUDIT_PAGE_LIMIT), cursor);
        } catch (error) {
          if (sequence !== newestLoad) {
            return;
          }
          // Only a typed refusal is the server's answer; a contract violation is re-thrown.
          if (!isApiRequestError(error)) {
            patchState(store, { paging: false });
            throw error;
          }
          patchState(store, { paging: false, pagingError: error.error });
          return;
        }
        // A reload owns the state now: this page belongs to a list nobody renders any more.
        if (sequence !== newestLoad) {
          return;
        }

        followedCursors.add(cursor);
        pagesRead += 1;
        patchState(store, {
          paging: false,
          pagingError: null,
          rows: [...store.rows(), ...page.items],
          nextCursor: page.next_cursor,
          // The bound is reached and the server still names a page: a bounded list, not a fault.
          browseCapped: page.next_cursor !== null && pagesRead >= AUDIT_MAX_PAGES,
        });
      },

      /**
       * Exports the CURRENT filters' rows as CSV, bounded by {@link AUDIT_EXPORT_CAP} (AC7).
       *
       * Reads `limit = AUDIT_EXPORT_PAGE_LIMIT` pages, stops as soon as the cap is reached, the
       * server names no next cursor, or {@link AUDIT_EXPORT_MAX_PAGES} pages were read, and
       * truncates the last page to exactly the cap. `capped` is true when rows were left behind —
       * the cap was reached and the server still had more — or when the page bound was reached
       * with a cursor still named, in which case `rows` is the count actually collected (below the
       * cap) and it is the number the banner must print.
       *
       * Generation-guarded: `load`, `resetExport`, the next export and the store's destruction all
       * bump the generation, and a superseded loop returns `null` after its next `await` without
       * patching state or reading another page (header 4).
       *
       * Never rejects and never puts the rows in state.
       *
       * @returns the CSV document, or `null` when the export failed or was superseded.
       */
      async exportCsv(orgId: string): Promise<string | null> {
        const query = toQuery(store.filters(), AUDIT_EXPORT_PAGE_LIMIT);
        // One export at a time: a second click while the first is running is the same export, and
        // running it twice would double the rows held in memory for one download. A reset (org
        // switch, banner close) makes the state idle again, so a *new* export may start while the
        // old loop is still pending — the generation bump below is what stops the old one.
        if (store.exportState().phase === 'running') {
          return null;
        }
        exportGeneration += 1;
        const generation = exportGeneration;
        const superseded = (): boolean => generation !== exportGeneration;
        patchState(store, { exportState: { phase: 'running', rows: 0 } });

        const collected: AuditEntryDto[] = [];
        const requested = new Set<string>();
        let cursor: string | undefined;
        let pages = 0;
        let capped = false;
        try {
          for (;;) {
            const response =
              cursor === undefined
                ? await data.list(orgId, query)
                : await data.list(orgId, query, cursor);
            // Superseded while the page was in flight: publish nothing, request nothing more.
            if (superseded()) {
              return null;
            }
            collected.push(...response.items);

            const overflow = collected.length > AUDIT_EXPORT_CAP;
            if (overflow) {
              collected.length = AUDIT_EXPORT_CAP;
            }
            pages += 1;
            if (collected.length >= AUDIT_EXPORT_CAP) {
              capped = overflow || response.next_cursor !== null;
              break;
            }
            if (response.next_cursor === null) {
              break;
            }
            if (requested.has(response.next_cursor)) {
              throw new Error(`the audit export repeated cursor "${response.next_cursor}"`);
            }
            requested.add(response.next_cursor);
            cursor = response.next_cursor;
            // Short pages end the loop here, not in the error path: the server still names a page
            // and the document cannot grow, so the rows in hand are the export's complete content.
            if (pages >= AUDIT_EXPORT_MAX_PAGES) {
              capped = true;
              break;
            }
            patchState(store, { exportState: { phase: 'running', rows: collected.length } });
          }
        } catch (error) {
          if (superseded()) {
            return null;
          }
          patchState(store, {
            exportState: {
              phase: 'failed',
              error: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
            },
          });
          return null;
        }

        if (superseded()) {
          return null;
        }
        patchState(store, {
          exportState: { phase: 'done', rows: collected.length, capped },
        });
        return toAuditCsv(collected);
      },

      /**
       * Clears the export's outcome, so closing the banner starts from `idle` again — and drops a
       * running export: the page calls this on an org switch, before `load`, and the bumped
       * generation is what stops the old loop from publishing into the new org's state.
       */
      resetExport(): void {
        exportGeneration += 1;
        patchState(store, { exportState: EXPORT_IDLE });
      },
    };
  }),
);
