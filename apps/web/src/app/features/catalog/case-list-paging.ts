import type { CaseDto, CaseListResponse } from '@evidgate/sdk';
import { isApiRequestError, type ApiError } from '../../core/http/api-error';
import type { CaseListDataService, CaseListQuery } from '../../core/data/case-list-data.service';
import { takeKeysetPage, type KeysetRefusals } from '../../shared/keyset-walk';
import { retainKeysetRows } from '../../shared/keyset-retention';

const NO_ROWS: readonly CaseDto[] = [];

export const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message: $localize`:@@catalog.unresolved:The catalog request did not complete. Try again.`,
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

export function typedFailure(error: unknown): ApiError {
  return isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE;
}

export const PAGING_REFUSALS: KeysetRefusals = {
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

/** The page size a `?limit=` value names, or the default when absent or unsupported. */
export function pageSizeFrom(raw: string | undefined, fallback: number): number {
  const parsed = Number(raw ?? '');
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

export function sameServerState(left: CaseListQuery, right: CaseListQuery): boolean {
  return (
    left.limit === right.limit && left.status === right.status && left.framework === right.framework
  );
}

/** The state a page read writes back; the store keeps these fields, nothing else. */
export interface CasePagingPatch {
  readonly projectId?: string;
  readonly query?: CaseListQuery;
  readonly phase?: 'loading' | 'ready' | 'error';
  readonly loadError?: ApiError | null;
  readonly rows?: readonly CaseDto[];
  readonly nextCursor?: string | null;
  readonly page?: number;
  readonly requestCursors?: readonly (string | null)[];
  readonly browseCapped?: boolean;
  readonly paging?: boolean;
  readonly pagingError?: ApiError | null;
}

export interface CasePagingHost {
  readonly projectId: () => string | null;
  readonly query: () => CaseListQuery;
  readonly phase: () => 'loading' | 'ready' | 'error';
  readonly nextCursor: () => string | null;
  readonly paging: () => boolean;
  readonly page: () => number;
  readonly requestCursors: () => readonly (string | null)[];
  readonly patch: (patch: CasePagingPatch) => void;
}

export interface CasePaging {
  start(projectId: string, query: CaseListQuery): void;
  follow(): Promise<boolean>;
  previous(): Promise<boolean>;
  retry(): Promise<boolean>;
  /** Cancels both reads; a late answer never writes state. */
  abort(): void;
  /** The current generation: a read that captures an older one must not write state. */
  epoch(): number;
}

/** The read state one paging action advances. */
interface PagingRun {
  /** The retained-row cap every page of this list keeps; injected once when paging starts. */
  readonly cap: number;
  /** Bumped by every reset: a read of an older generation never writes state. */
  generation: number;
  firstPage: AbortController | null;
  nextPage: AbortController | null;
  /** Keys the loaded rows already show, so a shifted page cannot repeat one. */
  seen: Set<string>;
  pending: { page: number; cursor: string | null } | null;
}

function clearReads(run: PagingRun): void {
  run.firstPage?.abort();
  run.nextPage?.abort();
  run.firstPage = null;
  run.nextPage = null;
}

function readFirstPage(
  run: PagingRun,
  host: CasePagingHost,
  data: CaseListDataService,
  projectId: string,
  query: CaseListQuery,
): void {
  run.generation += 1;
  const sequence = run.generation;
  clearReads(run);
  const controller = new AbortController();
  run.firstPage = controller;
  run.seen = new Set<string>();
  run.pending = null;
  host.patch({
    projectId,
    query,
    phase: 'loading',
    loadError: null,
    rows: NO_ROWS,
    nextCursor: null,
    page: 1,
    requestCursors: [null],
    browseCapped: false,
    paging: false,
    pagingError: null,
  });
  void data.list(projectId, query, controller.signal).then(
    (page) => {
      if (sequence !== run.generation) return;
      const retained = retainKeysetRows(page.items, page.next_cursor, run.cap);
      run.seen = new Set(retained.rows.map((row) => row.id));
      host.patch({
        phase: 'ready',
        loadError: null,
        rows: retained.rows,
        nextCursor: retained.nextCursor,
        browseCapped: retained.capped,
      });
    },
    (error: unknown) => {
      if (sequence !== run.generation) return;
      host.patch({
        phase: 'error',
        loadError: typedFailure(error),
        rows: NO_ROWS,
        nextCursor: null,
      });
    },
  );
}

function replacePage(
  run: PagingRun,
  host: CasePagingHost,
  target: { page: number; cursor: string | null },
  page: CaseListResponse,
): boolean {
  const cursor = target.cursor;
  const history = [...host.requestCursors().slice(0, target.page - 1), target.cursor];
  if (page.next_cursor !== null && history.includes(page.next_cursor)) {
    host.patch({ paging: false, pagingError: PAGING_REFUSALS.cursorRepeated });
    return false;
  }
  const step = takeKeysetPage({
    cursor,
    items: page.items,
    nextCursor: page.next_cursor,
    seen: target.page > host.page() ? run.seen : new Set<string>(),
    keyOf: (row) => row.id,
    refusals: PAGING_REFUSALS,
  });
  if (step.outcome === 'refused') {
    host.patch({ paging: false, pagingError: step.error });
    return false;
  }
  const retained = retainKeysetRows(step.fresh, step.nextCursor, run.cap);
  run.seen = new Set(retained.rows.map((row) => row.id));
  run.pending = null;
  host.patch({
    paging: false,
    pagingError: null,
    rows: retained.rows,
    page: target.page,
    requestCursors: history,
    nextCursor: retained.nextCursor,
    browseCapped: retained.capped,
  });
  return true;
}

async function readPage(
  run: PagingRun,
  host: CasePagingHost,
  data: CaseListDataService,
  target: { page: number; cursor: string | null },
): Promise<boolean> {
  const projectId = host.projectId();
  if (projectId === null || host.paging() || host.phase() !== 'ready') return false;
  run.pending = target;
  const sequence = run.generation;
  run.nextPage?.abort();
  const controller = new AbortController();
  run.nextPage = controller;
  host.patch({ paging: true, pagingError: null });
  let page: CaseListResponse;
  try {
    page = await data.list(
      projectId,
      { ...host.query(), ...(target.cursor === null ? {} : { cursor: target.cursor }) },
      controller.signal,
    );
  } catch (error) {
    if (sequence !== run.generation) return false;
    host.patch({ paging: false, pagingError: typedFailure(error) });
    return false;
  }
  if (sequence !== run.generation) return false;
  return replacePage(run, host, target, page);
}

export function createCasePaging(
  host: CasePagingHost,
  data: CaseListDataService,
  cap: number,
): CasePaging {
  const run: PagingRun = {
    cap,
    generation: 0,
    firstPage: null,
    nextPage: null,
    seen: new Set<string>(),
    pending: null,
  };
  return {
    start: (projectId, query) => readFirstPage(run, host, data, projectId, query),
    follow: () => {
      const cursor = host.nextCursor();
      return cursor === null
        ? Promise.resolve(false)
        : readPage(run, host, data, { page: host.page() + 1, cursor });
    },
    previous: () =>
      host.page() <= 1
        ? Promise.resolve(false)
        : readPage(run, host, data, {
            page: host.page() - 1,
            cursor: host.requestCursors()[host.page() - 2] ?? null,
          }),
    retry: () =>
      run.pending === null ? Promise.resolve(false) : readPage(run, host, data, run.pending),
    abort: () => clearReads(run),
    epoch: () => run.generation,
  };
}
