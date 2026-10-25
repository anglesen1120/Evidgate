// 09.2.1-T1.1 (plan 09.2.1 §9 "onboarding.store.spec.ts: seeded project+binding+no-run → resumes at
// step 3 (AC1)" and "Token resume within validity window skips re-issuance (AC7)", §10 T1.1;
// decisions (d) and (f)): the wizard store over the real derivation, with only the data layer and
// the session store doubled.
//
// The cases are the decisions the shell depends on:
//   - a reload resumes on the first incomplete step, derived from server state (AC1);
//   - `canGoNext` follows the *viewed* step's done-condition, and `selectStep` cannot open a step
//     the server has not reached (T1.3's disabled `onboarding-next`);
//   - a failed resume is a `failure`, never a step: no case may publish progress from a refusal;
//   - a superseded load never publishes, whichever load answers first;
//   - `projectCreated` adopts the project into the session cache and resumes on step 2, and never
//     throws when the follow-up read fails — the create already succeeded;
//   - AC7: resuming a project that already has an ingest token reaches step 3 without issuing
//     anything (the wizard has no token-creation path at all).

import { TestBed } from '@angular/core/testing';
import type {
  CiBindingListResponse,
  CiBindingView,
  IngestTokenListResponse,
  IngestTokenView,
  ListProjectsData,
  ProjectDto,
  ProjectListResponse,
  RunPage,
  RunStateDto,
  RunSummary,
} from '@evidgate/sdk';

import { OnboardingDataService } from '../../core/data/onboarding-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import {
  OnboardingProgressService,
  type OnboardingProgress,
} from './data/onboarding-progress.service';
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

