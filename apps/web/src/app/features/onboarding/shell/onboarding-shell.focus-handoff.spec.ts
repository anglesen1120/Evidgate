// The step a create opened gets its focus after the app shell has moved focus to `<main>` for the
// same navigation, and not by racing it with a timer.
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  input,
  provideZonelessChangeDetection,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  NavigationCancel,
  NavigationEnd,
  Router,
  convertToParamMap,
  type ParamMap,
} from '@angular/router';
import { BehaviorSubject, Subject, filter } from 'rxjs';

import { OnboardingStore } from '../onboarding.store';
import { ConnectCiPage } from '../steps/connect-ci.page';
import { CreateProjectPage } from '../steps/create-project.page';
import { EnableCollectionPage } from '../steps/enable-collection.page';
import { OnboardingShellComponent } from './onboarding-shell.component';
import { shellStore, type ShellStore, type StubScope } from './onboarding-shell.spec-support';

/** The pages are asserted elsewhere; here the step renders, and these keep their stores out. */
@Component({ selector: 'app-onboarding-create-project', template: '' })
class CreateProjectStub {
  readonly orgId = input.required<string>();
}

@Component({ selector: 'app-onboarding-connect-ci', template: '' })
class ConnectCiStub {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
  readonly active = input(true);
}

@Component({ selector: 'app-onboarding-enable-collection', template: '' })
class EnableCollectionStub {
  readonly orgId = input.required<string>();
  readonly projectId = input.required<string>();
}

/** The router the app shell and the wizard both read, with the navigation state under test. */
interface RouterStub {
  events: Subject<unknown>;
  /** The navigation still activating, as `Router.currentNavigation` exposes it. */
  navigation: unknown;
  currentNavigation: () => unknown;
  url: string;
}

function createRouterStub(): RouterStub {
  const stub: RouterStub = {
    events: new Subject<unknown>(),
    navigation: null,
    currentNavigation: () => stub.navigation,
    url: '/orgs/org-1/onboarding',
  };
  return stub;
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
  let store: ShellStore = shellStore();
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
    /** The scope the store reports as the create marker; the address the route opens by default. */
    readonly createdScope?: () => StubScope | null;
  }

  /**
   * The store the shell reads: already on the step a create opens, with the token the create
   * bumped before this activation existed, so the handoff is the only thing that can move focus.
   */
  function createdStore(options: SetupOptions): ShellStore {
    return shellStore({
      createdScope: options.createdScope ?? (() => ({ orgId: 'org-1', projectId: 'project-1' })),
      viewStep: 2,
      done: { 1: true, 2: false, 3: false, 4: false, 5: false },
      focusToken: 1,
    });
  }

  async function setup(options: SetupOptions): Promise<void> {
    router = createRouterStub();
    router.navigation = options.navigationInFlight ? 'activating' : null;
    store = createdStore(options);
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

  it('cancelled navigation does not leave the cue armed', async () => {
    await setup({ navigationInFlight: true, readyWhenHandoffRuns: true });
    expect(store.consumeCreated).toHaveBeenCalledWith('org-1', 'project-1');

    // The navigation to the step the create opened never completed, so nothing was handed off.
    router.navigation = null;
    router.events.next(new NavigationCancel(1, '/orgs/org-1/onboarding/project-1', 'aborted'));
    await fixture.whenStable();
    expect(order).toEqual([]);

    // A later navigation that has nothing to do with the create keeps the focus the shell gives
    // it: the abandoned cue is not waiting to move focus onto a step this visit never opened.
    router.events.next(new NavigationEnd(2, '/orgs/org-1/settings', '/orgs/org-1/settings'));
    await fixture.whenStable();
    expect(order).toEqual(['app-main']);
    expect(document.activeElement?.id).toBe('app-main');
  });

  it('two quick activations focus only the second', async () => {
    let created: StubScope = { orgId: 'org-1', projectId: 'project-1' };
    await setup({
      navigationInFlight: true,
      readyWhenHandoffRuns: true,
      createdScope: () => created,
    });

    // The first navigation ends and queues its handoff; the second activation lands before the
    // render pass that would run it, so both cues are in flight at once.
    router.navigation = null;
    router.events.next(
      new NavigationEnd(1, '/orgs/org-1/onboarding/project-1', '/orgs/org-1/onboarding/p/1'),
    );
    created = { orgId: 'org-1', projectId: 'project-2' };
    router.navigation = 'activating';
    childParams.next(convertToParamMap({ projectId: 'project-2' }));
    await fixture.whenStable();
    // The abandoned handoff moved nothing: the second activation is still navigating.
    expect(order).toEqual(['app-main']);
    expect(document.activeElement?.id).toBe('app-main');

    router.navigation = null;
    router.events.next(
      new NavigationEnd(2, '/orgs/org-1/onboarding/project-2', '/orgs/org-1/onboarding/p/2'),
    );
    await fixture.whenStable();
    // Only the second activation handed the step its focus, and it did so once: the heading is
    // the last focus change, and the cancelled handoff had already spent nothing.
    expect(order).toEqual(['app-main', 'onboarding-step-title']);
    expect(document.activeElement?.id).toBe('onboarding-step-title');
  });
});
