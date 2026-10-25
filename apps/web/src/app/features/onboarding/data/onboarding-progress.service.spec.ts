// 09.2.1-T1.2/T2 (plan 09.2.1 §9 "unit test: five independent seed combinations each resolve the
// documented step", §10 T1.2/T2; decisions (b) and (d); AC1, AC2, AC7): the derivation that turns
// server resources into the wizard's position, with the data service stubbed so each seed is a
// single fact to read.
//
// What is pinned here:
//   - the three project reads are issued together, not one after another (a resume is one round
//     trip after the project, not three), each with the page size the panel needs
//     (`CONNECT_LIST_LIMIT` rows for step 2's two lists, `limit: 1` for the oldest run);
//   - step 2 completes on *presence* — a binding with no token, a token with no binding — and the
//     progress carries the rows themselves, in server order, so step 2's panel can render metadata
//     for a project it resumed on (AC2, AC7: a resume re-issues nothing); each list also reports
//     whether the server held more rows than the page it answered (`next_cursor`), so the panel
//     never presents one page as the project's whole inventory;
//   - step 3 completes only on a *terminal collection* state — `incomplete` counts, `canceled`
//     does not (plan §6: "canceled in the wizard shows a retry control, never treated as complete");
//   - step 4 reads the evidence revision of the oldest run, never the newest one;
//   - step 5 is never done and `activationReadable` is always false, because the contract has no
//     project-keyed read of the activation (decision (b)) — the wizard must not guess `true`;
//   - a refusal arrives as the same typed error the gateway produced;
//   - a project id that the org's own list does not hold is `not_found`, so a guessed URL can
//     never resume another tenant's wizard.

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

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import {
  ApiRequestError,
  isApiRequestError,
  type ApiErrorKind,
} from '../../../core/http/api-error';
import {
  CONNECT_LIST_LIMIT,
  OnboardingProgressService,
  type OnboardingProgress,
} from './onboarding-progress.service';

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

const OTHER_ORG_PROJECT: ProjectDto = {
  ...PROJECT,
  id: 'q-1',
  org_id: 'o-2',
  slug: 'lumen-widgets',
  case_prefix: 'LUME',
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
  scopes: ['run:create', 'event:write'],
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

function bindingPage(
  items: readonly CiBindingView[],
  nextCursor: string | null = null,
): CiBindingListResponse {
  return { items: [...items], next_cursor: nextCursor };
}

function tokenPage(
  items: readonly IngestTokenView[],
  nextCursor: string | null = null,
): IngestTokenListResponse {
  return { items: [...items], next_cursor: nextCursor };
}

function runPage(items: readonly RunSummary[]): RunPage {
  return { items: [...items], next_cursor: null };
}

function runSummary(runId: string, createdAt: string, state: RunSummary['state']): RunSummary {
  return { run_id: runId, state, created_at: createdAt, ref: RUN_REF, unverified_ref: false };
}

function runState(
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
  };
}

/** A data-service double: seeded answers, per-operation failures, and deferred reads. */
class FakeOnboardingDataService {
  /** The rows `listProjects` answers, per org — the no-projectId path only. */
  readonly projectsByOrg = new Map<string, readonly ProjectDto[]>();
  /** The rows `getProject` answers, by id — the explicit projectId path. */
  readonly projectsById = new Map<string, ProjectDto>();

  bindings: CiBindingListResponse = bindingPage([]);
  tokens: IngestTokenListResponse = tokenPage([]);
  runs: RunPage = runPage([]);
  readonly runStates = new Map<string, RunStateDto>();

  projectFailure: Error | null = null;
  projectReadFailure: Error | null = null;
  bindingFailure: Error | null = null;
  runStateFailure: Error | null = null;

  readonly projectCalls: Array<{ orgId: string; query: ProjectListQuery }> = [];
  readonly projectReadIds: string[] = [];
  readonly bindingQueries: unknown[] = [];
  readonly tokenQueries: unknown[] = [];
  readonly runQueries: unknown[] = [];
  bindingCalls = 0;
  tokenCalls = 0;
  runCalls = 0;
  readonly runIds: string[] = [];

  /** When true the three project reads answer only when the test settles their slots. */
  deferProjectReads = false;
  readonly pendingBindings: Array<(page: CiBindingListResponse) => void> = [];
  readonly pendingTokens: Array<(page: IngestTokenListResponse) => void> = [];
  readonly pendingRuns: Array<(page: RunPage) => void> = [];

  settleBindings(page: CiBindingListResponse): void {
    this.pendingBindings.shift()?.(page);
  }

  settleTokens(page: IngestTokenListResponse): void {
    this.pendingTokens.shift()?.(page);
  }

