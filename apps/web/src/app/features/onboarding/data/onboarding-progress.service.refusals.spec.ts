import { apiError } from '../../../shared/testing/http-data.testing';
import { OnboardingProgressService } from './onboarding-progress.service';
import {
  FakeOnboardingDataService,
  PROJECT,
  rejectionOf,
  runPage,
  runSummary,
  setupProgressService,
} from './onboarding-progress.service.test-support';

describe('OnboardingProgressService (09.2.1-T1.2)', () => {
  let data: FakeOnboardingDataService;
  let service: OnboardingProgressService;

  beforeEach(() => {
    ({ data, service } = setupProgressService());
  });

  it('propagates a 403 unchanged, from the project list and from a project read', async () => {
    const listFailure = apiError('forbidden', 403);
    data.projectFailure = listFailure;
    expect(await rejectionOf(service.load('o-1', null))).toBe(listFailure);

    const readFailure = apiError('forbidden', 403);
    data.projectReadFailure = readFailure;
    expect(await rejectionOf(service.load('o-1', 'p-1'))).toBe(readFailure);
  });

  it('propagates a 404 from the project read unchanged', async () => {
    const failure = apiError('not_found', 404);
    data.projectReadFailure = failure;

    const cause = await rejectionOf(service.load('o-1', 'p-1'));

    expect(cause).toBe(failure);
  });

  it('propagates a run refusal unchanged', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'complete')]);
    const failure = apiError('unavailable', 503);
    data.runStateFailure = failure;

    const cause = await rejectionOf(service.load('o-1', 'p-1'));

    expect(cause).toBe(failure);
  });
});
