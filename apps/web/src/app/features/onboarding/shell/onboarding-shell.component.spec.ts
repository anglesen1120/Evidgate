import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  convertToParamMap,
  Router,
  RouterLink,
  type ParamMap,
} from '@angular/router';
import type { ProjectDto } from '@evidgate/sdk';
import { By } from '@angular/platform-browser';
import { BehaviorSubject } from 'rxjs';

import { SessionStore } from '../../../core/session/session.store';
import {
  OnboardingProgressService,
  type OnboardingProgress,
} from '../data/onboarding-progress.service';
import { OnboardingStore } from '../onboarding.store';
import { CreateProjectPage } from '../steps/create-project.page';
import { ConnectCiPage } from '../steps/connect-ci.page';
import { EnableCollectionPage } from '../steps/enable-collection.page';
import { OnboardingShellComponent } from './onboarding-shell.component';
import {
  ConnectCiStub,
  CreateProjectStub,
  EnableCollectionStub,
  finishedRouter,
  ShellRouterLinkStub,
  mountShell,
  shellStore,
  type MountOptions,
  type ShellStore,
} from './onboarding-shell.spec-support';
import { StepPendingComponent } from './step-pending.component';

describe('OnboardingShellComponent', () => {
  let fixture: ComponentFixture<OnboardingShellComponent>;
  let root: HTMLElement;
  let store: ShellStore = shellStore();
  let orgParams: BehaviorSubject<ParamMap>;
  let childParams: BehaviorSubject<ParamMap>;

  /** Rebuilds the shell for a case that needs a different store or route before it exists. */
  async function setup(options: MountOptions = {}): Promise<void> {
    ({ fixture, root, store, orgParams, childParams } = await mountShell(options));
  }

  beforeEach(async () => {
    await setup();
  });

  function button(id: string): HTMLButtonElement {
    return root.querySelector<HTMLButtonElement>(`[data-testid="onboarding-${id}"]`)!;
  }

  it('disables Next until the viewed step is done and links its visible reason', async () => {
    expect(button('next').getAttribute('data-slot')).toBe('button');
    expect(button('back').getAttribute('data-slot')).toBe('button');
    expect(
      root
        .querySelector('#onboarding-step-title')
        ?.closest('[data-slot="card"]')
        ?.getAttribute('data-slot'),
    ).toBe('card');
    expect(root.querySelector('button[aria-current="step"]')?.getAttribute('type')).toBe('button');
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

  it('returns focus to the stable page heading after every failed reload', async () => {
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
    expect(stepper.querySelector('[aria-current="step"]')?.getAttribute('data-testid')).toBe(
      'onboarding-step-2',
    );
    const first = root.querySelector<HTMLButtonElement>(
      '[data-testid="onboarding-select-step-1"]',
    )!;
    expect(first.disabled).toBe(false);
    expect(first.parentElement?.getAttribute('data-done')).toBe('true');
    first.click();
    await fixture.whenStable();
    expect(store.selectStep).toHaveBeenCalledWith(1);
    expect(store.viewStep()).toBe(1);
    expect(
      root.querySelector<HTMLButtonElement>('[data-testid="onboarding-select-step-3"]')?.disabled,
    ).toBe(true);
    expect(root.querySelector('[data-testid="onboarding-evidence-panel"]')).not.toBeNull();
  });

  it.each([
    ['forbidden', 'app-forbidden-pattern'],
    ['not-found', 'app-not-found-pattern'],
    ['unavailable', 'app-error-pattern'],
  ] as const)(
    'renders the %s replacement without stale completion flags',
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

  it('renders the loading skeleton without a stale stepper', async () => {
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

  it.each([3, 4] as const)(
    'keeps step %s truthful without inventing a report body',
    async (step) => {
      store.viewStep.set(step);
      await fixture.whenStable();
      expect(root.querySelector('[data-testid="onboarding-step-pending"]')).not.toBeNull();
      expect(
        root.querySelector('[data-testid="connect-ci-page"]')?.closest('[hidden]'),
      ).not.toBeNull();
    },
  );

  it('renders the enable step at step 5 with the wizard scope', async () => {
    store.done.set({ 1: true, 2: true, 3: true, 4: true, 5: false });
    store.viewStep.set(5);
    await fixture.whenStable();
    // The receiving page replaced the placeholder for this step only.
    expect(root.querySelector('[data-testid="onboarding-step-pending"]')).toBeNull();
    expect(root.querySelector('[data-testid="enable-collection-page"]')?.textContent).toContain(
      'org-1 project-1',
    );
    // Next stays disabled on this open step: `done[5]` comes from the project's activation read,
    // and this stub state reports no activation.
    expect(button('next').disabled).toBe(true);
  });

  it('renders the scope a create just published, and reads any other', async () => {
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

  it('reads again on a revisit that inherits a used focus token, and moves no focus', async () => {
    // A store left ready by an earlier activation carries the token of a step moved back then.
    await setup({ focusToken: 2 });

    expect(store.consumeCreated).toHaveBeenCalledWith('org-1', null);
    expect(store.load).toHaveBeenCalledWith('org-1', null);
    expect(root.querySelector('#onboarding-step-title')).not.toBeNull();
    // The first ready render of the revisit must not steal focus to the step heading.
    expect(document.activeElement?.id).not.toBe('onboarding-step-title');
  });

  it('replaces terminal Next with a project overview link and keeps Back available', async () => {
    store.currentStep.set('enabled');
    store.viewStep.set(5);
    store.done.set({ 1: true, 2: true, 3: true, 4: true, 5: true });
    await fixture.whenStable();
    const complete = root.querySelector<HTMLAnchorElement>('[data-testid="onboarding-complete"]');
    expect(complete?.textContent?.trim()).toBe('Open project');
    expect(complete?.getAttribute('href')).toBe('/orgs/org-1/projects/project-1/overview');
    expect(root.querySelector('[data-testid="onboarding-next"]')).toBeNull();
    expect(root.querySelector('#onboarding-next-reason')).toBeNull();
    expect(button('back').disabled).toBe(false);
    button('back').click();
    await fixture.whenStable();
    expect(store.viewStep()).toBe(4);
    expect(root.querySelector('[data-testid="onboarding-complete"]')).toBeNull();
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b/);
  });

  it('does not offer completion without a confirmed project', async () => {
    store.currentStep.set('enabled');
    store.viewStep.set(5);
    store.project.set(null);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="onboarding-complete"]')).toBeNull();
    expect(button('next').disabled).toBe(true);
  });
});

describe('OnboardingShellComponent over the real store', () => {
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
      .overrideDirective(ShellRouterLinkStub, { set: { selector: '[routerLink]' } })
      .overrideComponent(OnboardingShellComponent, {
        remove: { imports: [CreateProjectPage, ConnectCiPage, EnableCollectionPage, RouterLink] },
        add: {
          imports: [CreateProjectStub, ConnectCiStub, EnableCollectionStub, ShellRouterLinkStub],
        },
      })
      .overrideComponent(StepPendingComponent, {
        remove: { imports: [RouterLink] },
        add: { imports: [ShellRouterLinkStub] },
      })
      .compileComponents();
    const activate = async (): Promise<ComponentFixture<OnboardingShellComponent>> => {
      const fixture = TestBed.createComponent(OnboardingShellComponent);
      await fixture.whenStable();
      return fixture;
    };
    return { progressLoad, childParams, activate, store: TestBed.inject(OnboardingStore) };
  }

  it('retains a confirmed activation in the resource ledger after Back destroys the step', async () => {
    const { progressLoad, activate } = await setupRealShell();
    progressLoad.mockResolvedValue({
      ...CREATED,
      done: { 1: true, 2: true, 3: true, 4: true, 5: false },
      currentStep: 5,
    });
    const fixture = await activate();
    const step = fixture.debugElement.query(By.directive(EnableCollectionStub))
      .componentInstance as EnableCollectionStub;
    step.activationConfirmed.emit({
      project_id: PROJECT.id,
      policy_revision_id: 'newly-confirmed-policy',
      hash: 'sha256:confirmed',
      mode: 'advisory',
      activated_by: 'member-1',
      activated_at: '2026-11-04T12:00:00Z',
    });
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const panel = root.querySelector('[data-testid="onboarding-evidence-panel"]');
    expect(panel?.textContent).toContain('newly-confirmed-policy');
    expect(panel?.querySelector('[data-resource-state="empty"]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-testid="onboarding-back"]')?.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="enable-collection-page"]')).toBeNull();
    expect(panel?.textContent).toContain('newly-confirmed-policy');
  });

  it('reads once for the page and once for the create, and reads again on the next visit', async () => {
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
    // The create's step change still moves focus on the activation it caused.
    expect(document.activeElement?.id).toBe('onboarding-step-title');

    // The marker was consumed by that activation, so a later visit to the same URL re-derives.
    fixture.destroy();
    await activate();
    expect(progressLoad).toHaveBeenCalledTimes(3);
  });

  it('reads again when the shell is re-created over a ready scope with no create in between', async () => {
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
