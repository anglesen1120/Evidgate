import { inject, Injectable } from '@angular/core';
import {
  listSecurityEvents,
  type ListSecurityEventsData,
  type SecurityEventItem,
  type SecurityEventList,
} from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';

export type SecurityEventsQuery = NonNullable<ListSecurityEventsData['query']>;

/** Preserve runtime event names introduced by a newer server without changing the generated query enum. */
export type SecurityEventRow = Omit<SecurityEventItem, 'type'> & { type: string };
export type SecurityEventsPageData = Omit<SecurityEventList, 'items'> & {
  items: SecurityEventRow[];
};

@Injectable({ providedIn: 'root' })
export class SecurityEventsDataService {
  private readonly gateway = inject(ApiGateway);

  list(
    orgId: string,
    query: SecurityEventsQuery,
    signal?: AbortSignal,
  ): Promise<SecurityEventsPageData> {
    return this.gateway.call((client) =>
      listSecurityEvents({ client, path: { o: orgId }, query, ...(signal ? { signal } : {}) }),
    );
  }
}
