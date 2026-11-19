import { inject, Injectable } from '@angular/core';
import {
  listAuthoringTree,
  listChecklistItems,
  listManualCases,
  type AuthoringTreeResponse,
  type ChecklistItemListResponse,
  type ListAuthoringTreeData,
  type ListChecklistItemsData,
  type ListManualCasesData,
  type ManualCaseListResponse,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

/** The wire query each authoring read accepts; the SDK names the members. */
export type AuthoringTreeQuery = NonNullable<ListAuthoringTreeData['query']>;
export type ChecklistItemQuery = NonNullable<ListChecklistItemsData['query']>;
export type ManualCaseQuery = NonNullable<ListManualCasesData['query']>;

/**
 * The authoring reads on the wire. Only `core` may name an SDK operation, so the feature data
 * service composes these instead of calling the SDK itself; every call runs through
 * `ApiGateway`, which owns the typed refusal, the cancellation and the session hook.
 */
@Injectable({ providedIn: 'root' })
export class AuthoringReadsService {
  private readonly gateway = inject(ApiGateway);

  listTree(
    projectId: string,
    query: AuthoringTreeQuery,
    signal: AbortSignal,
  ): Promise<AuthoringTreeResponse> {
    return this.gateway.call((client) =>
      listAuthoringTree({ client, path: { p: projectId }, query, signal }),
    );
  }

  listChecklistItems(
    projectId: string,
    query: ChecklistItemQuery,
    signal: AbortSignal,
  ): Promise<ChecklistItemListResponse> {
    return this.gateway.call((client) =>
      listChecklistItems({ client, path: { p: projectId }, query, signal }),
    );
  }

  listManualCases(
    projectId: string,
    query: ManualCaseQuery,
    signal: AbortSignal,
  ): Promise<ManualCaseListResponse> {
    return this.gateway.call((client) =>
      listManualCases({ client, path: { p: projectId }, query, signal }),
    );
  }
}
