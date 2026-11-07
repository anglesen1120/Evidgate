// The terminal screen: an enabled derivation opens step 5, which is the last step and the only one
// without a next move.

import { TestBed } from '@angular/core/testing';

import { SessionStore } from '../../core/session/session.store';
import {
  OnboardingProgressService,
  type OnboardingProgress,
} from './data/onboarding-progress.service';
import { OnboardingStore } from './onboarding.store';
import { BINDING, FakeSessionStore, PROJECT } from './onboarding.store.test-support';

describe('OnboardingStore on the terminal screen (09.2.1-T1.1)', () => {
  const ENABLED: OnboardingProgress = {
    project: PROJECT,
    done: { 1: true, 2: true, 3: true, 4: true, 5: true },
    activation: {
      kind: 'active',
      state: {
        project_id: PROJECT.id,
        policy_revision_id: 'policy-revision-1',
        hash: 'a'.repeat(64),
        mode: 'advisory',
        activated_by: 'user-1',
        activated_at: '2026-10-20T09:00:00Z',
      },
    },
    sampleRun: null,
    bindings: [BINDING],
    tokens: [],
    bindingsTruncated: false,
    tokensTruncated: false,
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
