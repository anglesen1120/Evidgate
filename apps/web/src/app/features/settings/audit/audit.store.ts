import { computed, DestroyRef, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { AuditEntryDto } from '@evidgate/sdk';

import { AuditDataService, type AuditListQuery } from '../../../core/data/audit-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { AUDIT_EXPORT_CAPPED_EVENT } from '../../../core/telemetry/telemetry-names';
import { TelemetryService } from '../../../core/telemetry/telemetry.service';
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

/** Browsing retains one server page; export uses its independent wire maximum. */
export const AUDIT_PAGE_LIMIT = 20;
/** Rows per request for the export: the wire maximum, so the cap costs as few round trips as it can. */
export const AUDIT_EXPORT_PAGE_LIMIT = 200;
/** Bound the download without retaining export rows in the browsing state. */
export const AUDIT_EXPORT_CAP = 10_000;

// Short server pages must finish normally; the page bound also stops an endless cursor walk.
export const AUDIT_EXPORT_MAX_PAGES = 200;

/** The list's read state: `idle` before the first read, which the page renders as loading. */
export type AuditPhase = 'idle' | 'loading' | 'ready' | 'error';

/** The export's state: idle, how many rows are in hand while running, then the outcome. */
export type AuditExportState =
  | { readonly phase: 'idle' }
  | { readonly phase: 'running'; readonly rows: number }
  | { readonly phase: 'done'; readonly rows: number; readonly capped: boolean }
  | { readonly phase: 'failed'; readonly error: ApiError | null };

export interface AuditState {
  rows: readonly AuditEntryDto[];
  filters: AuditFilters;
  nextCursor: string | null;
  page: number;
  pageSize: number;
  phase: AuditPhase;
  loadError: ApiError | null;
  paging: boolean;
  pagingError: ApiError | null;
  exportState: AuditExportState;
}

const NO_ROWS: readonly AuditEntryDto[] = [];
const EXPORT_IDLE: AuditExportState = { phase: 'idle' };

// Never surface adapter exception text: it may contain private request data.
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
    page: 1,
    pageSize: AUDIT_PAGE_LIMIT,
    phase: 'idle',
    loadError: null,
    paging: false,
    pagingError: null,
    exportState: EXPORT_IDLE,
  }),
  withComputed(({ nextCursor }) => ({
    hasNext: computed(() => nextCursor() !== null),
  })),
  withMethods((store) => {
    const data = inject(AuditDataService);
    const lifetime = inject(DestroyRef);
    /** `providedIn: 'root'`, so a spec that does not provide it gets the real (opt-out-aware) one. */
    const telemetry = inject(TelemetryService);

    let newestLoad = 0;
    let exportGeneration = 0;
    let ownerOrg: string | null = null;
    let cursors: (string | undefined)[] = [undefined];
    let pendingPage: number | null = null;

    lifetime.onDestroy(() => {
      exportGeneration += 1;
      newestLoad += 1;
    });

    async function loadFirstPage(orgId: string, filters?: AuditFilters): Promise<void> {
      const sequence = ++newestLoad;
      const applied = filters ?? store.filters();
      const retainedRows = orgId === ownerOrg ? store.rows() : NO_ROWS;
      ownerOrg = orgId;
      cursors = [undefined];
      pendingPage = null;
      patchState(store, {
        phase: 'loading',
        loadError: null,
        filters: applied,
        rows: retainedRows,
        nextCursor: null,
        page: 1,
        paging: false,
        pagingError: null,
      });
      try {
        const page = await data.list(orgId, toQuery(applied, store.pageSize()));
        if (sequence !== newestLoad) return;
        patchState(store, {
          phase: 'ready',
          rows: page.items,
          nextCursor: page.next_cursor,
        });
      } catch (error) {
        if (sequence !== newestLoad) return;
        patchState(store, {
          phase: 'error',
          rows: NO_ROWS,
          loadError: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
        });
      }
    }

    async function navigate(orgId: string, target: number): Promise<void> {
      if (orgId !== ownerOrg || store.paging() || store.phase() !== 'ready') return;
      const forward = target === store.page() + 1;
      const cursor = forward ? store.nextCursor() : cursors[target - 1];
      if (target < 1 || (forward && cursor === null) || target > store.page() + 1) return;
      pendingPage = target;
      const sequence = newestLoad;
      patchState(store, { paging: true, pagingError: null });
      try {
        const query = toQuery(store.filters(), store.pageSize());
        const page =
          cursor == null ? await data.list(orgId, query) : await data.list(orgId, query, cursor);
        if (sequence !== newestLoad) return;
        const history = [...cursors.slice(0, target - 1), cursor ?? undefined];
        if (page.next_cursor !== null && history.includes(page.next_cursor)) {
          throw new Error('The audit list repeated a forward cursor.');
        }
        cursors = history;
        pendingPage = null;
        patchState(store, {
          rows: page.items,
          nextCursor: page.next_cursor,
          page: target,
          paging: false,
          pagingError: null,
        });
      } catch (error) {
        if (sequence !== newestLoad) return;
        patchState(store, {
          paging: false,
          pagingError: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
        });
      }
    }

    return {
      async load(orgId: string, filters?: AuditFilters): Promise<void> {
        exportGeneration += 1;
        if (store.exportState().phase === 'running') {
          patchState(store, { exportState: EXPORT_IDLE });
        }
        await loadFirstPage(orgId, filters);
      },
      next: (orgId: string): Promise<void> => navigate(orgId, store.page() + 1),
      previous: (orgId: string): Promise<void> => navigate(orgId, store.page() - 1),
      retryPage: (orgId: string): Promise<void> =>
        pendingPage === null ? Promise.resolve() : navigate(orgId, pendingPage),
      async setPageSize(orgId: string, size: number): Promise<void> {
        if (orgId !== ownerOrg || ![20, 50, 100].includes(size) || size === store.pageSize())
          return;
        patchState(store, { pageSize: size });
        await loadFirstPage(orgId);
      },
      clearScope(): void {
        newestLoad += 1;
        exportGeneration += 1;
        ownerOrg = null;
        cursors = [undefined];
        pendingPage = null;
        patchState(store, {
          rows: NO_ROWS,
          filters: EMPTY_AUDIT_FILTERS,
          nextCursor: null,
          page: 1,
          phase: 'idle',
          loadError: null,
          paging: false,
          pagingError: null,
          exportState: EXPORT_IDLE,
        });
      },

      // Keep export's independent cursor walk and row cap; browsing never retains these rows.
      async exportCsv(orgId: string): Promise<string | null> {
        const query = toQuery(store.filters(), AUDIT_EXPORT_PAGE_LIMIT);
        // A reset may start a replacement export while the old request is still pending.
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
        if (capped) {
          // Only the current export reports its cap hit.
          telemetry.reportEvent(AUDIT_EXPORT_CAPPED_EVENT);
        }
        return toAuditCsv(collected);
      },

      // Invalidate an in-flight export before dropping its banner or changing scope.
      resetExport(): void {
        exportGeneration += 1;
        patchState(store, { exportState: EXPORT_IDLE });
      },
    };
  }),
);
