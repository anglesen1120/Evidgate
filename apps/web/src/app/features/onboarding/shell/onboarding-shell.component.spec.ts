import { Component, computed, input, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, type ParamMap } from '@angular/router';
import type { ProjectDto } from '@evidgate/sdk';
import { BehaviorSubject } from 'rxjs';

import { SessionStore } from '../../../core/session/session.store';
import {
  OnboardingProgressService,
  type OnboardingProgress,
} from '../data/onboarding-progress.service';
import type { StepNumber } from '../data/step-derivation';
import { OnboardingStore } from '../onboarding.store';
import { CreateProjectPage } from '../steps/create-project.page';
import { OnboardingShellComponent } from './onboarding-shell.component';

@Component({ selector: 'app-onboarding-create-project', template: '' })
class CreateProjectStub {
  readonly orgId = input.required<string>();
}

/** The route scope a stub store reports as already published (S71 F1). */
interface StubScope {
  orgId: string;
  projectId: string | null;
}

function shellStore(heldScope: () => StubScope | null = () => null) {
  const viewStep = signal<StepNumber>(1);
  const currentStep = signal<StepNumber | 'enabled'>(2);
  const focusToken = signal(0);
  const done = signal<Readonly<Record<StepNumber, boolean>>>({
    1: false,
    2: false,
    3: false,
    4: false,
    5: false,
  });
  /** The real store bumps the token only when a user move actually changes the step. */
  const showStep = (step: StepNumber): void => {
    if (viewStep() === step) return;
    viewStep.set(step);
    focusToken.update((token) => token + 1);
  };
  return {
    status: signal<'loading' | 'ready' | 'error'>('ready'),
    failure: signal<'forbidden' | 'not-found' | 'unavailable' | null>(null),
    viewStep,
    currentStep,
    done,
    project: signal(null),
    focusToken,
    canGoNext: computed(() => done()[viewStep()] && viewStep() < 5),
    canGoBack: computed(() => viewStep() > 1),
    load: vi.fn<(orgId: string, projectId: string | null) => Promise<void>>(() =>
      Promise.resolve(),
    ),
    holds: vi.fn((orgId: string, projectId: string | null) => {
      const scope = heldScope();
      return scope !== null && scope.orgId === orgId && scope.projectId === projectId;
    }),
    selectStep: vi.fn((step: StepNumber) => {
      const current = currentStep();
      if (current === 'enabled' || step <= current) showStep(step);
    }),
    next: vi.fn(() => {
      if (done()[viewStep()] && viewStep() < 5) showStep((viewStep() + 1) as StepNumber);
    }),
    back: vi.fn(() => {
      if (viewStep() > 1) showStep((viewStep() - 1) as StepNumber);
    }),
  };
}