const PROJECT: ProjectDto = {
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

const BINDING: CiBindingView = {
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

const TOKEN: IngestTokenView = {
  id: 't-1',
  project_id: 'p-1',
  name: 'ci ingest',
  prefix: 'egt_ab12',
  status: 'active',
  scopes: ['run:create'],
  created_at: '2026-10-20T10:05:00Z',
};

function apiError(kind: ApiErrorKind, status: number): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: kind,
    message: `${kind} (test)`,
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}

function projectPage(items: readonly ProjectDto[]): ProjectListResponse {
  return { items: [...items], next_cursor: null };
}

function bindingPage(items: readonly CiBindingView[]): CiBindingListResponse {
  return { items: [...items], next_cursor: null };
}

function tokenPage(items: readonly IngestTokenView[]): IngestTokenListResponse {
  return { items: [...items], next_cursor: null };
}

function runSummary(runId: string, state: RunSummary['state']): RunSummary {
  return {
    run_id: runId,
    state,
    created_at: '2026-10-21T09:00:00Z',
    ref: RUN_REF,
    unverified_ref: false,
  };
}

function runState(runId: string, evidenceRevisionId: string | null): RunStateDto {
  return {
    run_id: runId,
    state: 'complete',
    current_evidence_revision_id: evidenceRevisionId,
    version: 1,
    purpose: 'regular',
    ref: RUN_REF,
    unverified_ref: false,
  };
}

/**
 * The data layer as the wizard reads it. Mutations throw: neither the store nor the derivation may
 * issue a resource, which is the shape AC7 pins (a resume re-issues nothing). `deferProjectReads`
 * holds the `getProject` answer of each load so two loads can be settled out of order.
 */
class FakeOnboardingDataService {
  projects: readonly ProjectDto[] = [];
  bindings: CiBindingListResponse = bindingPage([]);
  tokens: IngestTokenListResponse = tokenPage([]);
  runs: readonly RunSummary[] = [];
  readonly runStates = new Map<string, RunStateDto>();

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
class FakeSessionStore {
  readonly adopted: ProjectDto[] = [];

  adoptCreatedProject(project: ProjectDto): void {
    this.adopted.push(project);
  }
}

describe('OnboardingStore (09.2.1-T1.1)', () => {
  let data: FakeOnboardingDataService;
  let session: FakeSessionStore;
  let store: InstanceType<typeof OnboardingStore>;

  beforeEach(() => {
    data = new FakeOnboardingDataService();
    session = new FakeSessionStore();
    TestBed.configureTestingModule({
      providers: [
        { provide: OnboardingDataService, useValue: data },
        { provide: SessionStore, useValue: session },
        OnboardingStore,
      ],
    });
    store = TestBed.inject(OnboardingStore);
  });

  it('starts on step 1 while no resume has answered', () => {
    expect(store.status()).toBe('loading');
    expect(store.failure()).toBeNull();
    expect(store.currentStep()).toBe(1);
    expect(store.viewStep()).toBe(1);
    expect(store.canGoBack()).toBe(false);
    expect(store.canGoNext()).toBe(false);
  });

  it('resumes on the first incomplete step from server state (AC1)', async () => {
    // Seeded: project created, CI connected, no run yet — the AC1 resume position.
    data.projects = [PROJECT];
    data.bindings = bindingPage([BINDING]);

    await store.load('o-1', 'p-1');

    expect(store.status()).toBe('ready');
    expect(store.failure()).toBeNull();
    expect(store.project()).toEqual(PROJECT);
    expect(store.currentStep()).toBe(3);
    expect(store.viewStep()).toBe(3);
    expect(store.done()).toEqual({ 1: true, 2: true, 3: false, 4: false, 5: false });
    expect(store.canGoBack()).toBe(true);
    // Step 3 is not done, so the next control stays disabled (T1.3).
    expect(store.canGoNext()).toBe(false);
    // A resume on an explicit project reads it by id: the org's project list is not walked.
    expect(data.projectReadIds).toEqual(['p-1']);
    expect(data.projectCalls).toEqual([]);
    // Only reads reached the data layer.
    expect(data.mutations).toEqual([]);
  });

  it('gates next on the viewed step being done (T1.3)', async () => {
    // Seeded: project + binding + a complete run without an evidence revision → step 4, so steps
    // 1–3 are done and step 4 is the first open one.
    data.projects = [PROJECT];
    data.bindings = bindingPage([BINDING]);
    data.runs = [runSummary('r-1', 'complete')];
    data.runStates.set('r-1', runState('r-1', null));

    await store.load('o-1', 'p-1');
    expect(store.currentStep()).toBe(4);
    expect(store.viewStep()).toBe(4);
    // The viewed step is the first open one, so the next control stays disabled.
    expect(store.canGoNext()).toBe(false);

    // On a completed step next is enabled, and it walks forward one step at a time — it never
    // jumps over the open step 4.
    store.selectStep(2);
    expect(store.canGoNext()).toBe(true);
    store.next();
    expect(store.viewStep()).toBe(3);
    expect(store.canGoNext()).toBe(true);
    store.next();
    expect(store.viewStep()).toBe(4);
    expect(store.canGoNext()).toBe(false);
    expect(store.canGoBack()).toBe(true);

    store.back();
    expect(store.viewStep()).toBe(3);
  });

  it('cannot select a step past the derived current one', async () => {
    data.projects = [PROJECT];
    data.bindings = bindingPage([BINDING]);

    await store.load('o-1', 'p-1');
    expect(store.currentStep()).toBe(3);

    store.selectStep(5);
    expect(store.viewStep()).toBe(3);
    store.selectStep(2);
    expect(store.viewStep()).toBe(2);
    // Back is a no-op on step 1.
    store.selectStep(1);
    store.back();
    expect(store.viewStep()).toBe(1);
  });

  it('maps a forbidden refusal to the forbidden failure', async () => {
    data.projectFailure = apiError('forbidden', 403);

    await store.load('o-1', 'p-1');

    expect(store.status()).toBe('error');
    expect(store.failure()).toBe('forbidden');
    // A refusal never leaves a derivation behind that a panel could render.
    expect(store.progress()).toBeNull();
  });

  it('maps a not_found refusal to the not-found failure', async () => {
    data.projects = [PROJECT];
    data.bindingFailure = apiError('not_found', 404);

    await store.load('o-1', 'p-1');

    expect(store.status()).toBe('error');
    expect(store.failure()).toBe('not-found');
  });

  it('maps every other typed refusal to unavailable', async () => {
    data.projectFailure = apiError('unavailable', 503);

    await store.load('o-1', 'p-1');

    expect(store.status()).toBe('error');
    expect(store.failure()).toBe('unavailable');
  });

  it('maps a non-typed rejection to unavailable', async () => {
    data.projectFailure = new Error('the progress read broke');

    await store.load('o-1', 'p-1');

    expect(store.status()).toBe('error');
    expect(store.failure()).toBe('unavailable');
  });

  it('ignores a load that a newer load superseded', async () => {
    data.projects = [PROJECT];
    data.bindings = bindingPage([BINDING]);
    data.deferProjectReads = true;

    const superseded = store.load('o-1', 'p-1');
    const newest = store.load('o-1', 'p-1');
    expect(data.pendingProjects.length).toBe(2);

    // The newer load answers first and publishes step 3.
    data.settleProject(1, PROJECT);
    await newest;
    expect(store.status()).toBe('ready');
    expect(store.currentStep()).toBe(3);

    // The older load then answers with a refusal (the project read said 404): it is dropped.
    data.settleProject(0, null);
    await superseded;

    expect(store.status()).toBe('ready');
    expect(store.failure()).toBeNull();
    expect(store.currentStep()).toBe(3);
    expect(store.viewStep()).toBe(3);
  });

  it('adopts the created project, then resumes on step 2 (AC1)', async () => {
    // An org with no project yet resolves step 1.
    await store.load('o-1', null);
    expect(store.currentStep()).toBe(1);
    expect(store.viewStep()).toBe(1);

    // Step 1's page has created the project; the store adopts it and re-derives.
    data.projects = [PROJECT];
    await store.projectCreated(PROJECT);

    expect(session.adopted).toEqual([PROJECT]);
    expect(store.status()).toBe('ready');
    expect(store.currentStep()).toBe(2);
    expect(store.viewStep()).toBe(2);
    expect(store.project()).toEqual(PROJECT);
  });

  it('does not throw when the reload after a successful create fails', async () => {
    data.projectFailure = apiError('unavailable', 503);

    await expect(store.projectCreated(PROJECT)).resolves.toBeUndefined();

    expect(session.adopted).toEqual([PROJECT]);
    expect(store.status()).toBe('error');
    expect(store.failure()).toBe('unavailable');
  });

  it('resumes a project that already has an ingest token without re-issuing one (AC7)', async () => {
    data.projects = [PROJECT];
    data.tokens = tokenPage([TOKEN]);

    await store.load('o-1', 'p-1');

    expect(store.currentStep()).toBe(3);
    expect(store.done()[2]).toBe(true);
    // AC7: a resume is read-only — no create call exists on the store at all.
    expect(data.mutations).toEqual([]);
    expect('createIngestToken' in store).toBe(false);
  });
});

describe('OnboardingStore on the terminal screen (09.2.1-T1.1)', () => {
  const ENABLED: OnboardingProgress = {
    project: PROJECT,
    done: { 1: true, 2: true, 3: true, 4: true, 5: true },
    activationReadable: false,
    sampleRun: null,
    bindingCount: 1,
    tokenCount: 0,
    currentStep: 'enabled',
  };

  it('opens step 5 and allows no further step when the derivation is enabled', async () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: OnboardingProgressService, useValue: { load: () => Promise.resolve(ENABLED) } },
        { provide: SessionStore, useValue: new FakeSessionStore() },
        OnboardingStore,
      ],
    });
    const store = TestBed.inject(OnboardingStore);

    await store.load('o-1', 'p-1');

    expect(store.currentStep()).toBe('enabled');
    expect(store.viewStep()).toBe(5);
    expect(store.canGoBack()).toBe(true);
    expect(store.canGoNext()).toBe(false);
    store.next();
    expect(store.viewStep()).toBe(5);
    // With all five done, an earlier step is selectable, and next is enabled there because the
    // viewed step is done; on step 5 there is still nothing after it.
    store.selectStep(2);
    expect(store.viewStep()).toBe(2);
    expect(store.canGoNext()).toBe(true);
    store.next();
    expect(store.viewStep()).toBe(3);
  });
});
