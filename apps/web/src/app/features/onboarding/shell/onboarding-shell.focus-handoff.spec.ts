// The step a create opened gets its focus after the app shell has moved focus to `<main>` for the
// same navigation, and not by racing it with a timer.
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  provideZonelessChangeDetection,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  NavigationEnd,
  Router,
  convertToParamMap,
  type ParamMap,
} from '@angular/router';
import { BehaviorSubject, Subject, filter } from 'rxjs';

import type { StepNumber } from '../data/step-derivation';
import { OnboardingStore } from '../onboarding.store';
import { ConnectCiPage } from '../steps/connect-ci.page';
import { CreateProjectPage } from '../steps/create-project.page';
import { EnableCollectionPage } from '../steps/enable-collection.page';
import { OnboardingShellComponent } from './onboarding-shell.component';

/** The pages are asserted elsewhere; here the step renders, and these keep their stores out. */
@Component({ selector: 'app-onboarding-create-project', template: '' })
class CreateProjectStub {
  readonly orgId = input.required<string>();
}

@Component({ selector: 'app-onboarding-connect-ci', template: '' })
class ConnectCiStub {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
}

@Component({ selector: 'app-onboarding-enable-collection', template: '' })
class EnableCollectionStub {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
}

/** The scope a stub store reports as the one-shot create marker. */
interface StubScope {
  orgId: string;
  projectId: string | null;
}

/** The router the app shell and the wizard both read, with the navigation state under test. */
interface RouterStub {
  events: Subject<unknown>;
  /** The navigation still activating, as `Router.currentNavigation` exposes it. */
  navigation: unknown;
  currentNavigation: () => unknown;
}

function createRouterStub(): RouterStub {
  const stub: RouterStub = {
    events: new Subject<unknown>(),
    navigation: null,
    currentNavigation: () => stub.navigation,
  };
  return stub;
}

function shellStore(createdScope: () => StubScope | null) {
  const viewStep = signal<StepNumber>(2);
  const done = signal<Readonly<Record<StepNumber, boolean>>>({
    1: true,
    2: false,
    3: false,
    4: false,
    5: false,
  });
  return {
    status: signal<'loading' | 'ready' | 'error'>('ready'),
    failure: signal<'forbidden' | 'not-found' | 'unavailable' | null>(null),
    viewStep,
    currentStep: signal<StepNumber | 'enabled'>(2),
    done,
    project: signal<{ id: string; name: string } | null>({
      id: 'project-1',
      name: 'Storefront web',
    }),
    focusToken: signal(1),
    canGoNext: computed(() => false),
    canGoBack: computed(() => true),
    load: vi.fn<(orgId: string, projectId: string | null) => Promise<void>>(() =>
      Promise.resolve(),
    ),
    consumeCreated: vi.fn((orgId: string, projectId: string | null) => {
      const created = createdScope();
      return created !== null && created.orgId === orgId && created.projectId === projectId;
    }),
    selectStep: vi.fn(),
    next: vi.fn(),
    back: vi.fn(),
  };
}

/**
 * Stands in for `shell/layout/shell.component.ts`: it subscribes to the same navigation events and
 * queues the same `<main>` focus. It is the parent, so — as in the app — its subscription and its
 * callback come first.
 */
@Component({
  selector: 'app-shell-host',
  imports: [OnboardingShellComponent],
  template: `
    <main #appMain id="app-main" tabindex="-1"></main>
    <app-onboarding-shell />
  `,
})
class ShellHostComponent {
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly main = viewChild<ElementRef<HTMLElement>>('appMain');

  constructor() {
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        afterNextRender(
          () => {
            this.main()?.nativeElement.focus();
          },
          { injector: this.injector },
        );
      });
  }
}

describe('OnboardingShellComponent focus handoff', () => {
  let router: RouterStub;
  let store = shellStore(() => null);
  let fixture: ComponentFixture<ShellHostComponent>;
  let childParams: BehaviorSubject<ParamMap>;
  let order: string[];

  /** Records the order focus landed in, so a move the shell takes back is visible. */
  const recordFocus = (event: FocusEvent): void => {
    order.push((event.target as HTMLElement).id);
  };

  interface SetupOptions {
    /** Whether a navigation is still activating while the wizard subscribes. */
    readonly navigationInFlight: boolean;
    /** The state the store reports when the handoff runs. */
    readonly readyWhenHandoffRuns: boolean;
  }

  async function setup(options: SetupOptions): Promise<void> {
    router = createRouterStub();
    router.navigation = options.navigationInFlight ? 'activating' : null;
    store = shellStore(() => ({ orgId: 'org-1', projectId: 'project-1' }));
    if (!options.readyWhenHandoffRuns) store.status.set('loading');
    childParams = new BehaviorSubject(convertToParamMap({ projectId: 'project-1' }));
    order = [];
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [ShellHostComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: OnboardingStore, useValue: store },
        { provide: Router, useValue: router },
        {
          provide: ActivatedRoute,
          useValue: {
            pathFromRoot: [
              { paramMap: new BehaviorSubject(convertToParamMap({ orgId: 'org-1' })) },
              { paramMap: childParams },
            ],
          },
        },
      ],
    })
      .overrideComponent(OnboardingShellComponent, {
        remove: { imports: [CreateProjectPage, ConnectCiPage, EnableCollectionPage] },
        add: { imports: [CreateProjectStub, ConnectCiStub, EnableCollectionStub] },
      })
      .compileComponents();
    fixture = TestBed.createComponent(ShellHostComponent);
    document.addEventListener('focusin', recordFocus, true);
    await fixture.whenStable();
  }

  async function navigate(): Promise<void> {
    // The navigation finished: NavigationEnd follows it, and both subscribers queue their move.
    router.navigation = null;
    router.events.next(
      new NavigationEnd(1, '/orgs/org-1/onboarding/project-1', '/orgs/org-1/onboarding/project-1'),
    );
    await fixture.whenStable();
  }

  afterEach(() => {
    document.removeEventListener('focusin', recordFocus, true);
  });

  it('shell hands off heading focus after main focus', async () => {
    // A navigation that is still activating when the wizard arms the cue for the created step.
    await setup({ navigationInFlight: true, readyWhenHandoffRuns: true });
    expect(store.consumeCreated).toHaveBeenCalledWith('org-1', 'project-1');

    await navigate();
    // The app shell's move to `<main>` came first and the wizard's handoff came second: either
    // other order would have left the visitor on `<main>` instead of on the step it opened.
    expect(order).toEqual(['app-main', 'onboarding-step-title']);
    expect(document.activeElement?.id).toBe('onboarding-step-title');
  });

  it('keeps the created step cue until the step it names has rendered', async () => {
    // The derivation that renders the step lands one pass after the navigation answered.
    await setup({ navigationInFlight: true, readyWhenHandoffRuns: false });
    await navigate();
    expect(order).toEqual(['app-main']);
    expect(document.activeElement?.id).toBe('app-main');

    store.status.set('ready');
    await fixture.whenStable();
    // The cue was kept rather than spent, and the heading it names took the focus afterwards.
    expect(order).toEqual(['app-main', 'onboarding-step-title']);
    expect(document.activeElement?.id).toBe('onboarding-step-title');
  });
});
