// The one-shot create marker and `projectCreated`: a route-level singleton may spare the activation
// a create causes repeated reads, adopt the new project, and nothing else.

import { OnboardingStore } from './onboarding.store';
import {
  apiError,
  FakeOnboardingDataService,
  FakeSessionStore,
  PROJECT,
  setupOnboardingStore,
} from './onboarding.store.test-support';

describe('OnboardingStore (09.2.1-T1.1/T2)', () => {
  let data: FakeOnboardingDataService;
  let session: FakeSessionStore;
  let store: InstanceType<typeof OnboardingStore>;

  beforeEach(() => {
    ({ data, session, store } = setupOnboardingStore());
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
