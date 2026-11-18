import { inject, Injectable } from '@angular/core';
import { listCases, type CaseListResponse, type ListCasesData } from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';

export type CaseListQuery = NonNullable<ListCasesData['query']>;

@Injectable({ providedIn: 'root' })
export class CaseListDataService {
  private readonly gateway = inject(ApiGateway);

  list(projectId: string, query: CaseListQuery, signal: AbortSignal): Promise<CaseListResponse> {
    return this.gateway.call((client) =>
      listCases({ client, path: { p: projectId }, query, signal }),
    );
  }
}
