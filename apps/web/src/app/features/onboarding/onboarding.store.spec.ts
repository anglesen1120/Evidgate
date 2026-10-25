// 09.2.1-T1.1/T2 (plan 09.2.1 §9 "onboarding.store.spec.ts: seeded project+binding+no-run → resumes
// at step 3 (AC1)" and "Token resume within validity window skips re-issuance (AC7)", §10 T1.1/T2;
// decisions (d) and (f); AC2): the wizard store over the real derivation, with only the data layer
// and the session store doubled.
//
// The cases are the decisions the shell and step 2 depend on:
//   - a reload resumes on the first incomplete step, derived from server state (AC1);
//   - `canGoNext` follows the *viewed* step's done-condition, and `selectStep` cannot open a step
//     the server has not reached (T1.3's disabled `onboarding-next`);
//   - a failed resume is a `failure`, never a step: no case may publish progress from a refusal;
//   - a superseded load never publishes, whichever load answers first;
//   - the store is a route-level singleton, so a `ready` state may belong to an earlier visit:
//     the only thing reusable across activations is the one-shot create marker `consumeCreated`
//     takes, which spares the activation a create causes a second read and nothing else (F1/N1),
//     and `clearCreated` drops it when the URL change it was set for never happened (N2);
//   - `focusToken` counts only the step moves a user caused, so a load or a Retry never moves
//     focus (S71 F2);
//   - `refresh` re-derives in place (AC2): the page — and the one-time secret it reveals — stays
//     mounted, so `status`, `viewStep` and `focusToken` never change, `done[2]` flips only when the
//     server says so, and a failure lands in `refreshFailure` without throwing or dropping the
//     derivation the page is rendering;
//   - `projectCreated` adopts the project into the session cache and resumes on step 2, and never
//     throws when the follow-up read fails — the create already succeeded;
//   - AC7: resuming a project that already has an ingest token reaches step 3 without issuing
//     anything, and exposes that token's metadata for the panel to render.

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
import { OnboardingStore, type OnboardingFailure } from './onboarding.store';

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

