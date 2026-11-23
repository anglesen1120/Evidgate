import { inject, Injectable } from '@angular/core';
import {
  listRuns,
  listProjectWaivers,
  listQuarantineEntries,
  listCiBindings,
  listAuthoringTree,
  listManualCases,
  listAuthoringChanges,
  getUsageSummary,
  listAudit,
  type RunSummary,
} from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';

export interface DashboardRunSample {
  readonly items: readonly RunSummary[];
  readonly complete: boolean;
}

@Injectable({ providedIn: 'root' })
export class ProjectDashboardDataService {
  private readonly gateway = inject(ApiGateway);

  async runs(project: string, signal: AbortSignal): Promise<DashboardRunSample> {
    const items: RunSummary[] = [];
    let cursor: string | undefined;
    for (let page = 0; page < 5; page++) {
      const result = await this.gateway.call((client) =>
        listRuns({
          client,
          path: { p: project },
          query: { limit: 100, ...(cursor ? { cursor } : {}) },
          signal,
        }),
      );
      items.push(...result.items);
      if (result.next_cursor === null) return { items, complete: true };
      if (result.next_cursor === cursor) break;
      cursor = result.next_cursor;
    }
    return { items, complete: false };
  }

  waivers(project: string, signal: AbortSignal) {
    return this.gateway.call((client) =>
      listProjectWaivers({ client, path: { p: project }, query: { limit: 10 }, signal }),
    );
  }
  quarantine(project: string, signal: AbortSignal) {
    return this.gateway.call((client) =>
      listQuarantineEntries({ client, path: { p: project }, query: { limit: 10 }, signal }),
    );
  }
  bindings(project: string, signal: AbortSignal) {
    return this.gateway.call((client) =>
      listCiBindings({ client, path: { p: project }, query: { limit: 10 }, signal }),
    );
  }
  tree(project: string, signal: AbortSignal) {
    return this.gateway.call((client) =>
      listAuthoringTree({ client, path: { p: project }, query: { limit: 10 }, signal }),
    );
  }
  cases(project: string, signal: AbortSignal) {
    return this.gateway.call((client) =>
      listManualCases({ client, path: { p: project }, query: { limit: 20 }, signal }),
    );
  }
  changes(project: string, signal: AbortSignal) {
    return this.gateway.call((client) =>
      listAuthoringChanges({
        client,
        path: { p: project },
        query: { state: 'pending', limit: 20 },
        signal,
      }),
    );
  }
  usage(org: string, signal: AbortSignal) {
    return this.gateway.call(
      (client) => getUsageSummary({ client, path: { o: org }, query: { limit: 1 }, signal }),
      { reportQuota: false },
    );
  }
  audit(org: string, signal: AbortSignal) {
    return this.gateway.call((client) =>
      listAudit({ client, path: { o: org }, query: { limit: 3 }, signal }),
    );
  }
}
