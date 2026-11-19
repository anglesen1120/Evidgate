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
  readonly rows: () => readonly CaseDto[];
  readonly patch: (patch: CasePagingPatch) => void;
}

export interface CasePaging {
  /** Replaces the loaded pages with the first page of `query`; aborting any read in flight. */
  start(projectId: string, query: CaseListQuery): void;
  /** Appends the next page; resolves `true` only when the rows in state came from it. */
  follow(): Promise<boolean>;
  /** Cancels both reads; a late answer never writes state. */
  abort(): void;
  /** The current generation: a read that captures an older one must not write state. */
  epoch(): number;
}

/** The read state one paging action advances. */
interface PagingRun {
  /** Bumped by every reset: a read of an older generation never writes state. */
  generation: number;
  firstPage: AbortController | null;
  nextPage: AbortController | null;
  /** Keys the loaded rows already show, so a shifted page cannot repeat one. */
  seen: Set<string>;
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
  host.patch({
    projectId,
    query,
    phase: 'loading',
    loadError: null,
    rows: NO_ROWS,
    nextCursor: null,
    browseCapped: false,
    paging: false,
    pagingError: null,
  });
  void data.list(projectId, query, controller.signal).then(
    (page) => {
      if (sequence !== run.generation) return;
      const retained = retainKeysetRows(page.items, page.next_cursor);
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

/** Appends the keyset-checked rows of `page`; `false` when the step refused it. */
function appendPage(
  run: PagingRun,
  host: CasePagingHost,
  cursor: string,
  page: CaseListResponse,
): boolean {
  const step = takeKeysetPage({
    cursor,
    items: page.items,
    nextCursor: page.next_cursor,
    seen: run.seen,
    keyOf: (row) => row.id,
    refusals: PAGING_REFUSALS,
  });
  if (step.outcome === 'refused') {
    host.patch({ paging: false, pagingError: step.error });
    return false;
  }
  const retained = retainKeysetRows([...host.rows(), ...step.fresh], step.nextCursor);
  run.seen = retained.capped ? new Set(retained.rows.map((row) => row.id)) : step.seen;
  host.patch({
    paging: false,
    pagingError: null,
    rows: retained.rows,
    nextCursor: retained.nextCursor,
    browseCapped: retained.capped,
  });
  return true;
}

async function readNextPage(
  run: PagingRun,
  host: CasePagingHost,
  data: CaseListDataService,
): Promise<boolean> {
  const projectId = host.projectId();
  const cursor = host.nextCursor();
  if (projectId === null || cursor === null) return false;
  if (host.paging() || host.phase() !== 'ready') return false;
  const sequence = run.generation;
  run.nextPage?.abort();
  const controller = new AbortController();
  run.nextPage = controller;
  host.patch({ paging: true, pagingError: null });
  let page: CaseListResponse;
  try {
    page = await data.list(projectId, { ...host.query(), cursor }, controller.signal);
  } catch (error) {
    if (sequence !== run.generation) return false;
    host.patch({ paging: false, pagingError: typedFailure(error) });
    return false;
  }
  if (sequence !== run.generation) return false;
  return appendPage(run, host, cursor, page);
}

export function createCasePaging(host: CasePagingHost, data: CaseListDataService): CasePaging {
  const run: PagingRun = {
    generation: 0,
    firstPage: null,
    nextPage: null,
    seen: new Set<string>(),
  };
  return {
    start: (projectId, query) => readFirstPage(run, host, data, projectId, query),
    follow: () => readNextPage(run, host, data),
    abort: () => clearReads(run),
    epoch: () => run.generation,
  };
}