describe('OnboardingStore (09.2.1-T1.1/T2)', () => {
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

  it('consumes the create marker once, and only for the scope the create published (S71 F1/N1)', async () => {
    data.projects = [PROJECT];
    await store.projectCreated(PROJECT);
    expect(store.status()).toBe('ready');

    // Another org's root, and this org's own root, are not the scope the create published.
    expect(store.consumeCreated('o-2', null)).toBe(false);
    expect(store.consumeCreated(PROJECT.org_id, null)).toBe(false);
    // The exact scope is consumed once…
    expect(store.consumeCreated(PROJECT.org_id, PROJECT.id)).toBe(true);
    // …and the next activation of the same URL reads the server again.
    expect(store.consumeCreated(PROJECT.org_id, PROJECT.id)).toBe(false);
  });

  it('consumes nothing after a plain load, whatever the store still holds (S71 N1)', async () => {
    // The store is a route-level singleton: after an earlier activation it is `ready`, but that
    // is no licence to skip a read. Two orgs with no project in particular must not share a skip,
    // because `progress.project` is null for both and cannot identify the scope.
    data.projects = [PROJECT];
    await store.load('o-1', null);
    expect(store.status()).toBe('ready');
    expect(store.consumeCreated('o-1', null)).toBe(false);
    expect(store.consumeCreated('o-2', null)).toBe(false);

    await store.load('o-1', 'p-1');
    expect(store.consumeCreated('o-1', 'p-1')).toBe(false);
  });

  it('invalidates the create marker as soon as any load runs (S71 N1)', async () => {
    data.projects = [PROJECT];
    await store.projectCreated(PROJECT);

    // A revisit (or a Retry, or a route change) reads first and only then publishes.
    await store.load(PROJECT.org_id, PROJECT.id);

    expect(store.consumeCreated(PROJECT.org_id, PROJECT.id)).toBe(false);
  });

  it('leaves no marker after a failed load (S71 N1)', async () => {
    data.projects = [PROJECT];
    await store.projectCreated(PROJECT);

    data.projectFailure = apiError('unavailable', 503);
    await store.load(PROJECT.org_id, PROJECT.id);

    expect(store.status()).toBe('error');
    expect(store.consumeCreated(PROJECT.org_id, PROJECT.id)).toBe(false);
  });

  it('bumps the focus token only when a user move changes the viewed step (S71 F2)', async () => {
    data.projects = [PROJECT];
    data.bindings = bindingPage([BINDING]);

    // A load publishes step 3, but a load is not a user move: focus must stay where it is.
    await store.load('o-1', 'p-1');
    expect(store.viewStep()).toBe(3);
    expect(store.focusToken()).toBe(0);

    // Selecting the step already viewed moves nothing, so it cues nothing either.
    store.selectStep(3);
    expect(store.focusToken()).toBe(0);

    store.selectStep(2);
    expect(store.focusToken()).toBe(1);
    store.next();
    expect(store.viewStep()).toBe(3);
    expect(store.focusToken()).toBe(2);
    store.back();
    expect(store.focusToken()).toBe(3);
    // A refused move is no move.
    store.selectStep(5);
    expect(store.focusToken()).toBe(3);
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
    // A load publishes a step but is not a user move, so it cues no focus (S71 F2).
    expect(store.focusToken()).toBe(0);

    // Step 1's page has created the project; the store adopts it and re-derives.
    data.projects = [PROJECT];
    await store.projectCreated(PROJECT);

    expect(session.adopted).toEqual([PROJECT]);
    expect(store.status()).toBe('ready');
    expect(store.currentStep()).toBe(2);
    expect(store.viewStep()).toBe(2);
    expect(store.project()).toEqual(PROJECT);
    // The create's load is the one read after a creation: the activation its URL change causes
    // may take that result instead of reading again — once (S71 F1/N1).
    expect(store.consumeCreated(PROJECT.org_id, PROJECT.id)).toBe(true);
    // The create opened step 2 on the user's behalf, so it cues the shell's focus move; the
    // shell that the URL change activates is built after this, hence the token (S71 F2).
    expect(store.focusToken()).toBe(1);
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
    // AC7: a resume is read-only — no create call exists on the store at all, and the token is
    // shown from its metadata (prefix and status), never from a secret the store could hold.
    expect(data.mutations).toEqual([]);
    expect('createIngestToken' in store).toBe(false);
    expect(store.tokens()).toEqual([TOKEN]);
    expect(store.bindings()).toEqual([]);
  });

  it('exposes no step 2 row before a derivation answers', () => {
    // The shared empty answer: the panel renders its own empty state, not an undefined list.
    expect(store.bindings()).toEqual([]);
    expect(store.tokens()).toEqual([]);
    expect(store.refreshFailure()).toBeNull();
  });

  it('refresh publishes the new derivation without moving the page (AC2)', async () => {
    // Step 2 of a project with neither list: Next is disabled.
    data.projects = [PROJECT];
    await store.load('o-1', 'p-1');
    expect(store.currentStep()).toBe(2);
    expect(store.viewStep()).toBe(2);
    expect(store.canGoNext()).toBe(false);
    expect(store.focusToken()).toBe(0);

    // The page just created a binding, so it asks the store to re-derive. Only the derivation
    // changes: the page (with the one-time challenge it is revealing) stays mounted, so status,
    // view and focus stay exactly as they were (AC2).
    data.bindings = bindingPage([BINDING]);
    await store.refresh('o-1', 'p-1');

    expect(store.status()).toBe('ready');
    expect(store.failure()).toBeNull();
    expect(store.refreshFailure()).toBeNull();
    expect(store.viewStep()).toBe(2);
    expect(store.focusToken()).toBe(0);
    expect(store.done()[2]).toBe(true);
    expect(store.bindings()).toEqual([BINDING]);
    expect(store.canGoNext()).toBe(true);
    // Still only reads: a refresh derives, it never creates.
    expect(data.mutations).toEqual([]);
  });

  it('refresh never publishes the loading state while it is in flight (AC2)', async () => {
    data.projects = [PROJECT];
    await store.load('o-1', 'p-1');
    expect(store.status()).toBe('ready');

    data.deferProjectReads = true;
    const refreshing = store.refresh('o-1', 'p-1');
    await Promise.resolve();
    expect(data.pendingProjects.length).toBe(1);
    // A page that flashed `loading` here would unmount step 2 — and the secret it reveals.
    expect(store.status()).toBe('ready');
    expect(store.viewStep()).toBe(2);

    data.bindings = bindingPage([BINDING]);
    data.settleProject(0, PROJECT);
    await expect(refreshing).resolves.toBeUndefined();

    expect(store.status()).toBe('ready');
    expect(store.progress()?.bindings).toEqual([BINDING]);
  });

  it('refresh failures keep the derivation and map to the fixed patterns', async () => {
    data.projects = [PROJECT];
    await store.load('o-1', 'p-1');
    const published = store.progress();
    expect(published?.currentStep).toBe(2);

    const cases: Array<{ failure: Error; expected: OnboardingFailure }> = [
      { failure: apiError('forbidden', 403), expected: 'forbidden' },
      { failure: apiError('not_found', 404), expected: 'not-found' },
      { failure: apiError('unavailable', 503), expected: 'unavailable' },
      { failure: new Error('the refresh read broke'), expected: 'unavailable' },
    ];
    for (const { failure, expected } of cases) {
      data.bindingFailure = failure;
      // Never throws: the page keeps rendering what it had, plus the refresh notice.
      await expect(store.refresh('o-1', 'p-1')).resolves.toBeUndefined();
      expect(store.refreshFailure()).toBe(expected);
      expect(store.status()).toBe('ready');
      expect(store.failure()).toBeNull();
      expect(store.progress()).toBe(published);
      expect(store.viewStep()).toBe(2);
    }
  });

  it('drops a refresh that a newer load superseded', async () => {
    data.projects = [PROJECT];
    await store.load('o-1', 'p-1');
    expect(store.currentStep()).toBe(2);

    // A refresh and a load overlap: the load is the newer read, so its answer wins.
    data.deferProjectReads = true;
    const refreshing = store.refresh('o-1', 'p-1');
    const newest = store.load('o-1', 'p-1');
    await Promise.resolve();
    expect(data.pendingProjects.length).toBe(2);

    data.bindings = bindingPage([BINDING]);
    data.settleProject(1, PROJECT);
    await newest;
    expect(store.currentStep()).toBe(3);

    // The stale refresh then answers with the state from before the binding existed: dropped,
    // because it describes a project nobody is looking at any more.
    data.bindings = bindingPage([]);
    data.settleProject(0, PROJECT);
    await refreshing;

    expect(store.currentStep()).toBe(3);
    expect(store.bindings()).toEqual([BINDING]);
    expect(store.refreshFailure()).toBeNull();
  });

  it('load clears a refresh failure it supersedes', async () => {
    data.projects = [PROJECT];
    await store.load('o-1', 'p-1');
    data.bindingFailure = apiError('unavailable', 503);
    await store.refresh('o-1', 'p-1');
    expect(store.refreshFailure()).toBe('unavailable');

    data.bindingFailure = null;
    await store.load('o-1', 'p-1');

    expect(store.refreshFailure()).toBeNull();
    expect(store.status()).toBe('ready');
  });

  it('clearCreated drops the marker of a create whose URL change did not happen (N2)', async () => {
    data.projects = [PROJECT];
    await store.projectCreated(PROJECT);

    store.clearCreated();
    expect(store.consumeCreated(PROJECT.org_id, PROJECT.id)).toBe(false);

    // A later create arms it again: the marker belongs to the newest create only.
    await store.projectCreated(PROJECT);
    expect(store.consumeCreated(PROJECT.org_id, PROJECT.id)).toBe(true);
  });
});

describe('OnboardingStore on the terminal screen (09.2.1-T1.1)', () => {
  const ENABLED: OnboardingProgress = {
    project: PROJECT,
    done: { 1: true, 2: true, 3: true, 4: true, 5: true },
    activationReadable: false,
    sampleRun: null,
    bindings: [BINDING],
    tokens: [],
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
