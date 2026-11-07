import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';

import {
  CONNECT_LIST_LIMIT,
  OnboardingProgressService,
  type OnboardingProgress,
} from './onboarding-progress.service';
import {
  ACTIVATION,
  BINDING,
  FakeOnboardingDataService,
  PROJECT,
  TOKEN,
  bindingPage,
  runPage,
  settle,
  setupProgressService,
  tokenPage,
} from './onboarding-progress.service.test-support';

describe('OnboardingProgressService (09.2.1-T1.2)', () => {
  let data: FakeOnboardingDataService;
  let service: OnboardingProgressService;

  beforeEach(() => {
    ({ data, service } = setupProgressService());
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

  it('reads the bindings, tokens, runs and activation together, before any of them answers', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.deferProjectReads = true;

    let settled = false;
    const loading = service.load('o-1', 'p-1').then((progress) => {
      settled = true;
      return progress;
    });
    await settle();

    // All four requests are in flight while none has answered.
    expect(data.bindingCalls).toBe(1);
    expect(data.tokenCalls).toBe(1);
    expect(data.runCalls).toBe(1);
    expect(data.activationCalls).toBe(1);
    expect(settled).toBe(false);

    // They settle in the reverse order they were issued: the derivation waits for all four.
    data.settleActivation(ACTIVATION);
    data.settleRuns(runPage([]));
    data.settleTokens(tokenPage([TOKEN]));
    data.settleBindings(bindingPage([]));
    const progress = await loading;

    expect(progress.bindings).toEqual([]);
    expect(progress.tokens).toEqual([TOKEN]);
    expect(progress.activation).toEqual({ kind: 'active', state: ACTIVATION });
    expect(progress.currentStep).toBe(3);
  });
});
