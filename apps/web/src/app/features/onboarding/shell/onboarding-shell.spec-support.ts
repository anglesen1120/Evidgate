// The store the onboarding shell reads, in the shape its template and its focus passes bind.
// Shared by the shell's specs, so a case drives one concern — focus, routing, the scope a create
// opened — without a progress read behind it.
import { LocationStrategy } from '@angular/common';
import {
  Component,
  Directive,
  computed,
  input,
  output,
  provideZonelessChangeDetection,
  signal,
  type Signal,
  type WritableSignal,
} from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  convertToParamMap,
  Router,
  RouterLink,
  type ParamMap,
} from '@angular/router';
import { vi, type Mock } from 'vitest';
import type { PolicyActivationStateDto } from '@evidgate/sdk';
import { BehaviorSubject, Subject } from 'rxjs';

import type { StepNumber } from '../data/step-derivation';
import type { OnboardingProgress } from '../data/onboarding-progress.service';
import { OnboardingViewport } from '../onboarding-viewport.testing';
import { OnboardingStore } from '../onboarding.store';
import { ConnectCiPage } from '../steps/connect-ci.page';
import { CreateProjectPage } from '../steps/create-project.page';
import { EnableCollectionPage } from '../steps/enable-collection.page';
import { OnboardingShellComponent } from './onboarding-shell.component';
import { StepPendingComponent } from './step-pending.component';

/** The scope a stub store reports as the one-shot create marker. */
export interface StubScope {
  readonly orgId: string;
  readonly projectId: string | null;
}

export interface ShellStoreOptions {
  /** The scope the store reports as just created; nothing by default. */
  readonly createdScope?: () => StubScope | null;
  readonly viewStep?: StepNumber;
  readonly currentStep?: StepNumber | 'enabled';
  readonly done?: Readonly<Record<StepNumber, boolean>>;
  /** The token the store already carries when the shell is built. */
  readonly focusToken?: number;
}

/** Everything the shell binds. Every member is a real signal or a spy. */
export interface ShellStore {
  readonly status: WritableSignal<'loading' | 'ready' | 'error'>;
  readonly failure: WritableSignal<'forbidden' | 'not-found' | 'unavailable' | null>;
  readonly progress: WritableSignal<OnboardingProgress | null>;
  readonly refreshFailure: WritableSignal<'forbidden' | 'not-found' | 'unavailable' | null>;
  readonly viewStep: WritableSignal<StepNumber>;
  readonly currentStep: WritableSignal<StepNumber | 'enabled'>;
  readonly done: WritableSignal<Readonly<Record<StepNumber, boolean>>>;
  readonly project: WritableSignal<{ id: string; name: string } | null>;
  readonly focusToken: WritableSignal<number>;
  readonly canGoNext: Signal<boolean>;
  readonly canGoBack: Signal<boolean>;
  readonly load: Mock<(orgId: string, projectId: string | null) => Promise<void>>;
  readonly recordActivation: Mock<(orgId: string, activation: PolicyActivationStateDto) => void>;
  readonly consumeCreated: Mock<(orgId: string, projectId: string | null) => boolean>;
  readonly selectStep: Mock<(step: StepNumber) => void>;
  readonly next: Mock<() => void>;
  readonly back: Mock<() => void>;
}

const NOTHING_DONE: Readonly<Record<StepNumber, boolean>> = {
  1: false,
  2: false,
  3: false,
  4: false,
  5: false,
};

/**
 * Builds the stub. Every member is a real signal or a spy, so a spec drives it the way the shell
 * does: a render pass answers a write, and a call is asserted where the shell makes one.
 */
