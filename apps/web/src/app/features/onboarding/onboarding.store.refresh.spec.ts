// `refresh` re-derives in place: the page and the secret it reveals stay mounted, its own sequence
// never supersedes the resume it overlaps, and a failure is a notice, not a lost derivation.

import type { CiBindingView } from '@evidgate/sdk';

import { apiError } from '../../shared/testing/http-data.testing';
import { OnboardingStore, type OnboardingFailure } from './onboarding.store';
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

  it('publishes the load a refresh overlaps, so the store cannot stay loading (F4)', async () => {
    data.projects = [PROJECT];
    data.deferProjectReads = true;

    // Step 2 asks for a refresh while the resume is still in flight: the store is `loading`.
    const loading = store.load('o-1', 'p-1');
    const refreshing = store.refresh('o-1', 'p-1');
    expect(data.pendingProjects.length).toBe(2);
    expect(store.status()).toBe('loading');

    // The load answers: it is the read that decides what is on screen, and the overlapped refresh
    // must not take that answer away from it — the bug was the store staying `loading` forever.
    data.bindings = bindingPage([BINDING]);
    data.settleProject(0, PROJECT);
    await loading;

    expect(store.status()).toBe('ready');
    expect(store.failure()).toBeNull();
    expect(store.currentStep()).toBe(3);
    expect(store.viewStep()).toBe(3);

    // The refresh then answers with the newer state; it patches the derivation only.
    data.bindings = bindingPage([BINDING, { ...BINDING, id: 'b-2' }]);
    data.settleProject(1, PROJECT);
    await refreshing;

    expect(store.status()).toBe('ready');
    expect(store.viewStep()).toBe(3);
    expect(store.bindings()).toHaveLength(2);
  });

  it('drops a refresh that a newer refresh superseded', async () => {
    data.projects = [PROJECT];
    data.bindings = bindingPage([BINDING]);
    await store.load('o-1', 'p-1');
    expect(store.bindings()).toEqual([BINDING]);

    // Two refreshes overlap; the newer one is the answer the page must end up showing.
    const second: CiBindingView = { ...BINDING, id: 'b-2', repository_full_name: 'acme/web' };
    data.deferProjectReads = true;
    const older = store.refresh('o-1', 'p-1');
    const newer = store.refresh('o-1', 'p-1');
    expect(data.pendingProjects.length).toBe(2);

    // The older refresh answers first with an empty list: dropped, because the newer one started
    // after it and that is the read whose answer describes the project now.
    data.bindings = bindingPage([]);
    data.settleProject(0, PROJECT);
    await older;
    expect(store.bindings()).toEqual([BINDING]);

    data.bindings = bindingPage([BINDING, second]);
    data.settleProject(1, PROJECT);
    await newer;

    expect(store.bindings()).toEqual([BINDING, second]);
    expect(store.status()).toBe('ready');
  });

  it('clears the refresh notice on every user step move (F4)', async () => {
    // Steps 1–2 are done and step 3 is the first open one, so all three moves are allowed.
    data.projects = [PROJECT];
    data.bindings = bindingPage([BINDING]);
    await store.load('o-1', 'p-1');
    expect(store.viewStep()).toBe(3);

    const failedRefresh = async (): Promise<void> => {
      data.bindingFailure = apiError('unavailable', 503);
      await store.refresh('o-1', 'p-1');
      data.bindingFailure = null;
      expect(store.refreshFailure()).toBe('unavailable');
    };

    // selectStep: the notice belongs to the step that was on screen, so a step re-entered later
    // opens without it — the remounted step 2 must not show an earlier failure's banner.
    await failedRefresh();
    store.selectStep(2);
    expect(store.viewStep()).toBe(2);
    expect(store.refreshFailure()).toBeNull();

    await failedRefresh();
    store.next();
    expect(store.viewStep()).toBe(3);
    expect(store.refreshFailure()).toBeNull();

    await failedRefresh();
    store.back();
    expect(store.viewStep()).toBe(2);
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
});
