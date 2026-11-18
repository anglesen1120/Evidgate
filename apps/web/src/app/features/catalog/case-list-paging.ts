import type { CaseDto, CaseListResponse } from '@evidgate/sdk';
import { isApiRequestError, type ApiError } from '../../core/http/api-error';
import type { CaseListDataService, CaseListQuery } from '../../core/data/case-list-data.service';
import { takeKeysetPage, type KeysetRefusals } from '../../shared/keyset-walk';

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

export function createCasePaging(host: CasePagingHost, data: CaseListDataService): CasePaging {
  /** Bumped by every reset: a read of an older generation never writes state. */
  let generation = 0;
  let firstPage: AbortController | null = null;
  let nextPage: AbortController | null = null;
  /** Keys the loaded rows already show, so a shifted page cannot repeat one. */
  let seen = new Set<string>();
  const abort = (): void => {
    firstPage?.abort();
    nextPage?.abort();
    firstPage = null;
    nextPage = null;
  };
  const start = (projectId: string, query: CaseListQuery): void => {
    generation += 1;
    const sequence = generation;
    abort();
    const controller = new AbortController();
    firstPage = controller;
    seen = new Set<string>();
    host.patch({
      projectId,
      query,
      phase: 'loading',
      loadError: null,
      rows: NO_ROWS,
      nextCursor: null,
      paging: false,
      pagingError: null,
    });
    void data.list(projectId, query, controller.signal).then(
      (page) => {
        if (sequence !== generation) return;
        seen = new Set(page.items.map((row) => row.id));
        host.patch({
          phase: 'ready',
          loadError: null,
          rows: page.items,
          nextCursor: page.next_cursor,
        });
      },
      (error: unknown) => {
        if (sequence !== generation) return;
        host.patch({
          phase: 'error',
          loadError: typedFailure(error),
          rows: NO_ROWS,
          nextCursor: null,
        });
      },
    );
  };
  const follow = async (): Promise<boolean> => {
    const projectId = host.projectId();
    const cursor = host.nextCursor();
    if (projectId === null || cursor === null) return false;
    if (host.paging() || host.phase() !== 'ready') return false;
    const sequence = generation;
    nextPage?.abort();
    const controller = new AbortController();
    nextPage = controller;
    host.patch({ paging: true, pagingError: null });
    let page: CaseListResponse;
    try {
      page = await data.list(projectId, { ...host.query(), cursor }, controller.signal);
    } catch (error) {
      if (sequence !== generation) return false;
      host.patch({ paging: false, pagingError: typedFailure(error) });
      return false;
    }
    if (sequence !== generation) return false;
    const step = takeKeysetPage({
      cursor,
      items: page.items,
      nextCursor: page.next_cursor,
      seen,
      keyOf: (row) => row.id,
      refusals: PAGING_REFUSALS,
    });
    if (step.outcome === 'refused') {
      host.patch({ paging: false, pagingError: step.error });
      return false;
    }
    seen = step.seen;
    host.patch({
      paging: false,
      pagingError: null,
      rows: [...host.rows(), ...step.fresh],
      nextCursor: step.nextCursor,
    });
    return true;
  };
  return { start, follow, abort, epoch: () => generation };
}
