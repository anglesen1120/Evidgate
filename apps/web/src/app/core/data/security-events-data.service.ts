import { inject, Injectable } from '@angular/core';
import {
  listSecurityEvents,
  type ListSecurityEventsData,
  type SecurityEventList,
} from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';

export type SecurityEventsQuery = NonNullable<ListSecurityEventsData['query']>;

@Injectable({ providedIn: 'root' })
export class SecurityEventsDataService {
  private readonly gateway = inject(ApiGateway);

  list(
    orgId: string,
    query: SecurityEventsQuery,
    signal?: AbortSignal,
  ): Promise<SecurityEventList> {
    return this.gateway.call((client) =>
      listSecurityEvents({ client, path: { o: orgId }, query, ...(signal ? { signal } : {}) }),
    );
  }
}
