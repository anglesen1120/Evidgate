// Shared fixtures and the real HTTP harness keep the responsibility specs aligned.
// The absolute same-origin API URL also works with Node's Request implementation.
// Each test creates its own TestBed services and verifies outstanding requests.

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type {
  CiBindingCreatedResponse,
  CiBindingListResponse,
  CiBindingView,
  CreateCiBindingRequest,
  CreateIngestTokenRequest,
  IngestTokenCreatedResponse,
  IngestTokenListResponse,
  IngestTokenView,
  PolicyActivationDto,
  PolicyActivationStateDto,
  PolicyRevisionDto,
  PolicyRevisionV1,
  ProjectDto,
  ProjectListResponse,
  RunPage,
  RunStateDto,
} from '@evidgate/sdk';

import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { TenantStore } from '../session/tenant.store';
import { OnboardingDataService } from './onboarding-data.service';

export const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
export const PROJECTS_URL = `${API_BASE_URL}/orgs/o-1/projects`;

export const PROJECT: ProjectDto = {
  id: 'p-1',
  org_id: 'o-1',
  name: 'Storefront web',
  slug: 'storefront-web',
  status: 'active',
  default_environment_id: null,
  created_at: '2026-10-20T09:00:00Z',
  updated_at: '2026-10-20T09:00:00Z',
  version: 1,
  retention_days: null,
  monthly_run_quota: null,
  case_prefix: 'STOR',
};

export const PROJECT_PAGE: ProjectListResponse = { items: [PROJECT], next_cursor: null };
export const NO_BINDINGS: CiBindingListResponse = { items: [], next_cursor: null };
export const NO_TOKENS: IngestTokenListResponse = { items: [], next_cursor: null };
export const NO_RUNS: RunPage = { items: [], next_cursor: null };

/** The row a create answers: metadata, then the one-time value beside it. */
export const BINDING_VIEW: CiBindingView = {
  id: 'b-1',
  project_id: 'p-1',
  provider: 'github_actions',
  repository_id: 42,
  repository_full_name: 'acme/storefront-web',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: ['acme/storefront-web/.github/workflows/ci.yml@refs/heads/main'],
  status: 'pending_verification',
  created_at: '2026-10-20T10:00:00Z',
  version: 1,
};

export const BINDING_CREATED: CiBindingCreatedResponse = {
  binding: BINDING_VIEW,
  binding_challenge: 'egc_9f2c1d7b4a6e8f0a',
};

export const TOKEN_VIEW: IngestTokenView = {
  id: 't-1',
  project_id: 'p-1',
  name: 'ci ingest',
  prefix: 'egt_ab12',
  status: 'active',
  scopes: ['run:create', 'event:write'],
  created_at: '2026-10-20T10:05:00Z',
};

export const TOKEN_CREATED: IngestTokenCreatedResponse = {
  token: TOKEN_VIEW,
  secret: 'egt_ab12cd34ef56gh78',
  overlap_ends_at: null,
};

export const BINDING_BODY: CreateCiBindingRequest = {
  provider: 'github_actions',
  repository_id: 42,
  repository_full_name: 'acme/storefront-web',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: ['acme/storefront-web/.github/workflows/ci.yml@refs/heads/main'],
};

export const TOKEN_BODY: CreateIngestTokenRequest = { name: 'ci ingest' };

/** The same closed body the policy step sends. */
export const POLICY_BODY: PolicyRevisionV1 = {
  required_suites: [],
  required_cases: [],
  late_window_minutes: 1440,
  gating_refs: { default_branch: false },
};

export const POLICY_REVISION_ID = '018f0000-0000-7000-8000-00000000a004';

export const POLICY_REVISION: PolicyRevisionDto = {
  id: POLICY_REVISION_ID,
  project_id: 'p-1',
  revision_no: 4,
  hash: 'sha256:f250bb2c0d1e9a3b5c7d1f2a4b6c8d0e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b1c',
  state: 'draft',
  body: POLICY_BODY,
  created_by: '01990000-0000-7000-8000-000000000001',
  created_at: '2026-10-26T08:20:00Z',
  version: 1,
};

export const ACTIVATION: PolicyActivationDto = {
  project_id: 'p-1',
  policy_revision_id: POLICY_REVISION_ID,
  hash: POLICY_REVISION.hash,
  mode: 'advisory',
  activated_by: '01990000-0000-7000-8000-000000000001',
  activated_at: '2026-10-26T08:22:00Z',
  previous_policy_revision_id: null,
  changed: true,
};

/** The read's answer: the stored pointer, without the POST's two extra members. */
export const ACTIVATION_STATE: PolicyActivationStateDto = {
  project_id: ACTIVATION.project_id,
  policy_revision_id: ACTIVATION.policy_revision_id,
  hash: ACTIVATION.hash,
  mode: ACTIVATION.mode,
  activated_by: ACTIVATION.activated_by,
  activated_at: ACTIVATION.activated_at,
};

export const RUN_STATE: RunStateDto = {
  run_id: 'r-1',
  state: 'complete',
  current_evidence_revision_id: null,
  version: 2,
  purpose: 'regular',
  ref: {
    kind: null,
    name: null,
    pr_number: null,
    head_sha: null,
    base_sha: null,
    trigger_event: null,
    is_fork: false,
  },
  unverified_ref: false,
  manifest_hash: 'sha256:018f000000000000000000000000000000000000000000000000000000000000',
  suite_revision_id: '018f0000-0000-7000-8000-00000000b001',
  policy_revision_id: '018f0000-0000-7000-8000-00000000a004',
  environment: {
    revision_id: '018f0000-0000-7000-8000-00000000e001',
    environment_id: '018f0000-0000-7000-8000-00000000e000',
    environment_key: 'staging',
    revision_no: 3,
    revision_hash: 'sha256:018f000100000000000000000000000000000000000000000000000000000000',
  },
  shard_count: 2,
  declared_producers: ['shard-1', 'shard-2'],
  releases: [],
  after_cancel_events: 0,
  state_history: null,
};

/** The rejection a promise answered with, so a case can assert on it. */
export async function rejectionOf(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

export function setupOnboardingDataService(): {
  httpMock: HttpTestingController;
  service: OnboardingDataService;
} {
  TestBed.configureTestingModule({
    providers: [
      provideApi(API_BASE_URL),
      provideHttpClientTesting(),
      provideZonelessChangeDetection(),
      { provide: PlatformLocation, useClass: BrowserPlatformLocation },
    ],
  });
  const httpMock = TestBed.inject(HttpTestingController);
  const service = TestBed.inject(OnboardingDataService);
  // The tenant the session publishes after sign-in; the tenant interceptor reads it for the
  // routes that name no org on the path.
  TestBed.inject(TenantStore).setOrg('o-1');
  return { httpMock, service };
}
