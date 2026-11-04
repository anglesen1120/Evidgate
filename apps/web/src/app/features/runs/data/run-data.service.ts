// Reuse the core gateway so list and decision reads share typed error handling.
// UI refName maps to ref_name, and verdict filters map to lowercase wire values.

import { inject, Injectable } from '@angular/core';
import type { RunPage } from '@evidgate/sdk';

import { RunsDataService } from '../../../core/data/runs-data.service';
import { readDecisionBody, type DecisionBody } from './decision-body';

/** One request of the run list, including its server-side filters. */
export interface RunListQuery {
  /** The opaque `next_cursor` of the previous page; absent on the first read. */
  readonly cursor?: string;
  /** Rows per page; the store always passes its own {@link RUN_LIST_PAGE_LIMIT} (50). */
  readonly limit?: number;
  /** Exact `ref_name` of the run's recorded identity; absent when the screen has none. */
  readonly refName?: string;
  /** Newest live verdict, or runs without a live evaluation. */
  readonly decision?: 'pending' | 'PASS' | 'FAIL' | 'INCONCLUSIVE';
}

const DECISION_QUERY_VALUES = {
  pending: 'pending',
  PASS: 'pass',
  FAIL: 'fail',
  INCONCLUSIVE: 'inconclusive',
} as const;

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

  /**
   * The run's live decision body, validated: `null` when the served value is not a contract body.
   *
   * The gateway hands the value over untyped because the generated `Decision` type is stale (see
   * `core/data/runs-data.service.ts#getDecision`). `readDecisionBody` is the one place that turns it
   * into named members, so neither the store nor a component has to trust a wire value. A refusal
   * still arrives as the gateway's typed `ApiRequestError`; only a 2xx body reaches the parser.
   */
  async getDecision(runId: string): Promise<DecisionBody | null> {
    return readDecisionBody(await this.runs.getDecision(runId));
  }
}

/**
 * The query the request actually carries. `ref_name` is omitted when the screen holds no ref filter
 * (an empty value would be a 422, and "no filter" is the absence of the parameter), and `cursor` is
 * omitted on the first page for the same reason.
 */
function wireQuery(request: RunListQuery) {
  const query: {
    cursor?: string;
    limit?: number;
    ref_name?: string;
    decision?: Lowercase<NonNullable<RunListQuery['decision']>>;
  } = {};
  if (request.cursor !== undefined && request.cursor !== '') {
    query.cursor = request.cursor;
  }
  if (request.limit !== undefined) {
    query.limit = request.limit;
  }
  if (request.refName !== undefined && request.refName !== '') {
    query.ref_name = request.refName;
  }
  if (request.decision !== undefined) {
    query.decision = DECISION_QUERY_VALUES[request.decision];
  }
  return query;
}
