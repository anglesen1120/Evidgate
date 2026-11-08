import { isApiRequestError } from '../../../core/http/api-error';
import { apiError } from '../../../shared/testing/http-data.testing';
import { OnboardingProgressService } from './onboarding-progress.service';
import {
  ACTIVATION,
  BINDING,
  FakeOnboardingDataService,
  PROJECT,
  bindingPage,
  rejectionOf,
  runPage,
  runState,
  runSummary,
  setupProgressService,
} from './onboarding-progress.service.test-support';

describe('OnboardingProgressService (09.2.1-T1.2)', () => {
  let data: FakeOnboardingDataService;
  let service: OnboardingProgressService;

  beforeEach(() => {
    ({ data, service } = setupProgressService());
  });

  it('completes step 5 and reaches the terminal screen when the activation read answers', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'complete')]);
    data.runStates.set('r-1', runState('r-1', 'complete', 'rev-7'));
    data.activation = ACTIVATION;

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done).toEqual({ 1: true, 2: true, 3: true, 4: true, 5: true });
    expect(progress.currentStep).toBe('enabled');
    // The server's document travels unchanged, so step 5's panel renders what it was given.
    expect(progress.activation).toEqual({ kind: 'active', state: ACTIVATION });
    // The read is project-keyed and made once, with the id the project row carries.
    expect(data.activationIds).toEqual(['p-1']);
  });

  it('leaves step 5 open when the activation read answers 404 (no activation)', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'complete')]);
    data.runStates.set('r-1', runState('r-1', 'complete', 'rev-7'));
    // The data service turns the contract's single 404 into `null`; a project that was never
    // activated is indistinguishable from an unknown or foreign one (00 §9.2).
    data.activation = null;

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[3]).toBe(true);
    expect(progress.done[4]).toBe(true);
    expect(progress.done[5]).toBe(false);
    expect(progress.currentStep).toBe(5);
    expect(progress.activation).toEqual({ kind: 'none' });
  });

  it('a progress load with the activation read refused still resolves the wizard and leaves step 5 un-derived', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'complete')]);
    data.runStates.set('r-1', runState('r-1', 'complete', 'rev-7'));
    data.activationFailure = apiError('forbidden', 403);

    const progress = await service.load('o-1', 'p-1');

    // Steps 1–4 are derived exactly as without the read: a refused activation is step 5's own
    // state, never a failed wizard and never an "nothing is active" the caller cannot know.
    expect(progress.done).toEqual({ 1: true, 2: true, 3: true, 4: true, 5: false });
    expect(progress.currentStep).toBe(5);
    expect(progress.activation).toEqual({ kind: 'forbidden' });
    expect(progress.bindings).toEqual([BINDING]);
    expect(progress.sampleRun?.run_id).toBe('r-1');
  });

  it('rejects the whole load for an activation refusal that is neither 403 nor the contract 404', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);

    for (const [kind, status] of [
      ['unavailable', 503],
      ['unauthenticated', 401],
    ] as const) {
      const failure = apiError(kind, status);
      data.activationFailure = failure;

      const cause = await rejectionOf(service.load('o-1', 'p-1'));

      // Neither refusal says whether an activation exists, so no step is published at all: the
      // gateway's error arrives unchanged and the store renders its error pattern.
      expect(cause).toBe(failure);
      expect(isApiRequestError(cause)).toBe(true);
      if (isApiRequestError(cause)) {
        expect(cause.error.kind).toBe(kind);
      }
    }
  });
});