describe('OnboardingShellComponent', () => {
  let fixture: ComponentFixture<OnboardingShellComponent>;
  let root: HTMLElement;
  let store = shellStore();
  let orgParams: BehaviorSubject<ParamMap>;
  let childParams: BehaviorSubject<ParamMap>;

  interface SetupOptions {
    /** The scope the store reports as already published; nothing is held by default. */
    held?: () => StubScope | null;
    /** The project address the route carries; the org root by default. */
    projectId?: string;
  }

  /**
   * Build the shell over a fresh store. A case that changes what the store holds or what the
   * route carries must go through here: the shell consults `holds` once, when its route
   * subscription first answers, so the state has to be in place before the component exists.
   */
  async function setup(options: SetupOptions = {}): Promise<void> {
    TestBed.resetTestingModule();
    store = shellStore(options.held);
    orgParams = new BehaviorSubject(convertToParamMap({ orgId: 'org-1' }));
    childParams = new BehaviorSubject(
      convertToParamMap(options.projectId === undefined ? {} : { projectId: options.projectId }),
    );
    await TestBed.configureTestingModule({
      imports: [OnboardingShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: OnboardingStore, useValue: store },
        {
          provide: ActivatedRoute,
          useValue: { pathFromRoot: [{ paramMap: orgParams }, { paramMap: childParams }] },
        },
      ],
    })
      .overrideComponent(OnboardingShellComponent, {
        remove: { imports: [CreateProjectPage] },
        add: { imports: [CreateProjectStub] },
      })
      .compileComponents();
    fixture = TestBed.createComponent(OnboardingShellComponent);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  }

  beforeEach(async () => {
    await setup();
  });

  function button(id: string): HTMLButtonElement {
    return root.querySelector<HTMLButtonElement>(`[data-testid="onboarding-${id}"]`)!;
  }

  it('disables Next until the viewed step is done and links its visible reason', async () => {
    expect(button('next').disabled).toBe(true);
    expect(button('next').getAttribute('aria-describedby')).toBe('onboarding-next-reason');
    expect(root.querySelector('#onboarding-next-reason')?.textContent?.trim()).not.toBe('');
    button('next').click();
    expect(store.next).not.toHaveBeenCalled();
    store.done.update((done) => ({ ...done, 1: true }));
    await fixture.whenStable();
    expect(button('next').disabled).toBe(false);
    expect(button('next').hasAttribute('aria-describedby')).toBe(false);
    button('next').click();
    await fixture.whenStable();
    expect(store.viewStep()).toBe(2);
    expect(root.querySelector('[data-testid="onboarding-step-pending"]')).not.toBeNull();
  });

  it('leaves focus alone on the first ready render of a page load', () => {
    expect(root.querySelector('#onboarding-step-title')).not.toBeNull();
    expect(document.activeElement?.id).not.toBe('onboarding-step-title');
  });

  it('disables Back on step 1, enables it later and focuses the new step heading', async () => {
    expect(button('back').disabled).toBe(true);
    button('back').click();
    expect(store.back).not.toHaveBeenCalled();
    // Only a step the user moved moves focus: Next here, never a store value set behind it.
    store.done.update((done) => ({ ...done, 1: true }));
    await fixture.whenStable();
    expect(document.activeElement?.id).not.toBe('onboarding-step-title');
    button('next').click();
    await fixture.whenStable();
    expect(store.viewStep()).toBe(2);
    expect(button('back').disabled).toBe(false);
    expect(document.activeElement?.id).toBe('onboarding-step-title');
    button('back').click();
    await fixture.whenStable();
    expect(store.viewStep()).toBe(1);
  });

  it('uses an ordered five-step list, permits a completed step and disables future steps', async () => {
    store.done.update((done) => ({ ...done, 1: true }));
    store.viewStep.set(2);
    await fixture.whenStable();
    const stepper = root.querySelector('[data-testid="onboarding-stepper"]')!;
    expect(stepper.tagName).toBe('OL');
    expect(stepper.querySelectorAll(':scope > li')).toHaveLength(5);
    expect(root.querySelector('[aria-current="step"]')?.getAttribute('data-testid')).toBe(
      'onboarding-step-2',
    );
    const first = root.querySelector<HTMLButtonElement>(
      '[data-testid="onboarding-step-1"] button',
    )!;
    expect(first.disabled).toBe(false);
    expect(first.parentElement?.getAttribute('data-done')).toBe('true');
    first.click();
    await fixture.whenStable();
    expect(store.selectStep).toHaveBeenCalledWith(1);
    expect(store.viewStep()).toBe(1);
    expect(
      root.querySelector<HTMLButtonElement>('[data-testid="onboarding-step-3"] button')?.disabled,
    ).toBe(true);
    expect(root.querySelector('[data-testid="onboarding-evidence-panel"]')).not.toBeNull();
  });

  it.each([
    ['forbidden', 'app-forbidden-pattern'],
    ['not-found', 'app-not-found-pattern'],
    ['unavailable', 'app-error-pattern'],
  ] as const)(
    'renders the T8 %s replacement without stale completion flags',
    async (failure, selector) => {
      store.done.update((done) => ({ ...done, 1: true, 2: true }));
      store.failure.set(failure);
      store.status.set('error');
      await fixture.whenStable();
      expect(root.querySelector(selector)).not.toBeNull();
      expect(root.querySelector('[data-testid="onboarding-stepper"]')).toBeNull();
      expect(root.querySelector('[data-done="true"]')).toBeNull();
      expect(root.querySelector('[data-testid="onboarding-evidence-panel"]')).toBeNull();
    },
  );

  it('renders the T8 loading skeleton without a stale stepper', async () => {
    store.status.set('loading');
    await fixture.whenStable();
    expect(root.querySelector('app-loading-pattern [aria-busy="true"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="onboarding-stepper"]')).toBeNull();
  });

  it('loads parent org and optional project params again when either changes', async () => {
    expect(store.load).toHaveBeenCalledWith('org-1', null);
    childParams.next(convertToParamMap({ projectId: 'project-2' }));
    await fixture.whenStable();
    expect(store.load).toHaveBeenLastCalledWith('org-1', 'project-2');
    orgParams.next(convertToParamMap({ orgId: 'org-3' }));
    await fixture.whenStable();
    expect(store.load).toHaveBeenLastCalledWith('org-3', 'project-2');
    const count = store.load.mock.calls.length;
    orgParams.next(convertToParamMap({ orgId: 'org-3' }));
    expect(store.load).toHaveBeenCalledTimes(count);
  });

  it('does not re-read the scope a create already published, and reads a different one (S71 F1)', async () => {
    await setup({
      held: () => ({ orgId: 'org-1', projectId: 'project-1' }),
      projectId: 'project-1',
    });
    // The create's own `projectCreated` load published this scope; the URL change it caused
    // re-activates the shell, which must render that state instead of issuing the fan-out again.
    expect(store.holds).toHaveBeenCalledWith('org-1', 'project-1');
    expect(store.load).not.toHaveBeenCalled();
    expect(root.querySelector('[data-testid="onboarding-stepper"]')).not.toBeNull();

    childParams.next(convertToParamMap({ projectId: 'project-2' }));
    await fixture.whenStable();
    expect(store.load).toHaveBeenCalledWith('org-1', 'project-2');
    expect(store.load).toHaveBeenCalledTimes(1);
  });

  it('keeps Next disabled on the final step even when all resources are done', async () => {
    store.currentStep.set('enabled');
    store.viewStep.set(5);
    store.done.set({ 1: true, 2: true, 3: true, 4: true, 5: true });
    await fixture.whenStable();
    expect(button('next').disabled).toBe(true);
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b/);
  });
});

