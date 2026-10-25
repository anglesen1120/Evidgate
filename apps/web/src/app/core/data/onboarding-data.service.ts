// 09.2.1-T1.1/T1.2/T2 (plan 09.2.1 §5 "All calls go through the OnboardingDataService (W5) over
// @evidgate/sdk; no hand-written DTOs (FR-09-10)", §6, §10 T1.1/T1.2/T2; AC2): the wizard's reads
// and its mutations, in the shape of `core/data/runs-data.service.ts` — the generated
// `@evidgate/sdk` operation is called with the app's one client, `ApiGateway.call` decides
// success versus typed failure, and no wire type is declared here: every signature is an SDK type.
//
// The reads are the five inputs of the derivation (plan §6): the org's projects (which project the
// wizard resumes on), the project's CI bindings and ingest tokens (step 2), the project's runs
// (step 3, oldest first) and one run's current evidence revision (step 4). The `Intent` is the
// caller's: `steps/create-project.page.ts` mints one with `createIntent()` when the user presses
// submit and passes the same value to every retry, so a retried create replays the original
// response instead of creating a second project (00 §9.1, ADR-0008 rule 2).
//
// Step 2's two connect paths (T2.1, AC2) are the three secret-bearing creates: a CI binding
// (`POST /v1/projects/{p}/ci-bindings`, whose answer carries the one-time `binding_challenge`) and
// an ingest token plus its rotation (`POST /v1/projects/{p}/ingest-tokens`,
// `POST /v1/ingest-tokens/{t}/rotate`, whose answers carry the one-time `secret`). None of the
// three takes an `Idempotency-Key` (contracts/openapi.yaml: a response carrying a one-time secret
// is never replayed), so none is minted for them and no `Intent` parameter exists: a caller guards
// one in-flight submit and maps a `409` through the typed error path instead.
// `POST /v1/ci/token` is CI's own pre-tenant exchange — the credential is the request — so the
// web never calls it.

import { inject, Injectable } from '@angular/core';
import {
  createCiBinding,
  createIngestToken,
  createProject,
  getProject,
  getRun,
  listCiBindings,
  listIngestTokens,
  listProjects,
  listRuns,
  rotateIngestToken,
  type CiBindingCreatedResponse,
  type CiBindingListResponse,
  type CreateCiBindingRequest,
  type CreateIngestTokenRequest,
  type CreateProjectRequest,
  type IngestTokenCreatedResponse,
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

  /**
   * One page of the project's CI bindings (`GET /v1/projects/{p}/ci-bindings`, step 2). The row it
   * answers is metadata only: the challenge is never listed, so a resume can show what exists
   * without being able to show the secret again (AC7, T2.2).
   */
  listCiBindings(
    projectId: string,
    query: CiBindingListQuery = {},
  ): Promise<CiBindingListResponse> {
    return this.gateway.call((client) => listCiBindings({ client, path: { p: projectId }, query }));
  }

  /**
   * Bind a repository to the project (`POST /v1/projects/{p}/ci-bindings`, step 2's GitHub OIDC
   * path, AC2; `ci_binding:write`). The answer carries `binding_challenge` exactly once (set as
   * the repository variable `EVIDGATE_BINDING_CHALLENGE`) and the binding starts
   * `pending_verification`; the route takes no `Idempotency-Key`, so the caller's single in-flight
   * submit is the guard and a `409` (`binding_exists`) arrives typed.
   *
   * `X-Evidgate-Org` travels with the request because the contract asks for it in prose — without
   * it "the org is the caller's only choice, so send it" — while the generated `headers` type
   * declares no such parameter. It is therefore passed as a header map rather than as an object
   * literal, which the SDK's excess-property check would reject.
   */
  createCiBinding(
    orgId: string,
    projectId: string,
    body: CreateCiBindingRequest,
  ): Promise<CiBindingCreatedResponse> {
    const headers: Record<string, string> = { 'X-Evidgate-Org': orgId };
    return this.gateway.call((client) =>
      createCiBinding({ client, path: { p: projectId }, body, headers }),
    );
  }

  /**
   * One page of the project's ingest-token metadata
   * (`GET /v1/projects/{p}/ingest-tokens`, step 2). Metadata only: the secret and its hash are
   * never listed, which is what makes a resume able to show a token without re-issuing it (AC7).
   */
  listIngestTokens(
    projectId: string,
    query: IngestTokenListQuery = {},
  ): Promise<IngestTokenListResponse> {
    return this.gateway.call((client) =>
      listIngestTokens({ client, path: { p: projectId }, query }),
    );
  }

  /**
   * Issue an ingest token (`POST /v1/projects/{p}/ingest-tokens`, step 2's token path, AC2;
   * `ingest_token:write`). The answer carries the plaintext `secret` exactly once and is never
   * replayed, so no `Idempotency-Key` is passed: the one-time value is what the reveal slot shows
   * and drops (T2.2), never something a replay could hand back.
   */
  createIngestToken(
    projectId: string,
    body: CreateIngestTokenRequest,
  ): Promise<IngestTokenCreatedResponse> {
    return this.gateway.call((client) =>
      createIngestToken({ client, path: { p: projectId }, body }),
    );
  }

  /**
   * Mint a successor for an ingest token (`POST /v1/ingest-tokens/{t}/rotate`, AC2;
   * `ingest_token:write`). The old token keeps verifying until the answer's `overlap_ends_at`, the
   * successor's plaintext is returned once, and — for the same reason as the create — the route
   * takes no `Idempotency-Key`.
   */
  rotateIngestToken(tokenId: string): Promise<IngestTokenCreatedResponse> {
    return this.gateway.call((client) => rotateIngestToken({ client, path: { t: tokenId } }));
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
