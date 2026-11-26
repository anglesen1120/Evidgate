import { OnboardingProgressService, type OnboardingProgress } from './onboarding-progress.service';
import {
  ACTIVATION,
  FakeOnboardingDataService,
  PROJECT,
  runPage,
  settle,
  setupProgressService,
} from './onboarding-progress.service.test-support';

describe('OnboardingProgressService (09.2.1-T1.2)', () => {
  let data: FakeOnboardingDataService;
  let service: OnboardingProgressService;

  beforeEach(() => {
    ({ data, service } = setupProgressService());
  });

  it('resumes at first run without reading credential inventory', async () => {
    data.projectsById.set('p-1', PROJECT);

    const progress: OnboardingProgress = await service.load('o-1', 'p-1');

    expect(progress.project).toEqual(PROJECT);
    expect(progress.done).toEqual({ 1: true, 2: false, 3: false, 4: false, 5: false });
    expect(progress.currentStep).toBe(3);
    expect(progress.sampleRun).toBeNull();
    expect(data.projectReadIds).toEqual(['p-1']);
    expect(data.projectCalls).toEqual([]);
    expect(data.runQueries).toEqual([{ limit: 1 }]);
  });

  it('reads runs and activation together, before either answers', async () => {
    data.projectsById.set('p-1', PROJECT);
    data.deferProjectReads = true;

    let settled = false;
    const loading = service.load('o-1', 'p-1').then((progress) => {
      settled = true;
      return progress;
    });
    await settle();

    expect(data.runCalls).toBe(1);
    expect(data.activationCalls).toBe(1);
    expect(settled).toBe(false);

    data.settleActivation(ACTIVATION);
    data.settleRuns(runPage([]));
    const progress = await loading;

    expect(progress.activation).toEqual({ kind: 'active', state: ACTIVATION });
    expect(progress.currentStep).toBe(3);
  });
});
