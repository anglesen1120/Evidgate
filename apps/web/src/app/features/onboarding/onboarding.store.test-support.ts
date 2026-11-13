// Shared doubles and fixtures of the onboarding store specs, which were split by responsibility so
// every file stays inside the size rule. The doubles are the data layer and the session cache as
// the store reads them; a spec asserts store behaviour, never a double's internals.

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

import { OnboardingDataService } from '../../core/data/onboarding-data.service';
import { SessionStore } from '../../core/session/session.store';
import { apiError } from '../../shared/testing/http-data.testing';
import { OnboardingStore } from './onboarding.store';

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
  scopes: ['run:create'],
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

export function runSummary(runId: string, state: RunSummary['state']): RunSummary {
  return {
    run_id: runId,
    state,
    created_at: '2026-10-21T09:00:00Z',
    ref: RUN_REF,
    unverified_ref: false,
    completeness: 'complete',
    parent_run_id: null,
    rerun_scope: null,
  };
}

export function runState(runId: string, evidenceRevisionId: string | null): RunStateDto {
  return {
    run_id: runId,
    state: 'complete',
    current_evidence_revision_id: evidenceRevisionId,
    version: 1,
    purpose: 'regular',
    ref: RUN_REF,
    unverified_ref: false,
    advisory_only: false,
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
  };
}

/**
 * The data layer as the wizard reads it. Mutations throw: neither the store nor the derivation may
 * issue a resource, which is the shape AC7 pins (a resume re-issues nothing). `deferProjectReads`
 * holds the `getProject` answer of each load so two loads can be settled out of order.
 */
export class FakeOnboardingDataService {
  projects: readonly ProjectDto[] = [];
  bindings: CiBindingListResponse = bindingPage([]);
  tokens: IngestTokenListResponse = tokenPage([]);
  runs: readonly RunSummary[] = [];
  readonly runStates = new Map<string, RunStateDto>();
  /** Step 5's read: `null` is the contract's `404` (no activation), the default state here. */
  activation: PolicyActivationStateDto | null = null;

  projectFailure: Error | null = null;
  bindingFailure: Error | null = null;

  /** Every call the store made, so a case can prove what was *not* issued. */
  readonly mutations: string[] = [];
  readonly projectCalls: ProjectListQuery[] = [];
  readonly projectReadIds: string[] = [];

  deferProjectReads = false;
  readonly pendingProjects: Array<(project: ProjectDto | null) => void> = [];

  settleProject(index: number, project: ProjectDto | null): void {
    this.pendingProjects[index]?.(project);
  }

  listProjects(_orgId: string, query: ProjectListQuery = {}): Promise<ProjectListResponse> {
    this.projectCalls.push(query);
    if (this.projectFailure !== null) {
      return Promise.reject(this.projectFailure);
    }
    return Promise.resolve(projectPage(this.projects));
  }

  getProject(projectId: string): Promise<ProjectDto> {
    this.projectReadIds.push(projectId);
    if (this.projectFailure !== null) {
      return Promise.reject(this.projectFailure);
    }
    if (this.deferProjectReads) {
      return new Promise<ProjectDto>((resolve, reject) => {
        this.pendingProjects.push((project) => {
          if (project === null) {
            reject(apiError('not_found', 404));
            return;
          }
          resolve(project);
        });
      });
    }
    const project = this.projects.find((candidate) => candidate.id === projectId);
    return project === undefined
      ? Promise.reject(apiError('not_found', 404))
      : Promise.resolve(project);
  }

  listCiBindings(): Promise<CiBindingListResponse> {
    if (this.bindingFailure !== null) {
      return Promise.reject(this.bindingFailure);
    }
    return Promise.resolve(this.bindings);
  }

  listIngestTokens(): Promise<IngestTokenListResponse> {
    return Promise.resolve(this.tokens);
  }

  listRuns(): Promise<RunPage> {
    return Promise.resolve({ items: [...this.runs], next_cursor: null });
  }

  getRun(runId: string): Promise<RunStateDto> {
    const state = this.runStates.get(runId);
    return state === undefined
      ? Promise.reject(apiError('not_found', 404))
      : Promise.resolve(state);
  }

  readPolicyActivation(): Promise<PolicyActivationStateDto | null> {
    return Promise.resolve(this.activation);
  }

  createProject(): Promise<never> {
    this.mutations.push('createProject');
    throw new Error('the wizard store must not create a project');
  }

  createIngestToken(): Promise<never> {
    this.mutations.push('createIngestToken');
    throw new Error('the wizard store must not issue an ingest token');
  }
}

/** The session cache as the store uses it: one adoption call, nothing else. */
export class FakeSessionStore {
  readonly adopted: ProjectDto[] = [];

  adoptCreatedProject(project: ProjectDto): void {
    this.adopted.push(project);
  }
}

export function setupOnboardingStore(): {
  data: FakeOnboardingDataService;
  session: FakeSessionStore;
  store: InstanceType<typeof OnboardingStore>;
} {
  const data = new FakeOnboardingDataService();
  const session = new FakeSessionStore();
  TestBed.configureTestingModule({
    providers: [
      { provide: OnboardingDataService, useValue: data },
      { provide: SessionStore, useValue: session },
      OnboardingStore,
    ],
  });
  return { data, session, store: TestBed.inject(OnboardingStore) };
}
