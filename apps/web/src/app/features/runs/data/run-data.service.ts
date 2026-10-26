// 09.3.1-T1 (plan 09.3.1 §2 AC9, §4 runs row, §5 runs run-list row, §10 T1.1): the run list's
// request half.
//
// The feature service holds no HTTP of its own. `core/data/runs-data.service.ts` already exposes
// `list(projectId, query)` over the generated `listRuns` operation through `ApiGateway`, and
// `apps/web/lint/boundaries.cjs` exempts only `core/{http,data}` from
// `evidgate/no-http-outside-core` — a second data service that imported the SDK operation would be
// refused by lint and would duplicate the seam where a typed failure is classified. This module
// therefore narrows that one call to the query the screen actually owns and does the wire naming
// in one place:
//
// - `refName` keeps its camelCase UI name and is sent as the contract's `ref_name` (the only server
//   filter this screen exposes: `pr_number`, `head_sha`, `trigger_event` and `is_fork` have no
//   index without `ref_name`, plan Note at line 228, so they stay unexposed);
// - `cursor` and `limit` are passed through untouched, because the store owns the cursor walk;
// - an absent/empty `refName` is omitted rather than sent as `ref_name=` (the serializer would send
//   the empty string, which the server refuses as a 422 — the same rule
//   `core/data/audit-data.service.ts` applies to its free-text filters).

import { inject, Injectable } from '@angular/core';
import type { RunPage } from '@evidgate/sdk';

import { RunsDataService } from '../../../core/data/runs-data.service';

/** One request of the run list: the page position plus the one server-side filter. */
export interface RunListQuery {
  /** The opaque `next_cursor` of the previous page; absent on the first read. */
  readonly cursor?: string;
  /** Rows per page; the store always passes its own {@link RUN_LIST_PAGE_LIMIT} (50). */
  readonly limit?: number;
  /** Exact `ref_name` of the run's recorded identity; absent when the screen has none. */
  readonly refName?: string;
}

/**
 * The run list's data service (`GET /v1/projects/{p}/runs`, 00 §9.1 cursor page of `RunSummary`).
 *
 * `providedIn: 'root'` like every data service: it holds no state, so the page's `RunListStore`
 * (one per page) owns the cursor, the filters and the rows.
 */
@Injectable({ providedIn: 'root' })
export class RunDataService {
  private readonly runs = inject(RunsDataService);

  /**
   * One page of the project's runs, in the server's order (`(created_at, id)` ascending).
   *
   * The page is returned as served: the store keeps the rows it already renders and follows
   * `next_cursor` itself, so a broken cursor costs the page it was following rather than the list on
   * screen. A typed refusal arrives as the thrown `ApiRequestError` of `ApiGateway` — nothing is
   * caught, mapped or invented here.
   */
  listRuns(projectId: string, request: RunListQuery = {}): Promise<RunPage> {
    return this.runs.list(projectId, wireQuery(request));
  }
}

/**
 * The query the request actually carries. `ref_name` is omitted when the screen holds no ref filter
 * (an empty value would be a 422, and "no filter" is the absence of the parameter), and `cursor` is
 * omitted on the first page for the same reason.
 */
function wireQuery(request: RunListQuery) {
  const query: { cursor?: string; limit?: number; ref_name?: string } = {};
  if (request.cursor !== undefined && request.cursor !== '') {
    query.cursor = request.cursor;
  }
  if (request.limit !== undefined) {
    query.limit = request.limit;
  }
  if (request.refName !== undefined && request.refName !== '') {
    query.ref_name = request.refName;
  }
  return query;
}
