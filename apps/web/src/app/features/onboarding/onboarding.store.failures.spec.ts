// Refusals are failures, never steps, and a superseded load never publishes: the wizard must not
// derive progress from an answer nobody is waiting for any more.

import { apiError } from '../../shared/testing/http-data.testing';
import { OnboardingStore } from './onboarding.store';
import {
  BINDING,
  bindingPage,
  FakeOnboardingDataService,
  PROJECT,
  setupOnboardingStore,
} from './onboarding.store.test-support';

describe('OnboardingStore (09.2.1-T1.1/T2)', () => {
  let data: FakeOnboardingDataService;
  let store: InstanceType<typeof OnboardingStore>;

  beforeEach(() => {
    ({ data, store } = setupOnboardingStore());
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
});
