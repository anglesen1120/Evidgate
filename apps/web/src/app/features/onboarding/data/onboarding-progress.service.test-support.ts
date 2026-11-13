// Shared fixtures for the onboarding-progress specs: the fake data service, the server rows each
// seed needs, and the settled-read helpers. The specs split by responsibility, so what all of them
// depend on lives here rather than in each partial.

import { TestBed } from '@angular/core/testing';
import type {
  CiBindingListResponse,
  CiBindingView,
  IngestTokenListResponse,
  IngestTokenView,
  ListProjectsData,
  PolicyActivationStateDto,
  ProjectDto,
  ProjectListResponse,
  RunPage,
  RunStateDto,
  RunSummary,
} from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { apiError } from '../../../shared/testing/http-data.testing';
import { OnboardingProgressService } from './onboarding-progress.service';

type ProjectListQuery = NonNullable<ListProjectsData['query']>;

const RUN_REF: RunSummary['ref'] = {
  kind: null,
  name: null,
  pr_number: null,
  head_sha: null,
  base_sha: null,
  trigger_event: null,
  is_fork: false,
};

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

export const OTHER_ORG_PROJECT: ProjectDto = {
  ...PROJECT,
  id: 'q-1',
  org_id: 'o-2',
  slug: 'lumen-widgets',
  case_prefix: 'LUME',
};

/** The activation document the project-keyed read answers (07.1.1 F18), as step 5's proof. */
export const ACTIVATION: PolicyActivationStateDto = {
  project_id: 'p-1',
  policy_revision_id: '018f0000-0000-7000-8000-00000000a004',
  hash: 'sha256:f250bbce48fca2a9a5f93b45236c0f330bb471f5d3f0d1396ddc5ac964928064',
  mode: 'advisory',
  activated_by: '018f0000-0000-7000-8000-0000000000e1',
  activated_at: '2026-10-26T08:22:00Z',
};

export const BINDING: CiBindingView = {
  id: 'b-1',
  project_id: 'p-1',
  provider: 'github_actions',
  repository_id: 42,
  repository_full_name: 'acme/storefront-web',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: ['acme/storefront-web/.github/workflows/ci.yml@refs/heads/main'],
  status: 'active',
  created_at: '2026-10-20T10:00:00Z',
  version: 1,
};

export const TOKEN: IngestTokenView = {
  id: 't-1',
  project_id: 'p-1',
  name: 'ci ingest',
  prefix: 'egt_ab12',
  status: 'active',
  scopes: ['run:create', 'event:write'],
  created_at: '2026-10-20T10:05:00Z',
};

function projectPage(items: readonly ProjectDto[]): ProjectListResponse {
  return { items: [...items], next_cursor: null };
}

export function bindingPage(
  items: readonly CiBindingView[],
  nextCursor: string | null = null,
): CiBindingListResponse {
  return { items: [...items], next_cursor: nextCursor };
}

export function tokenPage(
  items: readonly IngestTokenView[],
  nextCursor: string | null = null,
): IngestTokenListResponse {
  return { items: [...items], next_cursor: nextCursor };
}

export function runPage(items: readonly RunSummary[]): RunPage {
  return { items: [...items], next_cursor: null };
}

export function runSummary(
  runId: string,
  createdAt: string,
  state: RunSummary['state'],
): RunSummary {
  return {
    run_id: runId,
    state,
    created_at: createdAt,
    ref: RUN_REF,
    unverified_ref: false,
    completeness: state === 'complete' ? 'complete' : null,
    parent_run_id: null,
    rerun_scope: null,
  };
}

export function runState(
  runId: string,
  state: RunStateDto['state'],
  evidenceRevisionId: string | null = null,
): RunStateDto {
  return {
    run_id: runId,
    state,
    current_evidence_revision_id: evidenceRevisionId,
    version: 1,
    purpose: 'regular',
    ref: RUN_REF,
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
    producers: [
      {
        producer_id: 'shard-1',
        identity_state: 'bound',
        finalize_status: 'finalized',
        last_sequence: 11,
        count: 11,
        expected: 11,
        stale_epoch_events: 0,
        after_cancel_events: 0,
      },
      {
        producer_id: 'shard-2',
        identity_state: 'bound',
        finalize_status: 'finalized',
        last_sequence: 9,
        count: 9,
        expected: 9,
        stale_epoch_events: 0,
        after_cancel_events: 0,
      },
    ],
    releases: [],
    after_cancel_events: 0,
    state_history: null,
    counts: { passed: 0, failed: 0, skipped: 0, timed_out: 0, error: 0, blocked: 0 },
    executions_page: { items: [], next_cursor: null },
  };
}

/** A data-service double: seeded answers, per-operation failures, and deferred reads. */
export class FakeOnboardingDataService {
  /** The rows `listProjects` answers, per org — the no-projectId path only. */
  readonly projectsByOrg = new Map<string, readonly ProjectDto[]>();
  /** The rows `getProject` answers, by id — the explicit projectId path. */
  readonly projectsById = new Map<string, ProjectDto>();

