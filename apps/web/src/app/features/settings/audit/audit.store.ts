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
//    rows already on screen stay put. A repeated cursor or a page cap that is still naming another
//    page is a contract violation and a `throw` — the same two refusals as `IdentityDataService`
//    and `MembersStore` — so a broken cursor costs the visibility of the bug, not a hung tab.
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
// 4. **The export is bounded and never held in state.** `exportCsv` runs the repeated-cursor fetch
//    at `limit = AUDIT_EXPORT_PAGE_LIMIT` with the CURRENT filters, stops as soon as it holds
//    `AUDIT_EXPORT_CAP` rows or the server stops naming a cursor, truncates to exactly the cap,
//    and returns the CSV text. Only a row counter is state (`exportState`) — the rows live in the
//    call's local array and are released with it (AC7: a 10,000-row export must not become a
//    10,000-row signal a template can read). A repeated cursor, or its own page bound reached
//    without filling the cap, is a failure (`exportState` `failed`, no CSV) rather than a shorter
//    document that would misrepresent the org's log.
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

import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { AuditEntryDto, AuditListResponse } from '@evidgate/sdk';

import { AuditDataService, type AuditListQuery } from '../../../core/data/audit-data.service';
import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
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
 * How many pages the export may follow before the loop is a contract violation: exactly the pages
 * the cap needs, `ceil(10,000 / 200) = 50`. `MAX_CURSOR_PAGES` (20) bounds a whole-list *read* of a
 * catalogue, org or project list, which is tens of rows; the export deliberately reads up to the
 * cap, so it needs its own bound and reaching it without filling the cap means the cursor loop is
 * broken.
 */
const AUDIT_EXPORT_MAX_PAGES = Math.ceil(AUDIT_EXPORT_CAP / AUDIT_EXPORT_PAGE_LIMIT);

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
    exportState: EXPORT_IDLE,
  }),
  withComputed(({ nextCursor }) => ({
    /** True while the server named another page, so the "more" control can be offered. */
    hasMore: computed(() => nextCursor() !== null),
  })),
  withMethods((store) => {
    const data = inject(AuditDataService);

    /** Bumped by every `load`; an answer of an older generation publishes nothing (header 2). */
    let newestLoad = 0;
    /** The org the state belongs to: `loadMore(orgId)` for another org is ignored. */
    let ownerOrg: string | null = null;
    /** The cursors this list already asked for, so a repeated one is a contract violation. */
    const followedCursors = new Set<string>();
    /** Pages of this list already followed, the first page included; bounded by the page cap. */
    let pagesRead = 1;

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
        const sequence = newestLoad;
        const applied = filters ?? store.filters();
        ownerOrg = orgId;
        followedCursors.clear();
        pagesRead = 1;
        patchState(store, {
          phase: 'loading',
          loadError: null,
          filters: applied,
          rows: NO_ROWS,
          nextCursor: null,
          paging: false,
          pagingError: null,
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
       * Ignored while another page is in flight, without a cursor, or for an org this store is not
       * showing. A cursor already followed, or a `MAX_CURSOR_PAGES`-th page that still names
       * another one, is a contract violation and throws (header 1). A typed server refusal keeps
       * the rows and lands in `pagingError`.
       */
      async loadMore(orgId: string): Promise<void> {
        const cursor = store.nextCursor();
        if (store.paging() || cursor === null || orgId !== ownerOrg) {
          return;
        }
        if (followedCursors.has(cursor)) {
          throw new Error(`the audit list repeated cursor "${cursor}"`);
        }
        if (pagesRead >= MAX_CURSOR_PAGES) {
          throw new Error(`the audit list did not finish within ${MAX_CURSOR_PAGES} pages`);
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
        });
      },

      /**
       * Exports the CURRENT filters' rows as CSV, bounded by {@link AUDIT_EXPORT_CAP} (AC7).
       *
       * Reads `limit = AUDIT_EXPORT_PAGE_LIMIT` pages, stops as soon as the cap is reached or the
       * server names no next cursor, and truncates the last page to exactly the cap. `capped` is
       * true only when rows were left behind — the cap was reached and the server still had more.
       *
       * Never rejects and never puts the rows in state (header 4).
       *
       * @returns the CSV document, or `null` when the export failed.
       */
      async exportCsv(orgId: string): Promise<string | null> {
        const query = toQuery(store.filters(), AUDIT_EXPORT_PAGE_LIMIT);
        // One export at a time: a second click while the first is running is the same export, and
        // running it twice would double the rows held in memory for one download.
        if (store.exportState().phase === 'running') {
          return null;
        }
        patchState(store, { exportState: { phase: 'running', rows: 0 } });

        const collected: AuditEntryDto[] = [];
        const requested = new Set<string>();
        let cursor: string | undefined;
        let finished = false;
        let capped = false;
        try {
          for (let page = 0; page < AUDIT_EXPORT_MAX_PAGES; page += 1) {
            const response =
              cursor === undefined
                ? await data.list(orgId, query)
                : await data.list(orgId, query, cursor);
            collected.push(...response.items);

            const overflow = collected.length > AUDIT_EXPORT_CAP;
            if (overflow) {
              collected.length = AUDIT_EXPORT_CAP;
            }
            if (collected.length >= AUDIT_EXPORT_CAP) {
              capped = overflow || response.next_cursor !== null;
              finished = true;
              break;
            }
            if (response.next_cursor === null) {
              finished = true;
              break;
            }
            if (requested.has(response.next_cursor)) {
              throw new Error(`the audit export repeated cursor "${response.next_cursor}"`);
            }
            requested.add(response.next_cursor);
            cursor = response.next_cursor;
            patchState(store, { exportState: { phase: 'running', rows: collected.length } });
          }
          if (!finished) {
            throw new Error(
              `the audit export did not finish within ${AUDIT_EXPORT_MAX_PAGES} pages`,
            );
          }
        } catch (error) {
          patchState(store, {
            exportState: {
              phase: 'failed',
              error: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
            },
          });
          return null;
        }

        patchState(store, {
          exportState: { phase: 'done', rows: collected.length, capped },
        });
        return toAuditCsv(collected);
      },

      /** Clears the export's outcome, so closing the banner starts from `idle` again. */
      resetExport(): void {
        patchState(store, { exportState: EXPORT_IDLE });
      },
    };
  }),
);
