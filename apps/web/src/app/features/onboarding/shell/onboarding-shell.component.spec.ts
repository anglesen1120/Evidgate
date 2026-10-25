import { Component, computed, input, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, type ParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';

import type { StepNumber } from '../data/step-derivation';
import { OnboardingStore } from '../onboarding.store';
import { CreateProjectPage } from '../steps/create-project.page';
import { OnboardingShellComponent } from './onboarding-shell.component';

@Component({ selector: 'app-onboarding-create-project', template: '' })
class CreateProjectStub {
  readonly orgId = input.required<string>();
}

function shellStore() {
  const viewStep = signal<StepNumber>(1);
  const currentStep = signal<StepNumber | 'enabled'>(2);
  const done = signal<Readonly<Record<StepNumber, boolean>>>({
    1: false,
    2: false,
    3: false,
    4: false,
    5: false,
  });
  return {
    status: signal<'loading' | 'ready' | 'error'>('ready'),
    failure: signal<'forbidden' | 'not-found' | 'unavailable' | null>(null),
    viewStep,
    currentStep,
    done,
    project: signal(null),
    canGoNext: computed(() => done()[viewStep()] && viewStep() < 5),
    canGoBack: computed(() => viewStep() > 1),
    load: vi.fn<(orgId: string, projectId: string | null) => Promise<void>>(() =>
      Promise.resolve(),
    ),
    selectStep: vi.fn((step: StepNumber) => {
      const current = currentStep();
      if (current === 'enabled' || step <= current) viewStep.set(step);
    }),
    next: vi.fn(() => {
      if (done()[viewStep()] && viewStep() < 5) viewStep.update((n) => (n + 1) as StepNumber);
    }),
    back: vi.fn(() => {
      if (viewStep() > 1) viewStep.update((n) => (n - 1) as StepNumber);
    }),
  };
}

describe('OnboardingShellComponent', () => {
  let fixture: ComponentFixture<OnboardingShellComponent>;
  let root: HTMLElement;
  let store = shellStore();
  let orgParams: BehaviorSubject<ParamMap>;
  let childParams: BehaviorSubject<ParamMap>;

  beforeEach(async () => {
    store = shellStore();
    orgParams = new BehaviorSubject(convertToParamMap({ orgId: 'org-1' }));
    childParams = new BehaviorSubject(convertToParamMap({}));
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

  it('disables Back on step 1, enables it later and focuses the new step heading', async () => {
    expect(button('back').disabled).toBe(true);
    button('back').click();
    expect(store.back).not.toHaveBeenCalled();
    store.viewStep.set(2);
    await fixture.whenStable();
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

  it('keeps Next disabled on the final step even when all resources are done', async () => {
    store.currentStep.set('enabled');
    store.viewStep.set(5);
    store.done.set({ 1: true, 2: true, 3: true, 4: true, 5: true });
    await fixture.whenStable();
    expect(button('next').disabled).toBe(true);
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b/);
  });
});
