import { isApiRequestError } from '../../../core/http/api-error';
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

    data.projectFailure = null;
    data.projectsById.set('p-1', PROJECT);
    const bindingFailure = apiError('forbidden', 403);
    data.bindingFailure = bindingFailure;
    expect(await rejectionOf(service.load('o-1', 'p-1'))).toBe(bindingFailure);
  });

  it('propagates a 404 from the project read unchanged', async () => {
    const failure = apiError('not_found', 404);
    data.projectReadFailure = failure;

    const cause = await rejectionOf(service.load('o-1', 'p-1'));

    expect(cause).toBe(failure);
  });

  it('propagates a 403 or 404 from either step-2 list unchanged', async () => {
    data.projectsById.set('p-1', PROJECT);
    // Both lists are read on every resume, so each of the four refusals has to reach the caller as
    // the gateway's own `ApiRequestError`, by identity: a `not_found` from a list must never be
    // read as the not-found *project* pattern (the service builds its own error for that, with
    // different text), and a rejected load publishes no step and no partial progress — no
    // `OnboardingProgress` exists to carry a guessed one.
    for (const kind of ['forbidden', 'not_found'] as const) {
      const status = kind === 'forbidden' ? 403 : 404;
      for (const list of ['bindings', 'tokens'] as const) {
        const failure = apiError(kind, status);
        data.bindingFailure = list === 'bindings' ? failure : null;
        data.tokenFailure = list === 'tokens' ? failure : null;

        const cause = await rejectionOf(service.load('o-1', 'p-1'));

        expect(cause).toBe(failure);
        expect(isApiRequestError(cause)).toBe(true);
        if (isApiRequestError(cause)) {
          expect(cause.error.kind).toBe(kind);
          expect(cause.error.status).toBe(status);
          // The service's own not-found refusal reads differently: this is the list's answer.
          expect(cause.error.message).toBe(`${kind} (test)`);
        }
      }
    }
    data.bindingFailure = null;
    data.tokenFailure = null;
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
