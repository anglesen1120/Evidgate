// What a resume derives for the optional credential step: it never gates the wizard and never
// reads a credential inventory, so a refusal there cannot block the first run.

import { OnboardingStore } from './onboarding.store';
import {
  FakeOnboardingDataService,
  PROJECT,
  setupOnboardingStore,
} from './onboarding.store.test-support';

describe('OnboardingStore credential independence (09.2.1-T1.1/T2)', () => {
  let data: FakeOnboardingDataService;
  let store: InstanceType<typeof OnboardingStore>;

  beforeEach(() => {
    ({ data, store } = setupOnboardingStore());
  });

  it('resumes a project at the first run and leaves the optional step open', async () => {
    data.projects = [PROJECT];

    await store.load('o-1', 'p-1');

    expect(store.currentStep()).toBe(3);
    expect(store.done()[2]).toBe(false);
    // A resume is read-only: no create call exists on the store at all.
    expect(data.mutations).toEqual([]);
    expect('createIngestToken' in store).toBe(false);
  });

  it('exposes no credential state before or after a derivation', async () => {
    expect('tokens' in store).toBe(false);
    expect('bindings' in store).toBe(false);
    data.projects = [PROJECT];
    await store.load('o-1', 'p-1');
    expect('tokens' in store).toBe(false);
    expect('bindings' in store).toBe(false);
  });
});