  settleRuns(page: RunPage): void {
    this.pendingRuns.shift()?.(page);
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
}

/** Lets every pending microtask (the service's awaits) run before the next assertion. */
async function settle(): Promise<void> {
  await new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

/** The rejection a promise answered with, so a case can assert on it. */
async function rejectionOf(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

describe('OnboardingProgressService (09.2.1-T1.2)', () => {
  let data: FakeOnboardingDataService;
  let service: OnboardingProgressService;

  beforeEach(() => {
    data = new FakeOnboardingDataService();
    TestBed.configureTestingModule({
      providers: [{ provide: OnboardingDataService, useValue: data }],
    });
    service = TestBed.inject(OnboardingProgressService);
  });

  it('resumes on step 3 for a project with a CI binding and no run (AC1)', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);

    const progress: OnboardingProgress = await service.load('o-1', 'p-1');

    expect(progress.project).toEqual(PROJECT);
    expect(progress.done).toEqual({ 1: true, 2: true, 3: false, 4: false, 5: false });
    expect(progress.currentStep).toBe(3);
    expect(progress.sampleRun).toBeNull();
    // A binding with no token completes step 2, and the progress carries the row itself: step 2's
    // panel renders metadata for what the project already has (AC2), never a secret again (AC7).
    expect(progress.bindings).toEqual([BINDING]);
    expect(progress.tokens).toEqual([]);
    // The explicit project is one `getProject` call, not a walk of the org's project list. Step 2's
    // two lists are read with the page size the panel shows; the run read still asks for one row.
    expect(data.projectReadIds).toEqual(['p-1']);
    expect(data.projectCalls).toEqual([]);
    expect(data.bindingQueries).toEqual([{ limit: CONNECT_LIST_LIMIT }]);
    expect(data.tokenQueries).toEqual([{ limit: CONNECT_LIST_LIMIT }]);
    expect(data.runQueries).toEqual([{ limit: 1 }]);
  });

  it('resumes on step 3 for a project with an ingest token and no run (AC2)', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.tokens = tokenPage([TOKEN]);

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[2]).toBe(true);
    expect(progress.tokens).toEqual([TOKEN]);
    expect(progress.bindings).toEqual([]);
    expect(progress.currentStep).toBe(3);
  });

  it('carries the returned bindings and tokens in the order the server answered them', async () => {
    data.projectsById.set('p-1', PROJECT);
    // Server order is `(created_at, id)` ascending; the derivation must not sort, filter or
    // deduplicate: the panel renders what the server listed, and a re-order would make the rows
    // disagree with a later page of the same list.
    const secondBinding: CiBindingView = {
      ...BINDING,
      id: 'b-2',
      repository_full_name: 'acme/web',
    };
    const secondToken: IngestTokenView = { ...TOKEN, id: 't-2', prefix: 'egt_cd34' };
    data.bindings = bindingPage([BINDING, secondBinding]);
    data.tokens = tokenPage([TOKEN, secondToken]);

    const progress = await service.load('o-1', 'p-1');

    expect(progress.bindings.map((row) => row.id)).toEqual(['b-1', 'b-2']);
    expect(progress.tokens.map((row) => row.id)).toEqual(['t-1', 't-2']);
    // A second row changes nothing: `done[2]` is presence, and a binding's `status` is metadata
    // the wizard renders as the server reported it — it never re-derives it.
    expect(progress.done[2]).toBe(true);
  });

