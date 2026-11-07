import { isApiRequestError } from '../../../core/http/api-error';
import { OnboardingProgressService } from './onboarding-progress.service';
import {
  FakeOnboardingDataService,
  OTHER_ORG_PROJECT,
  PROJECT,
  rejectionOf,
  setupProgressService,
} from './onboarding-progress.service.test-support';

describe('OnboardingProgressService (09.2.1-T1.2)', () => {
  let data: FakeOnboardingDataService;
  let service: OnboardingProgressService;

  beforeEach(() => {
    ({ data, service } = setupProgressService());
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
    // No project means the activation was never read, and none can be active.
    expect(progress.activation).toEqual({ kind: 'none' });
    expect(data.activationCalls).toBe(0);
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
