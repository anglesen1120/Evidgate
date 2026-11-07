import { OnboardingProgressService } from './onboarding-progress.service';
import {
  BINDING,
  FakeOnboardingDataService,
  PROJECT,
  bindingPage,
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

  it('takes the oldest run of the page, not the first row it was handed', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([
      runSummary('r-new', '2026-10-22T09:00:00Z', 'complete'),
      runSummary('r-old', '2026-10-21T09:00:00Z', 'complete'),
    ]);
    data.runStates.set('r-old', runState('r-old', 'complete'));

    const progress = await service.load('o-1', 'p-1');

    expect(data.runIds).toEqual(['r-old']);
    expect(progress.sampleRun?.run_id).toBe('r-old');
  });

  it('completes step 3 for an incomplete run (terminal collection state)', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'incomplete')]);
    data.runStates.set('r-1', runState('r-1', 'incomplete'));

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[3]).toBe(true);
    expect(progress.done[4]).toBe(false);
    expect(progress.currentStep).toBe(4);
  });

  it('leaves step 4 open for a complete run without an evidence revision', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'complete')]);
    data.runStates.set('r-1', runState('r-1', 'complete'));

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[3]).toBe(true);
    expect(progress.done[4]).toBe(false);
    expect(progress.currentStep).toBe(4);
  });

  it('does not complete step 3 for a canceled run', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.bindings = bindingPage([BINDING]);
    data.runs = runPage([runSummary('r-1', '2026-10-21T09:00:00Z', 'canceled')]);
    data.runStates.set('r-1', runState('r-1', 'canceled'));

    const progress = await service.load('o-1', 'p-1');

    expect(progress.done[3]).toBe(false);
    expect(progress.currentStep).toBe(3);
  });
});
