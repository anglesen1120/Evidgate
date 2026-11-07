// The resume position and the step moves the shell depends on: a reload resumes on the first
// incomplete step, `canGoNext` follows the viewed step, and only a user move cues focus.

import { OnboardingStore } from './onboarding.store';
import {
  BINDING,
  bindingPage,
  FakeOnboardingDataService,
  PROJECT,
  runState,
  runSummary,
  setupOnboardingStore,
} from './onboarding.store.test-support';

describe('OnboardingStore (09.2.1-T1.1/T2)', () => {
  let data: FakeOnboardingDataService;
  let store: InstanceType<typeof OnboardingStore>;

  beforeEach(() => {
    ({ data, store } = setupOnboardingStore());
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
});
