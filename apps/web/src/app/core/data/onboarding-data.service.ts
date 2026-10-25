// 09.2.1-T1.1/T1.2 (plan 09.2.1 §5 "All calls go through the OnboardingDataService (W5) over
// @evidgate/sdk; no hand-written DTOs (FR-09-10)", §6, §10 T1.1/T1.2): the wizard's reads and its
// one mutation, in the shape of `core/data/runs-data.service.ts` — the generated `@evidgate/sdk`
// operation is called with the app's one client, `ApiGateway.call` decides success versus typed
// failure, and no wire type is declared here: every signature is an SDK type.
//
// The reads are the five inputs of the derivation (plan §6): the org's projects (which project the
// wizard resumes on), the project's CI bindings and ingest tokens (step 2), the project's runs
// (step 3, oldest first) and one run's current evidence revision (step 4). The `Intent` is the
// caller's: `steps/create-project.page.ts` mints one with `createIntent()` when the user presses
// submit and passes the same value to every retry, so a retried create replays the original
// response instead of creating a second project (00 §9.1, ADR-0008 rule 2).

import { inject, Injectable } from '@angular/core';
import {
  createProject,
  getProject,
  getRun,
  listCiBindings,
  listIngestTokens,
  listProjects,
  listRuns,
  type CiBindingListResponse,
  type CreateProjectRequest,
  type IngestTokenListResponse,
  type ListCiBindingsData,
  type ListIngestTokensData,
  type ListProjectsData,
  type ListRunsData,
  type ProjectDto,
  type ProjectListResponse,
  type RunPage,
  type RunStateDto,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';
import { intentHeaders, type Intent } from '../http/intent';

/** Each query object below without its optionality, so the parameter can default to `{}`. */
type ProjectListQuery = NonNullable<ListProjectsData['query']>;
type CiBindingListQuery = NonNullable<ListCiBindingsData['query']>;
type IngestTokenListQuery = NonNullable<ListIngestTokensData['query']>;
type RunListQuery = NonNullable<ListRunsData['query']>;

@Injectable({ providedIn: 'root' })
export class OnboardingDataService {
  private readonly gateway = inject(ApiGateway);

  /**
   * One page of an organization's projects (`GET /v1/orgs/{o}/projects`, 00 §9.1). The server
   * keyset-orders it by `(created_at, id)` ascending, so a `limit: 1` read answers the oldest
   * project — the wizard's default resume target (plan §10 T1.1, decision (d)).
   */
  listProjects(orgId: string, query: ProjectListQuery = {}): Promise<ProjectListResponse> {
    return this.gateway.call((client) => listProjects({ client, path: { o: orgId }, query }));
  }

  /**
   * Create a project (`POST /v1/orgs/{o}/projects`). The operation requires an `Idempotency-Key`,
   * so the caller's intent supplies it: a retry of the same intent replays the original 201
   * instead of creating a second project (00 §9.1).
   */
  createProject(orgId: string, body: CreateProjectRequest, intent: Intent): Promise<ProjectDto> {
    return this.gateway.call((client) =>
      createProject({ client, path: { o: orgId }, body, headers: intentHeaders(intent) }),
    );
  }

  /**
   * One project by id (`GET /v1/projects/{p}`): the exact resume target when the route names one,
   * in one call instead of a page walk. A project of another tenant is the same 404 an unknown id
   * is (00 §9.2, D-S68-Q4), and the progress service repeats the `org_id` check on the body.
   */
  getProject(projectId: string): Promise<ProjectDto> {
    return this.gateway.call((client) => getProject({ client, path: { p: projectId } }));
  }

  /** One page of the project's CI bindings (`GET /v1/projects/{p}/ci-bindings`, step 2). */
  listCiBindings(
    projectId: string,
    query: CiBindingListQuery = {},
  ): Promise<CiBindingListResponse> {
    return this.gateway.call((client) => listCiBindings({ client, path: { p: projectId }, query }));
  }

  /** One page of the project's ingest-token metadata (`GET /v1/projects/{p}/ingest-tokens`). */
  listIngestTokens(
    projectId: string,
    query: IngestTokenListQuery = {},
  ): Promise<IngestTokenListResponse> {
    return this.gateway.call((client) =>
      listIngestTokens({ client, path: { p: projectId }, query }),
    );
  }

  /**
   * One page of the project's runs (`GET /v1/projects/{p}/runs`), oldest first. A `limit: 1` read
   * answers the sample run of step 3.
   */
  listRuns(projectId: string, query: RunListQuery = {}): Promise<RunPage> {
    return this.gateway.call((client) => listRuns({ client, path: { p: projectId }, query }));
  }

  /** One run's state and current evidence revision (`GET /v1/runs/{r}`, step 4's input). */
  getRun(runId: string): Promise<RunStateDto> {
    return this.gateway.call((client) => getRun({ client, path: { r: runId } }));
  }
}