export function shellStore(options: ShellStoreOptions = {}): ShellStore {
  const viewStep = signal<StepNumber>(options.viewStep ?? 1);
  const currentStep = signal<StepNumber | 'enabled'>(options.currentStep ?? 2);
  const focusToken = signal(options.focusToken ?? 0);
  const done = signal<Readonly<Record<StepNumber, boolean>>>(options.done ?? NOTHING_DONE);
  /** The real store bumps the token only when a user move actually changes the step. */
  const showStep = (step: StepNumber): void => {
    if (viewStep() === step) return;
    viewStep.set(step);
    focusToken.update((token) => token + 1);
  };
  return {
    status: signal<'loading' | 'ready' | 'error'>('ready'),
    failure: signal<'forbidden' | 'not-found' | 'unavailable' | null>(null),
    progress: signal<OnboardingProgress | null>(null),
    refreshFailure: signal<'forbidden' | 'not-found' | 'unavailable' | null>(null),
    recordActivation: vi.fn<(orgId: string, activation: PolicyActivationStateDto) => void>(),
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
      const created = options.createdScope?.() ?? null;
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

// ---------------------------------------------------------------------------------------------
// The mounted shell. The stubs, the finished router and the TestBed wiring are shared: a spec that
// only reads one contract (the stepper's marker, the focus pass) still needs the same stub tree.
// ---------------------------------------------------------------------------------------------

@Component({ selector: 'app-onboarding-create-project', template: '' })
export class CreateProjectStub {
  readonly orgId = input.required<string>();
}

@Component({
  selector: 'app-onboarding-connect-ci',
  template: '<div data-testid="connect-ci-page">{{ orgId() }} {{ projectId() }}</div>',
})
export class ConnectCiStub {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
  readonly active = input(true);
}

@Component({
  selector: 'app-onboarding-enable-collection',
  template: '<div data-testid="enable-collection-page">{{ orgId() }} {{ projectId() }}</div>',
})
export class EnableCollectionStub {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
  readonly activationConfirmed = output<PolicyActivationStateDto>();
}

/**
 * The router the shell reads for its navigation handoff. These cases arrive with the navigation
 * already finished, so no event is in flight and none waits for the created step's handoff.
 */
export function finishedRouter(): {
  events: Subject<unknown>;
  currentNavigation: () => unknown;
  url: string;
} {
  return {
    events: new Subject<unknown>(),
    currentNavigation: () => null,
    url: '/orgs/org-1/onboarding',
  };
}

// Tests replace the selector at runtime to intercept RouterLink without changing production templates.
@Directive({ selector: '[appShellRouterLink]', host: { '[attr.href]': 'href()' } })
export class ShellRouterLinkStub {
  readonly routerLink = input.required<readonly string[]>();
  protected readonly href = computed(() => this.routerLink().join('/'));
}

export interface MountOptions {
  readonly viewport?: OnboardingViewport;
  /** The scope the stub store reports as the one-shot create marker; nothing by default. */
  readonly created?: () => StubScope | null;
  /** The project address the route carries; the org root by default. */
  readonly projectId?: string;
  /**
   * The focus token the store already carries when the shell is built — a revisit inheriting a
   * step moved in an earlier activation.
   */
  readonly focusToken?: number;
}

export interface MountedShell {
  readonly fixture: ComponentFixture<OnboardingShellComponent>;
  readonly root: HTMLElement;
  readonly store: ShellStore;
  /** The route param streams a case drives to change the address. */
  readonly orgParams: BehaviorSubject<ParamMap>;
  readonly childParams: BehaviorSubject<ParamMap>;
}

/**
 * Builds the shell over a fresh stub store. The shell consults the create marker once, when its
 * route subscription first answers, so a case that changes what the store holds or what the route
 * carries passes it here rather than after the component exists.
 */
export async function mountShell(options: MountOptions = {}): Promise<MountedShell> {
  TestBed.resetTestingModule();
  const store = shellStore({
    createdScope: options.created ?? (() => null),
    focusToken: options.focusToken ?? 0,
  });
  const orgParams = new BehaviorSubject(convertToParamMap({ orgId: 'org-1' }));
  const childParams = new BehaviorSubject(
    convertToParamMap(options.projectId === undefined ? {} : { projectId: options.projectId }),
  );
  await TestBed.configureTestingModule({
    imports: [OnboardingShellComponent],
    providers: [
      provideZonelessChangeDetection(),
      (options.viewport ?? new OnboardingViewport()).provider,
      { provide: OnboardingStore, useValue: store },
      { provide: Router, useValue: finishedRouter() },
      {
        provide: LocationStrategy,
        useValue: { prepareExternalUrl: (internal: string) => `/vi${internal}` },
      },
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
  const fixture = TestBed.createComponent(OnboardingShellComponent);
  const root = fixture.nativeElement as HTMLElement;
  await fixture.whenStable();
  return { fixture, root, store, orgParams, childParams };
}
