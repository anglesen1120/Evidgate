// 09.1.1-T3.1 (00 §5.6 W5, FR-09-10; ADR-0008 decision (a) and rule 2): the reference data
// service. It is the pattern every feature service copies — the generated `@evidgate/sdk`
// operation is called with the app's one client, `ApiGateway.call` decides success versus typed
// failure, and no wire type is declared here: every signature is an SDK type.
//
// The `Intent` is the caller's: a page mints one with `createIntent()` when the user presses the
// button and passes the same value to every retry, so the SDK `headers` carry the caller-owned
// `Idempotency-Key` the server replays on (00 §9.1, ADR-0008 rule 2).

import { inject, Injectable } from '@angular/core';
import {
  createRun,
  getRun,
  listRuns,
  type CreateRunRequest,
  type ListRunsData,
  type RunCreated,
  type RunPage,
  type RunStateDto,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';
import { intentHeaders, type Intent } from '../http/intent';

/** The query object of `listRuns` without its optionality, so the parameter can default to `{}`. */
type RunListQuery = NonNullable<ListRunsData['query']>;

@Injectable({ providedIn: 'root' })
export class RunsDataService {
  private readonly gateway = inject(ApiGateway);

  /** One page of a project's runs (`GET /v1/projects/{p}/runs`, 00 §9.1 cursor pagination). */
  list(projectId: string, query: RunListQuery = {}): Promise<RunPage> {
    return this.gateway.call((client) => listRuns({ client, path: { p: projectId }, query }));
  }

  /** One run's state and current evidence revision (`GET /v1/runs/{r}`). */
  get(runId: string): Promise<RunStateDto> {
    return this.gateway.call((client) => getRun({ client, path: { r: runId } }));
  }

  /**
   * Create a run (`POST /v1/projects/{p}/runs`). The operation requires an `Idempotency-Key`, so
   * the caller's intent supplies it: a retry of the same intent replays the original response
   * instead of creating a second run (00 §9.1).
   */
  create(projectId: string, body: CreateRunRequest, intent: Intent): Promise<RunCreated> {
    return this.gateway.call((client) =>
      createRun({ client, path: { p: projectId }, body, headers: intentHeaders(intent) }),
    );
  }
}
