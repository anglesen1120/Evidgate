import { Component, computed, input, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, Router, type ParamMap } from '@angular/router';
import type { ProjectDto } from '@evidgate/sdk';
import { BehaviorSubject, Subject } from 'rxjs';

import { SessionStore } from '../../../core/session/session.store';
import {
  OnboardingProgressService,
  type OnboardingProgress,
} from '../data/onboarding-progress.service';
import type { StepNumber } from '../data/step-derivation';
import { OnboardingStore } from '../onboarding.store';
import { CreateProjectPage } from '../steps/create-project.page';
import { ConnectCiPage } from '../steps/connect-ci.page';
import { EnableCollectionPage } from '../steps/enable-collection.page';
import { OnboardingShellComponent } from './onboarding-shell.component';

@Component({ selector: 'app-onboarding-create-project', template: '' })
class CreateProjectStub {
  readonly orgId = input.required<string>();
}

@Component({
  selector: 'app-onboarding-connect-ci',
  template: '<div data-testid="connect-ci-page">{{ orgId() }} {{ projectId() }}</div>',
})
class ConnectCiStub {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
}

@Component({
  selector: 'app-onboarding-enable-collection',
  template: '<div data-testid="enable-collection-page">{{ orgId() }} {{ projectId() }}</div>',
})
class EnableCollectionStub {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
}

/** The scope a stub store reports as the one-shot create marker (S71 F1/N1). */
interface StubScope {
  orgId: string;
  projectId: string | null;
}

/**
 * The router the shell reads for its navigation handoff. These cases arrive with the navigation
 * already finished, so no event is in flight and none waits for the created step's handoff.
 */
function finishedRouter(): { events: Subject<unknown>; currentNavigation: () => unknown } {
  return { events: new Subject<unknown>(), currentNavigation: () => null };
}