  bindings: CiBindingListResponse = bindingPage([]);
  tokens: IngestTokenListResponse = tokenPage([]);
  runs: RunPage = runPage([]);
  readonly runStates = new Map<string, RunStateDto>();
  /** What `readPolicyActivation` answers; `null` is the contract's `404` (no activation). */
  activation: PolicyActivationStateDto | null = null;

  projectFailure: Error | null = null;
  projectReadFailure: Error | null = null;
  bindingFailure: Error | null = null;
  tokenFailure: Error | null = null;
  runStateFailure: Error | null = null;
  activationFailure: Error | null = null;

  readonly projectCalls: Array<{ orgId: string; query: ProjectListQuery }> = [];
  readonly projectReadIds: string[] = [];
  readonly bindingQueries: unknown[] = [];
  readonly tokenQueries: unknown[] = [];
  readonly runQueries: unknown[] = [];
  readonly activationIds: string[] = [];
  bindingCalls = 0;
  tokenCalls = 0;
  runCalls = 0;
  activationCalls = 0;
  readonly runIds: string[] = [];

  /** When true the four project reads answer only when the test settles their slots. */
  deferProjectReads = false;
  readonly pendingBindings: Array<(page: CiBindingListResponse) => void> = [];
  readonly pendingTokens: Array<(page: IngestTokenListResponse) => void> = [];
  readonly pendingRuns: Array<(page: RunPage) => void> = [];
  readonly pendingActivations: Array<(state: PolicyActivationStateDto | null) => void> = [];

  settleBindings(page: CiBindingListResponse): void {
    this.pendingBindings.shift()?.(page);
  }

  settleTokens(page: IngestTokenListResponse): void {
    this.pendingTokens.shift()?.(page);
  }

  settleRuns(page: RunPage): void {
    this.pendingRuns.shift()?.(page);
  }

  settleActivation(state: PolicyActivationStateDto | null): void {
    this.pendingActivations.shift()?.(state);
  }

  listProjects(orgId: string, query: ProjectListQuery = {}): Promise<ProjectListResponse> {
    this.projectCalls.push({ orgId, query });
    if (this.projectFailure !== null) {
      return Promise.reject(this.projectFailure);
    }
    return Promise.resolve(projectPage(this.projectsByOrg.get(orgId) ?? []));
  }

  getProject(projectId: string): Promise<ProjectDto> {
    this.projectReadIds.push(projectId);
    if (this.projectReadFailure !== null) {
      return Promise.reject(this.projectReadFailure);
    }
    const project = this.projectsById.get(projectId);
    return project === undefined
      ? Promise.reject(apiError('not_found', 404))
      : Promise.resolve(project);
  }

  listCiBindings(_projectId: string, query: unknown = {}): Promise<CiBindingListResponse> {
    this.bindingCalls += 1;
    this.bindingQueries.push(query);
    if (this.bindingFailure !== null) {
      return Promise.reject(this.bindingFailure);
    }
    if (this.deferProjectReads) {
      return new Promise<CiBindingListResponse>((resolve) => this.pendingBindings.push(resolve));
    }
    return Promise.resolve(this.bindings);
  }

  listIngestTokens(_projectId: string, query: unknown = {}): Promise<IngestTokenListResponse> {
    this.tokenCalls += 1;
    this.tokenQueries.push(query);
    if (this.tokenFailure !== null) {
      return Promise.reject(this.tokenFailure);
    }
    if (this.deferProjectReads) {
      return new Promise<IngestTokenListResponse>((resolve) => this.pendingTokens.push(resolve));
    }
    return Promise.resolve(this.tokens);
  }

  listRuns(_projectId: string, query: unknown = {}): Promise<RunPage> {
    this.runCalls += 1;
    this.runQueries.push(query);
    if (this.deferProjectReads) {
      return new Promise<RunPage>((resolve) => this.pendingRuns.push(resolve));
    }
    return Promise.resolve(this.runs);
  }

  getRun(runId: string): Promise<RunStateDto> {
    this.runIds.push(runId);
    if (this.runStateFailure !== null) {
      return Promise.reject(this.runStateFailure);
    }
    const state = this.runStates.get(runId);
    return state === undefined
      ? Promise.reject(apiError('not_found', 404))
      : Promise.resolve(state);
  }

  readPolicyActivation(projectId: string): Promise<PolicyActivationStateDto | null> {
    this.activationCalls += 1;
    this.activationIds.push(projectId);
    if (this.activationFailure !== null) {
      return Promise.reject(this.activationFailure);
    }
    if (this.deferProjectReads) {
      return new Promise<PolicyActivationStateDto | null>((resolve) =>
        this.pendingActivations.push(resolve),
      );
    }
    return Promise.resolve(this.activation);
  }
}

/** Lets every pending microtask (the service's awaits) run before the next assertion. */
export async function settle(): Promise<void> {
  await new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

/** The rejection a promise answered with, so a case can assert on it. */
export async function rejectionOf(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

export function setupProgressService(): {
  data: FakeOnboardingDataService;
  service: OnboardingProgressService;
} {
  const data = new FakeOnboardingDataService();
  TestBed.configureTestingModule({
    providers: [{ provide: OnboardingDataService, useValue: data }],
  });
  return { data, service: TestBed.inject(OnboardingProgressService) };
}
