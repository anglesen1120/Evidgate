import { inject, Injectable } from '@angular/core';
import {
  listCases,
  updateCase,
  type CaseDto,
  type CaseListResponse,
  type ListCasesData,
  type UpdateCaseRequest,
} from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';
import { createIntent, intentHeaders } from '../http/intent';

export type CaseListQuery = NonNullable<ListCasesData['query']>;

@Injectable({ providedIn: 'root' })
export class CaseListDataService {
  private readonly gateway = inject(ApiGateway);

  list(projectId: string, query: CaseListQuery, signal: AbortSignal): Promise<CaseListResponse> {
    return this.gateway.call((client) =>
      listCases({ client, path: { p: projectId }, query, signal }),
    );
  }

  /**
   * One case write (`PATCH /v1/cases/{c}`, `case:write`). `etag` is the `ETag` the read issued —
   * quoted, exactly as served — because the route marks `If-Match` required: sending the update
   * without it would be a 422 the caller cannot recover from. The idempotency key is minted here
   * because the store issues one PATCH per row action; a retry of the same action re-sends the
   * same body against a fresh read, which the row version already guards.
   */
  update(
    caseId: string,
    body: UpdateCaseRequest,
    signal: AbortSignal,
    etag?: string,
  ): Promise<CaseDto> {
    if (etag === undefined) {
      return Promise.reject(new Error('updateCase needs the case ETag the read issued'));
    }
    const headers = { 'If-Match': etag, ...intentHeaders(createIntent()) };
    return this.gateway.call((client) =>
      updateCase({ client, path: { c: caseId }, body, headers, signal }),
    );
  }
}