function shellStore(createdScope: () => StubScope | null = () => null) {
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
    project: signal<{ id: string; name: string } | null>({
      id: 'project-1',
      name: 'Storefront web',
    }),
    focusToken,
    canGoNext: computed(() => done()[viewStep()] && viewStep() < 5),
    canGoBack: computed(() => viewStep() > 1),
    load: vi.fn<(orgId: string, projectId: string | null) => Promise<void>>(() =>
      Promise.resolve(),
    ),
    consumeCreated: vi.fn((orgId: string, projectId: string | null) => {
      const created = createdScope();
      return created !== null && created.orgId === orgId && created.projectId === projectId;
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
    /** The scope the stub store reports as the one-shot create marker; nothing by default. */
    created?: () => StubScope | null;
    /** The project address the route carries; the org root by default. */
    projectId?: string;
    /**
     * The focus token the store already carries when the shell is built — a revisit inheriting a
     * step moved in an earlier activation.
     */
    focusToken?: number;
  }

  /**
   * Build the shell over a fresh store. A case that changes what the store holds or what the
   * route carries must go through here: the shell consults the create marker once, when its route
   * subscription first answers, so the state has to be in place before the component exists.
   */
  async function setup(options: SetupOptions = {}): Promise<void> {
    TestBed.resetTestingModule();
    store = shellStore(options.created);
    store.focusToken.set(options.focusToken ?? 0);
    orgParams = new BehaviorSubject(convertToParamMap({ orgId: 'org-1' }));
    childParams = new BehaviorSubject(
      convertToParamMap(options.projectId === undefined ? {} : { projectId: options.projectId }),
    );
    await TestBed.configureTestingModule({
      imports: [OnboardingShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: OnboardingStore, useValue: store },
        { provide: Router, useValue: finishedRouter() },
        {
          provide: ActivatedRoute,
          useValue: { pathFromRoot: [{ paramMap: orgParams }, { paramMap: childParams }] },
        },
      ],
    })
      .overrideComponent(OnboardingShellComponent, {
        remove: { imports: [CreateProjectPage, ConnectCiPage, EnableCollectionPage] },
        add: { imports: [CreateProjectStub, ConnectCiStub, EnableCollectionStub] },
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
    expect(root.querySelector('[data-testid="connect-ci-page"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="onboarding-step-pending"]')).toBeNull();
  });

  it('leaves focus alone on the first ready render of a page load', () => {
    expect(root.querySelector('#onboarding-step-title')).not.toBeNull();
    expect(document.activeElement?.id).not.toBe('onboarding-step-title');
    expect(document.activeElement?.id).not.toBe('onboarding-title');
  });

  it('returns focus to the stable page heading after every failed reload (AC-F63-b)', async () => {
    // A page whose own read already failed: this is the error branch the retry button lives in.
    store.status.set('loading');
    await fixture.whenStable();
    store.status.set('error');
    store.failure.set('unavailable');
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="onboarding-retry"]')).not.toBeNull();
    // Rendering a failure is not a reload: nothing moved focus to the heading on its own.
    expect(document.activeElement?.id).not.toBe('onboarding-title');

    /**
     * A read the test answers, so the loading render really happens: that pass destroys the retry
     * button holding focus, which is the loss this spec is about. A mock that answered in one turn
     * lets Angular reuse the button node and hides the loss entirely.
     */
    // `Promise.withResolvers` is ES2024; this project compiles against the ES2022 lib
    // (http-client-fetch.ts:203), so the executor form is how the answer handle escapes.
    let answerRetry: (failure: 'forbidden' | 'not-found' | 'unavailable') => void = () => {};
    store.load.mockImplementation(() => {
      let resolveRead: () => void = () => undefined;
      const read = new Promise<void>((resolve) => {
        resolveRead = resolve;
      });
      store.status.set('loading');
      answerRetry = (failure) => {
        store.status.set('error');
        store.failure.set(failure);
        resolveRead();
      };
      return read;
    });
    const activationLoads = store.load.mock.calls.length;

    // Two consecutive failed reloads, and the second answers with a different failure: the heading
    // must be the target in every failure branch the shell renders, not only the generic one.
    for (const failure of ['unavailable', 'forbidden'] as const) {
      const retry = root.querySelector<HTMLButtonElement>('[data-testid="onboarding-retry"]')!;
      retry.focus();
      expect(document.activeElement).toBe(retry);
      retry.click();
      await fixture.whenStable();
      // The loading branch replaced the control that carried focus: focus is really gone here,
      // before the read answers, so the assertion below cannot pass on a node Angular reused.
      expect(root.querySelector('[data-testid="onboarding-retry"]')).toBeNull();
      expect(document.activeElement).not.toBe(retry);

      answerRetry(failure);
      await fixture.whenStable();
      expect(document.activeElement).toBe(root.querySelector('#onboarding-title'));
    }
    expect(store.load).toHaveBeenCalledTimes(activationLoads + 2);
    // The second failure rendered its own branch, so the heading is not the generic branch's.
    expect(root.querySelector('app-forbidden-pattern')).not.toBeNull();
    expect(root.querySelector('[data-testid="onboarding-retry"]')).toBeNull();
    // The retry landed on the failure branches and never opened a step of its own.
    expect(store.viewStep()).toBe(1);
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

  it('updates the connect child inputs coherently when the route scope changes', async () => {
    store.viewStep.set(2);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connect-ci-page"]')?.textContent).toContain(
      'org-1 project-1',
    );
    orgParams.next(convertToParamMap({ orgId: 'org-2' }));
    store.project.set({ id: 'project-2', name: 'Other project' });
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connect-ci-page"]')?.textContent).toContain(
      'org-2 project-2',
    );
  });

  it.each([3, 4] as const)('keeps step %s as the placeholder', async (step) => {
    store.viewStep.set(step);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="onboarding-step-pending"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="connect-ci-page"]')).toBeNull();
  });

  it('renders the enable step at step 5 with the wizard scope (09.2.1-T5)', async () => {
    store.done.set({ 1: true, 2: true, 3: true, 4: true, 5: false });
    store.viewStep.set(5);
    await fixture.whenStable();
    // The receiving page replaced the placeholder for this step only.
    expect(root.querySelector('[data-testid="onboarding-step-pending"]')).toBeNull();
    expect(root.querySelector('[data-testid="enable-collection-page"]')?.textContent).toContain(
      'org-1 project-1',
    );
    // Next stays disabled on this open step: `done[5]` comes from the project's activation read
    // (09.2.1-T5.2), and this stub state reports no activation.
    expect(button('next').disabled).toBe(true);
  });

  it('renders the scope a create just published, and reads any other (S71 F1)', async () => {
    await setup({
      created: () => ({ orgId: 'org-1', projectId: 'project-1' }),
      projectId: 'project-1',
      // The create bumped the token before this activation existed. Taking the marker must NOT
      // adopt it as the baseline, or the step the create opened would never receive focus.
      focusToken: 1,
    });
    // The create's own `projectCreated` load published this scope; the URL change it caused
    // re-activates the shell, which must render that state instead of issuing the fan-out again.
    expect(store.consumeCreated).toHaveBeenCalledWith('org-1', 'project-1');
    expect(store.load).not.toHaveBeenCalled();
    expect(root.querySelector('[data-testid="onboarding-stepper"]')).not.toBeNull();
    expect(document.activeElement?.id).toBe('onboarding-step-title');

    childParams.next(convertToParamMap({ projectId: 'project-2' }));
    await fixture.whenStable();
    expect(store.load).toHaveBeenCalledWith('org-1', 'project-2');
    expect(store.load).toHaveBeenCalledTimes(1);
  });

  it('reads again on a revisit that inherits a used focus token, and moves no focus (S71 N1)', async () => {
    // A store left ready by an earlier activation carries the token of a step moved back then.
    await setup({ focusToken: 2 });

    expect(store.consumeCreated).toHaveBeenCalledWith('org-1', null);
    expect(store.load).toHaveBeenCalledWith('org-1', null);
    expect(root.querySelector('#onboarding-step-title')).not.toBeNull();
    // The first ready render of the revisit must not steal focus to the step heading.
    expect(document.activeElement?.id).not.toBe('onboarding-step-title');
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

describe('OnboardingShellComponent over the real store (S71 F1/N1)', () => {
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
    activation: { kind: 'none' },
    sampleRun: null,
    bindings: [],
    tokens: [],
    bindingsTruncated: false,
    tokensTruncated: false,
    currentStep: 1,
  };
  const CREATED: OnboardingProgress = {
    ...NO_PROJECT,
    project: PROJECT,
    done: { 1: true, 2: false, 3: false, 4: false, 5: false },
    currentStep: 2,
  };

  /** A shell over the real store, with the progress read spied and the route params driven here. */
  async function setupRealShell() {
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
        { provide: Router, useValue: finishedRouter() },
        {
          provide: ActivatedRoute,
          useValue: { pathFromRoot: [{ paramMap: orgParams }, { paramMap: childParams }] },
        },
      ],
    })
      .overrideComponent(OnboardingShellComponent, {
        remove: { imports: [CreateProjectPage, ConnectCiPage, EnableCollectionPage] },
        add: { imports: [CreateProjectStub, ConnectCiStub, EnableCollectionStub] },
      })
      .compileComponents();
    const activate = async (): Promise<ComponentFixture<OnboardingShellComponent>> => {
      const fixture = TestBed.createComponent(OnboardingShellComponent);
      await fixture.whenStable();
      return fixture;
    };
    return { progressLoad, childParams, activate, store: TestBed.inject(OnboardingStore) };
  }

  it('reads once for the page and once for the create, and reads again on the next visit (S71 F1/N1)', async () => {
    const { progressLoad, childParams, activate, store } = await setupRealShell();
    const fixture = await activate();
    expect(progressLoad).toHaveBeenCalledTimes(1);
    expect(progressLoad).toHaveBeenLastCalledWith('org-1', null);

    // Step 1 creates the project; the store adopts it and loads the derivation for it.
    await store.projectCreated(PROJECT);
    expect(progressLoad).toHaveBeenCalledTimes(2);

    // The create navigates to `/orgs/org-1/onboarding/project-1`, which activates the shell on
    // the `:projectId` route: it takes the marker and renders that scope, no third read.
    childParams.next(convertToParamMap({ projectId: PROJECT.id }));
    await fixture.whenStable();
    expect(progressLoad).toHaveBeenCalledTimes(2);
    const rendered = fixture.nativeElement as HTMLElement;
    expect(rendered.querySelector('[data-testid="onboarding-step-2"]')).not.toBeNull();
    // The create's step change still moves focus on the activation it caused (S71 F2).
    expect(document.activeElement?.id).toBe('onboarding-step-title');

    // The marker was consumed by that activation, so a later visit to the same URL re-derives.
    fixture.destroy();
    await activate();
    expect(progressLoad).toHaveBeenCalledTimes(3);
  });

  it('reads again when the shell is re-created over a ready scope with no create in between (S71 N1)', async () => {
    const { progressLoad, activate } = await setupRealShell();
    const first = await activate();
    expect(progressLoad).toHaveBeenCalledTimes(1);

    // Leaving onboarding and coming back: the route-level store is still ready for the very same
    // scope, but no create published a marker, so the new activation must re-derive rather than
    // render a derivation nobody refreshed.
    first.destroy();
    await activate();
    expect(progressLoad).toHaveBeenCalledTimes(2);
  });
});
