import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { PolicyActivationStateDto } from '@evidgate/sdk';
import { SessionStore } from '../../core/session/session.store';
import {
  OnboardingProgressService,
  type OnboardingProgress,
} from './data/onboarding-progress.service';
import { OnboardingStore } from './onboarding.store';

const PROGRESS: OnboardingProgress = {
  project: {
    id: 'project-1',
    org_id: 'org-1',
    name: 'Storefront',
    slug: 'storefront',
    status: 'active',
    default_environment_id: null,
    created_at: '2026-11-04T00:00:00Z',
    updated_at: '2026-11-04T00:00:00Z',
    version: 1,
    retention_days: null,
    monthly_run_quota: 5000,
    case_prefix: 'STOR',
  },
  done: { 1: true, 2: true, 3: true, 4: true, 5: false },
  activation: { kind: 'none' },
  sampleRun: null,
  bindings: [],
  tokens: [],
  bindingsTruncated: false,
  tokensTruncated: false,
  currentStep: 5,
};
const ACTIVATION: PolicyActivationStateDto = {
  project_id: 'project-1',
  policy_revision_id: 'policy-confirmed',
  hash: 'sha256:confirmed',
  mode: 'advisory',
  activated_by: 'member-1',
  activated_at: '2026-11-04T12:00:00Z',
};

describe('OnboardingStore confirmed activations', () => {
  const load = vi.fn<() => Promise<OnboardingProgress>>();
  beforeEach(() => {
    load.mockReset().mockResolvedValue(PROGRESS);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        OnboardingStore,
        { provide: OnboardingProgressService, useValue: { load } },
        { provide: SessionStore, useValue: { adoptCreatedProject: vi.fn() } },
      ],
    });
  });

  it('keeps the confirmed activation when navigating Back without another server read', async () => {
    const store = TestBed.inject(OnboardingStore);
    await store.load('org-1', 'project-1');
    store.recordActivation('org-1', ACTIVATION);
    store.back();
    expect(store.viewStep()).toBe(4);
    expect(store.done()[5]).toBe(true);
    expect(store.progress()?.activation).toEqual({ kind: 'active', state: ACTIVATION });
    expect(load).toHaveBeenCalledTimes(1);
  });

  it.each([
    ['org-2', ACTIVATION],
    ['org-1', { ...ACTIVATION, project_id: 'project-2' }],
  ] satisfies [string, PolicyActivationStateDto][])(
    'ignores confirmation outside the current organization/project scope (%s)',
    async (orgId, activation) => {
      const store = TestBed.inject(OnboardingStore);
      await store.load('org-1', 'project-1');
      store.recordActivation(orgId, activation);
      expect(store.progress()?.activation).toEqual({ kind: 'none' });
      expect(store.done()[5]).toBe(false);
      store.recordActivation('org-1', ACTIVATION);
      expect(store.done()[5]).toBe(true);
    },
  );

  it('does not let a refresh started before confirmation erase the newer answer', async () => {
    const store = TestBed.inject(OnboardingStore);
    await store.load('org-1', 'project-1');
    let resolveRefresh: (value: OnboardingProgress) => void = () => {
      throw new Error('Refresh has not started');
    };
    load.mockReturnValueOnce(
      new Promise((resolve) => {
        resolveRefresh = resolve;
      }),
    );
    const refresh = store.refresh('org-1', 'project-1');
    store.recordActivation('org-1', ACTIVATION);
    resolveRefresh(PROGRESS);
    await refresh;
    expect(store.progress()?.activation).toEqual({ kind: 'active', state: ACTIVATION });
  });

  it('drops the old activation when a different organization is loaded', async () => {
    const store = TestBed.inject(OnboardingStore);
    await store.load('org-1', 'project-1');
    store.recordActivation('org-1', ACTIVATION);
    load.mockResolvedValueOnce({
      ...PROGRESS,
      project: null,
      done: { 1: false, 2: false, 3: false, 4: false, 5: false },
      currentStep: 1,
    });
    await store.load('org-2', null);
    expect(store.progress()?.activation).toEqual({ kind: 'none' });
    store.recordActivation('org-1', ACTIVATION);
    expect(store.progress()?.activation).toEqual({ kind: 'none' });
  });
});
