// What step 2 renders from a derivation: the shared empty answer, the truncation hint, and a resume
// of a project that already holds an ingest token (AC7), which issues nothing.

import { OnboardingStore } from './onboarding.store';
import {
  BINDING,
  bindingPage,
  FakeOnboardingDataService,
  PROJECT,
  setupOnboardingStore,
  TOKEN,
  tokenPage,
} from './onboarding.store.test-support';

describe('OnboardingStore (09.2.1-T1.1/T2)', () => {
  let data: FakeOnboardingDataService;
  let store: InstanceType<typeof OnboardingStore>;

  beforeEach(() => {
    ({ data, store } = setupOnboardingStore());
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
    // Nothing was read, so nothing is being left out either.
    expect(store.bindingsTruncated()).toBe(false);
    expect(store.tokensTruncated()).toBe(false);
  });

  it('reports a step 2 list as truncated only when the server answered a next cursor', async () => {
    data.projects = [PROJECT];
    // The bindings page is full and carries a cursor: the panel renders these rows, but they are
    // not the project's whole inventory, and it says so instead of implying they are.
    data.bindings = bindingPage([BINDING], 'cursor-b');
    data.tokens = tokenPage([TOKEN]);

    await store.load('o-1', 'p-1');

    expect(store.bindingsTruncated()).toBe(true);
    expect(store.tokensTruncated()).toBe(false);
    // Truncation is a rendering hint, not a done-condition: step 2 still completes on presence.
    expect(store.done()[2]).toBe(true);
  });
});
