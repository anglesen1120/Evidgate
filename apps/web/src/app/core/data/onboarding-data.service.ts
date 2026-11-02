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
//
// Step 5's two commands (09.2.1-T5.2, AC5) are the last two methods: the append of the default
// advisory policy (`POST /v1/projects/{p}/policies`, 07.1.1-T3) and the activation that moves the
// project's policy pointer to it in `advisory` mode
// (`POST /v1/projects/{p}/policy-activation`, 07.1.1-T4). The append takes no `Idempotency-Key` —
// the contract makes the content hash its idempotency — while the activation requires one, so the
// caller's `Intent` is passed to the second and only the second call. `PolicyDataService.publish`
// is the Settings area's append and quotes a cached head revision in `If-Match`; the wizard owns no
// head (it cannot read one, queue row F18), so its append never quotes one and stays here, where
// plan §5 line 73 puts every call the wizard makes.
//
// Step 5's read (09.2.1-T5.2 remainder) sits between them: `GET /v1/projects/{p}/policy-activation`
// (07.1.1 F18) answers the stored pointer, so the wizard can derive step 5 from the server instead
// of assuming it is never done. A `404` there is the contract's one "no activation" answer for
// every case it refuses to distinguish (00 §9.2) and becomes `null`; every other refusal stays the
// gateway's typed error.

import { inject, Injectable } from '@angular/core';
import {
  activatePolicyRevision,
  createCiBinding,
  createIngestToken,
  createPolicyRevision,
  createProject,
  getPolicyActivation,
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
  type PolicyActivationDto,
  type PolicyActivationStateDto,
  type PolicyRevisionDto,
  type PolicyRevisionV1,
  type ProjectDto,
  type ProjectListResponse,
  type RunPage,
  type RunStateDto,
} from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
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
   * `X-Evidgate-Org` is the interceptor's (`tenant-org.interceptor.ts`), never built here: the
   * generated `headers` type declares no such parameter anyway.
   */
  createCiBinding(
    projectId: string,
    body: CreateCiBindingRequest,
  ): Promise<CiBindingCreatedResponse> {
    return this.gateway.call((client) => createCiBinding({ client, path: { p: projectId }, body }));
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

  /**
   * Append one policy revision (`POST /v1/projects/{p}/policies`, step 5;
   * `policy:write`). No `Idempotency-Key` is supplied because the route asks for none: the server
   * canonicalises the body (RFC 8785), hashes it, and answers `200` with the revision already
   * stored for that hash, or `201` for a new one (07.1.1-T3). Sending the identical body again is
   * therefore idempotent by content, which is what lets a retried confirm re-run this step without
   * appending a second revision. (The platform's interceptor stamps an untyped mutation with a
   * random `Idempotency-Key` anyway — this route ignores it, so it neither helps nor collides: F6.)
   *
   * No `If-Match` is sent either: the wizard cannot read the project's head revision (the contract
   * has no such read) and a precondition it cannot quote would only produce `412`s. The Settings
   * area's publish (`PolicyDataService.publish`) is the caller that owns that `ETag`.
   */
  createPolicyRevision(projectId: string, body: PolicyRevisionV1): Promise<PolicyRevisionDto> {
    return this.gateway.call((client) =>
      createPolicyRevision({ client, path: { p: projectId }, body }),
    );
  }

  /**
   * The project's stored policy activation (`GET /v1/projects/{p}/policy-activation`, 07.1.1 F18,
   * step 5's read; `policy:read`). A `200` answers the pointer the server holds — the four stored
   * members plus the activated revision's `hash` — and nothing else (`PolicyActivationStateDto`:
   * migration `0033` stores no `previous_policy_revision_id` and no `changed`).
   *
   * A `404` is `null`, "no activation", and only that: the contract answers one undifferentiated
   * `404` for a project that was never activated, an unknown id, a soft-deleted project and
   * another tenant's project (00 §9.2), so there is no more specific fact to report. Every other
   * refusal (401, 403, 429, 5xx) is re-thrown as the gateway's typed `ApiRequestError` unchanged:
   * it says nothing about whether an activation exists, and folding it into `null` would let a
   * refused read look like an unactivated project.
   */
  async readPolicyActivation(projectId: string): Promise<PolicyActivationStateDto | null> {
    try {
      return await this.gateway.call((client) =>
        getPolicyActivation({ client, path: { p: projectId } }),
      );
    } catch (cause) {
      if (isApiRequestError(cause) && cause.error.status === 404) {
        return null;
      }
      throw cause;
    }
  }

  /**
   * Activate a policy revision (`POST /v1/projects/{p}/policy-activation`, step 5's confirm;
   * `policy:write` = `release_approver`, T98). The mode is always `advisory`: `enforcing` is
   * refused `422` until 07.7.1 and the wizard offers no mode choice, so the parameter does not
   * exist here.
   *
   * The route requires an `Idempotency-Key`, so the caller's intent supplies it: a double submit
   * and every retry of the same confirm send the same key and the server replays the stored answer
   * instead of writing a second `policy_activation` row. `changed: false` is a success — the
   * revision was already the active one — and arrives as a normal `PolicyActivationDto`.
   */
  activatePolicy(
    projectId: string,
    revisionId: string,
    intent: Intent,
  ): Promise<PolicyActivationDto> {
    return this.gateway.call((client) =>
      activatePolicyRevision({
        client,
        path: { p: projectId },
        body: { policy_revision_id: revisionId, mode: 'advisory' },
        headers: intentHeaders(intent),
      }),
    );
  }
}
