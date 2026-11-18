import { inject, Injectable } from '@angular/core';
import {
  getCase,
  listCases,
  updateCase,
  type CaseDto,
  type CaseListResponse,
  type ListCasesData,
  type UpdateCaseRequest,
} from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';
import { intentHeaders, type Intent } from '../http/intent';

export type CaseListQuery = NonNullable<ListCasesData['query']>;

/** One case read: the row, and the `ETag` the next `If-Match` must quote exactly as served. */
export interface CaseRead {
  readonly row: CaseDto;
  readonly etag: string;
}

@Injectable({ providedIn: 'root' })
export class CaseListDataService {
  private readonly gateway = inject(ApiGateway);

  list(projectId: string, query: CaseListQuery, signal: AbortSignal): Promise<CaseListResponse> {
    return this.gateway.call((client) =>
      listCases({ client, path: { p: projectId }, query, signal }),
    );
  }

  /**
   * One case (`GET /v1/cases/{c}`). The read is the only place the row's current `ETag` exists:
   * it travels as a response header, so a caller that must retry a refused write re-reads the
   * row here rather than guessing the header from the row version.
   */
  async get(caseId: string, signal: AbortSignal): Promise<CaseRead> {
    const { data, headers } = await this.gateway.callWithResponse((client) =>
      getCase({ client, path: { c: caseId }, signal }),
    );
    const etag = headers.get('ETag');
    if (etag === null) {
      throw new Error('getCase answered without the ETag the next If-Match needs');
    }
    return { row: data.case, etag };
  }

  /**
   * One case write (`PATCH /v1/cases/{c}`, `case:write`). `etag` is the `ETag` the read issued —
   * quoted, exactly as served — because the route marks `If-Match` required: sending the update
   * without it would be a 422 the caller cannot recover from. `intent` is the caller's key for
   * this user action, so a retry of the same action replays it instead of creating a second one.
   */
  update(
    caseId: string,
    body: UpdateCaseRequest,
    signal: AbortSignal,
    etag: string | undefined,
    intent: Intent,
  ): Promise<CaseDto> {
    if (etag === undefined) {
      return Promise.reject(new Error('updateCase needs the case ETag the read issued'));
    }
    const headers = { 'If-Match': etag, ...intentHeaders(intent) };
    return this.gateway.call((client) =>
      updateCase({ client, path: { c: caseId }, body, headers, signal }),
    );
  }
}