describe('OnboardingShellComponent over the real store (S71 F1)', () => {
  const PROJECT: ProjectDto = {
    id: 'project-1',
    org_id: 'org-1',
    name: 'Storefront web',
    slug: 'storefront-web',
    status: 'active',
    default_environment_id: null,
    created_at: '2026-10-25T00:00:00Z',
    updated_at: '2026-10-25T00:00:00Z',
    version: 1,
    retention_days: null,
    monthly_run_quota: 5000,
    case_prefix: 'STOR',
  };
  const NO_PROJECT: OnboardingProgress = {
    project: null,
    done: { 1: false, 2: false, 3: false, 4: false, 5: false },
    activationReadable: false,
    sampleRun: null,
    bindingCount: 0,
    tokenCount: 0,
    currentStep: 1,
  };
  const CREATED: OnboardingProgress = {
    ...NO_PROJECT,
    project: PROJECT,
    done: { 1: true, 2: false, 3: false, 4: false, 5: false },
    currentStep: 2,
  };

  /**
   * The acceptance shape of F1 with the real store: the create's own load plus the activation the
   * URL change causes must stay one fan-out, so the progress read runs twice in total — once for
   * the org root, once for the create — and never a third time for the `:projectId` URL.
   */
  it('reads the wizard once for the page and once for the create, never again for the URL', async () => {
    const progressLoad = vi.fn((_orgId: string, projectId: string | null) =>
      Promise.resolve(projectId === null ? NO_PROJECT : CREATED),
    );
    const orgParams = new BehaviorSubject(convertToParamMap({ orgId: 'org-1' }));
    const childParams = new BehaviorSubject(convertToParamMap({}));
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [OnboardingShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        OnboardingStore,
        { provide: OnboardingProgressService, useValue: { load: progressLoad } },
        { provide: SessionStore, useValue: { adoptCreatedProject: vi.fn() } },
        {
          provide: ActivatedRoute,
          useValue: { pathFromRoot: [{ paramMap: orgParams }, { paramMap: childParams }] },
        },
      ],
    })
      .overrideComponent(OnboardingShellComponent, {
        remove: { imports: [CreateProjectPage] },
        add: { imports: [CreateProjectStub] },
      })
      .compileComponents();
    const fixture = TestBed.createComponent(OnboardingShellComponent);
    await fixture.whenStable();
    expect(progressLoad).toHaveBeenCalledTimes(1);
    expect(progressLoad).toHaveBeenLastCalledWith('org-1', null);

    // Step 1 creates the project; the store adopts it and publishes that scope.
    const realStore = TestBed.inject(OnboardingStore);
    await realStore.projectCreated(PROJECT);
    expect(progressLoad).toHaveBeenCalledTimes(2);

    // The create navigates to `/orgs/org-1/onboarding/project-1`, which activates the shell on
    // the `:projectId` route: it renders the published scope instead of reading it again.
    childParams.next(convertToParamMap({ projectId: PROJECT.id }));
    await fixture.whenStable();
    expect(progressLoad).toHaveBeenCalledTimes(2);
    const rendered = fixture.nativeElement as HTMLElement;
    expect(rendered.querySelector('[data-testid="onboarding-step-2"]')).not.toBeNull();
  });
});
