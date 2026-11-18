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
  cancelRun,
  createRun,
  getRun,
  getRunDecision,
  listEvidenceRevisions,
  listRunAttempts,
  listRuns,
  type CreateRunRequest,
  type EvidenceRevisionPage,
  type ListEvidenceRevisionsData,
  type ListRunAttemptsData,
  type ListRunsData,
  type RunAttemptPage,
  type RunCreated,
  type RunPage,
  type RunStateDto,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';
import { intentHeaders, type Intent } from '../http/intent';

/** The query object of `listRuns` without its optionality, so the parameter can default to `{}`. */
type RunListQuery = NonNullable<ListRunsData['query']>;

type RunAttemptsQuery = NonNullable<ListRunAttemptsData['query']>;

type RunEvidenceRevisionsQuery = NonNullable<ListEvidenceRevisionsData['query']>;

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

  /** One page of a run's attempts (`GET /v1/runs/{r}/attempts`, keyset `(execution_key, attempt_no)`). */
  listAttempts(runId: string, query: RunAttemptsQuery = {}): Promise<RunAttemptPage> {
    return this.gateway.call((client) => listRunAttempts({ client, path: { r: runId }, query }));
  }

  /**
   * One page of a run's frozen evidence revisions
   * (`GET /v1/runs/{run_id}/evidence-revisions`, keyset `revision_no` descending).
   *
   * The generated `EvidenceRevision`/`EvidenceRevisionPage` types are current, so the page is
   * returned exactly as served: the store owns the cursor walk and never rewrites a revision.
   */
  listEvidenceRevisions(
    runId: string,
    query: RunEvidenceRevisionsQuery = {},
  ): Promise<EvidenceRevisionPage> {
    return this.gateway.call((client) =>
      listEvidenceRevisions({ client, path: { run_id: runId }, query }),
    );
  }

  /**
   * The run's live decision body (`GET /v1/runs/{r}/decision`).
   *
   * Deliberately `unknown`: the served value is validated by the feature's `readDecisionBody`
   * before anything reads a field, so a body that drifts from the contract renders the
   * contract-invalid state instead of a guessed verdict.
   */
  getDecision(runId: string): Promise<unknown> {
    return this.gateway.call((client) => getRunDecision({ client, path: { r: runId } }));
  }

  /**
   * Create a run (`POST /v1/projects/{p}/runs`). The operation requires an `Idempotency-Key`, so
   * the caller's intent supplies it: a retry of the same intent replays the original response
   * instead of creating a second run (00 §9.1).
   *
   * `signal` cancels the request when the user leaves the dialog; it is spread only when present
   * because the SDK option refuses an explicit `undefined` under `exactOptionalPropertyTypes`.
   */
  create(
    projectId: string,
    body: CreateRunRequest,
    intent: Intent,
    signal?: AbortSignal,
  ): Promise<RunCreated> {
    return this.gateway.call((client) =>
      createRun({
        client,
        path: { p: projectId },
        body,
        headers: intentHeaders(intent),
        ...(signal === undefined ? {} : { signal }),
      }),
    );
  }

  /**
   * Record a cancellation (`POST /v1/runs/{r}/cancel`, `202` with no body). The body is omitted
   * when the caller gave no reason, which the contract reads as "no reason given"; a run that is
   * already terminal answers the same 202, so there is nothing to reconcile here.
   */
  cancel(
    runId: string,
    reason: string | undefined,
    intent: Intent,
    signal?: AbortSignal,
  ): Promise<void> {
    return this.gateway
      .call((client) =>
        cancelRun({
          client,
          path: { r: runId },
          headers: intentHeaders(intent),
          ...(reason === undefined ? {} : { body: { reason } }),
          ...(signal === undefined ? {} : { signal }),
        }),
      )
      .then(() => undefined);
  }
}