  it('leaves step 2 open for a project with neither a binding nor a token', async () => {
    data.projectsById.set('p-1', PROJECT);

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[2]).toBe(false);
    expect(progress.currentStep).toBe(2);
    expect(progress.bindings).toEqual([]);
    expect(progress.tokens).toEqual([]);
  });

  it('reports each list as truncated only when its answer carried a next cursor', async () => {
    data.projectsById.set('p-1', PROJECT);
    // The server answered one full page and a cursor: the rows are real, but they are not all of
    // them, which is what the panel's notice says.
    data.bindings = bindingPage([BINDING], 'cursor-b');
    data.tokens = tokenPage([TOKEN], 'cursor-t');

    const truncated = await service.load('o-1', 'p-1');

    expect(truncated.bindingsTruncated).toBe(true);
    expect(truncated.tokensTruncated).toBe(true);
    // Presence, not truncation, is the done-condition: the derivation itself is unchanged.
    expect(truncated.done[2]).toBe(true);
    expect(truncated.currentStep).toBe(3);

    // A list that ended on this page (`next_cursor: null`) is not truncated.
    data.bindings = bindingPage([BINDING]);
    data.tokens = tokenPage([TOKEN]);

    const whole = await service.load('o-1', 'p-1');

    expect(whole.bindingsTruncated).toBe(false);
    expect(whole.tokensTruncated).toBe(false);
  });

  it('reads the bindings, tokens and runs together, before any of them answers', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.deferProjectReads = true;

    let settled = false;
    const loading = service.load('o-1', 'p-1').then((progress) => {
      settled = true;
      return progress;
    });
    await settle();

    // All three requests are in flight while none has answered.
    expect(data.bindingCalls).toBe(1);
    expect(data.tokenCalls).toBe(1);
    expect(data.runCalls).toBe(1);
    expect(settled).toBe(false);

    // They settle in the reverse order they were issued: the derivation waits for all three.
    data.settleRuns(runPage([]));
    data.settleTokens(tokenPage([TOKEN]));
    data.settleBindings(bindingPage([]));
    const progress = await loading;

    expect(progress.bindings).toEqual([]);
    expect(progress.tokens).toEqual([TOKEN]);
    expect(progress.currentStep).toBe(3);
  });

  it('takes the oldest run of the page, not the first row it was handed', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([
      runSummary('r-new', '2026-10-22T09:00:00Z', 'complete'),
      runSummary('r-old', '2026-10-21T09:00:00Z', 'complete'),
    ]);
    data.runStates.set('r-old', runState('r-old', 'complete'));

    const progress = await service.load('o-1', 'p-1');

    expect(data.runIds).toEqual(['r-old']);
    expect(progress.sampleRun?.run_id).toBe('r-old');
  });

  it('completes step 3 for an incomplete run (terminal collection state)', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'incomplete')]);
    data.runStates.set('r-1', runState('r-1', 'incomplete'));

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[3]).toBe(true);
    expect(progress.done[4]).toBe(false);
    expect(progress.currentStep).toBe(4);
  });

  it('leaves step 4 open for a complete run without an evidence revision', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'complete')]);
    data.runStates.set('r-1', runState('r-1', 'complete'));

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[3]).toBe(true);
    expect(progress.done[4]).toBe(false);
    expect(progress.currentStep).toBe(4);
  });

  it('does not complete step 3 for a canceled run', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'canceled')]);
    data.runStates.set('r-1', runState('r-1', 'canceled'));

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[3]).toBe(false);
    expect(progress.currentStep).toBe(3);
  });

  it('completes step 4 on an evidence revision and never claims step 5', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'complete')]);
    data.runStates.set('r-1', runState('r-1', 'complete', 'rev-7'));

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[4]).toBe(true);
    expect(progress.currentStep).toBe(5);
    // Decision (b): the activation has no project-keyed read, so step 5 is never claimed done,
    // whatever the other resources say.
    expect(progress.done[5]).toBe(false);
    expect(progress.activationReadable).toBe(false);
  });

  it('answers step 1 without any project read when the org has no project', async () => {
    const progress = await service.load('o-1', null);

    expect(progress.project).toBeNull();
    expect(progress.currentStep).toBe(1);
    expect(progress.done).toEqual({ 1: false, 2: false, 3: false, 4: false, 5: false });
    // No project means neither step 2 list, and no request that would need one.
    expect(progress.bindings).toEqual([]);
    expect(progress.tokens).toEqual([]);
    // Neither list was read, so neither is reported as truncated.
    expect(progress.bindingsTruncated).toBe(false);
    expect(progress.tokensTruncated).toBe(false);
    // The oldest project is the only read: no project means no project-scoped request.
    expect(data.projectCalls).toEqual([{ orgId: 'o-1', query: { limit: 1 } }]);
    expect(data.bindingCalls).toBe(0);
    expect(data.tokenCalls).toBe(0);
    expect(data.runCalls).toBe(0);
  });

  it('answers the oldest project from one list read when the route names none', async () => {
    data.projectsByOrg.set('o-1', [PROJECT]);

    const progress = await service.load('o-1', null);

    expect(progress.project?.id).toBe('p-1');
    expect(progress.currentStep).toBe(2);
    expect(data.projectCalls).toEqual([{ orgId: 'o-1', query: { limit: 1 } }]);
    // The explicit-id read is never used on this path.
    expect(data.projectReadIds).toEqual([]);
  });

  it('propagates a 403 unchanged, from the project list and from a project read', async () => {
    const listFailure = apiError('forbidden', 403);
    data.projectFailure = listFailure;
    expect(await rejectionOf(service.load('o-1', null))).toBe(listFailure);

    data.projectFailure = null;
    data.projectsById.set('p-1', PROJECT);
    const bindingFailure = apiError('forbidden', 403);
    data.bindingFailure = bindingFailure;
    expect(await rejectionOf(service.load('o-1', 'p-1'))).toBe(bindingFailure);
  });

  it('propagates a 404 from the project read unchanged', async () => {
    const failure = apiError('not_found', 404);
    data.projectReadFailure = failure;

    const cause = await rejectionOf(service.load('o-1', 'p-1'));

    expect(cause).toBe(failure);
  });

  it('propagates a run refusal unchanged', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'complete')]);
    const failure = apiError('unavailable', 503);
    data.runStateFailure = failure;

    const cause = await rejectionOf(service.load('o-1', 'p-1'));

    expect(cause).toBe(failure);
  });

  it('treats a project of another org as not found', async () => {
    // A server answer that carries a foreign org_id: the id is not this org's project.
    data.projectsById.set('q-1', OTHER_ORG_PROJECT);

    const cause = await rejectionOf(service.load('o-1', 'q-1'));

    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
    }
    expect(data.bindingCalls).toBe(0);
  });

  it('treats a project id the server does not answer as not found, not step 1', async () => {
    const cause = await rejectionOf(service.load('o-1', 'p-unknown'));

    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
    }
    expect(data.bindingCalls).toBe(0);
  });
});
