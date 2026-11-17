import { FocusKeyManager, type FocusableOption } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  ActivatedRoute,
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  Router,
  RouterLink,
} from '@angular/router';
import { combineLatest, distinctUntilChanged, filter, map } from 'rxjs';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';

import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import {
  ErrorPatternComponent,
  ForbiddenPatternComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import type { StepNumber } from '../data/step-derivation';
import { OnboardingStore } from '../onboarding.store';
import { CreateProjectPage } from '../steps/create-project.page';
import { ConnectCiPage } from '../steps/connect-ci.page';
import { EnableCollectionPage } from '../steps/enable-collection.page';
import { EvidencePanelComponent } from './evidence-panel.component';
import { StepPendingComponent } from './step-pending.component';

@Component({
  selector: 'app-onboarding-shell',
  imports: [
    HlmButton,
    RouterLink,
    HlmCardImports,
    HlmIcon,
    CreateProjectPage,
    ConnectCiPage,
    EnableCollectionPage,
    EvidencePanelComponent,
    StepPendingComponent,
    ErrorPatternComponent,
    ForbiddenPatternComponent,
    LoadingPatternComponent,
    NotFoundPatternComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './onboarding-shell.component.html',
  host: { class: 'block min-w-0 font-sans text-foreground' },
})
export class OnboardingShellComponent {
  protected readonly store = inject(OnboardingStore);
  protected readonly outlineVariant = 'outline';
  protected readonly pageHeadingSize = 'page' as const;
  protected readonly sectionHeadingSize = 'section' as const;
  protected readonly loadingLabel = $localize`:@@onboarding.shell.loading:Loading project setup`;
  protected readonly completedCount = computed(() => {
    const done = this.store.done();
    let completed = 0;
    for (const step of this.steps) if (done[step.number]) completed += 1;
    return completed;
  });
  protected readonly progressText = computed(() => {
    const completed = this.completedCount();
    const total = this.steps.length;
    return $localize`:@@onboarding.shell.progress-count:${completed}:COMPLETED: of ${total}:TOTAL: steps complete`;
  });
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly heading = viewChild<ElementRef<HTMLHeadingElement>>('stepHeading');
  private readonly stepButtons = viewChildren<ElementRef<HTMLButtonElement>>('stepButton');
  private readonly stepToggle = viewChild<ElementRef<HTMLButtonElement>>('stepToggle');
  private readonly resourcesLedger = viewChild<unknown, ElementRef<HTMLElement>>(
    'resourcesLedger',
    { read: ElementRef },
  );
  protected readonly viewport = inject(NarrowMutationPolicy);
  protected readonly stepsExpanded = signal(false);
  protected readonly stepListId = 'onboarding-step-list';
  protected readonly showStepsLabel = $localize`:@@onboarding.shell.show-steps:Show all steps`;
  protected readonly hideStepsLabel = $localize`:@@onboarding.shell.hide-steps:Hide steps`;
  protected readonly confirmedProjectLabel = $localize`:@@onboarding.shell.confirmed-project:Confirmed project:`;
  protected readonly resourcesLabel = $localize`:@@onboarding.evidence.title:Created resources`;
  protected readonly confirmedProjectName = computed(() =>
    escapeFormatChars(this.store.project()?.name ?? ''),
  );
  protected readonly viewedSummary = computed(() => {
    const number = this.store.viewStep();
    const total = this.steps.length;
    const label = this.steps[number - 1]?.label ?? '';
    return $localize`:@@onboarding.shell.viewed-summary:Viewing ${number}:NUMBER: of ${total}:TOTAL:: ${label}:LABEL:`;
  });
  protected readonly nextSummary = computed(() => {
    const number = this.store.currentStep();
    const total = this.steps.length;
    if (number === 'enabled') {
      return $localize`:@@onboarding.shell.all-complete:All ${total}:TOTAL: steps complete.`;
    }
    const label = this.steps[number - 1]?.label ?? '';
    return $localize`:@@onboarding.shell.next-summary:Next required ${number}:NUMBER: of ${total}:TOTAL:: ${label}:LABEL:`;
  });
  private stepNavigation: FocusKeyManager<FocusableOption> | undefined;
  protected readonly hasLockedSteps = computed(() =>
    this.steps.some((step) => !this.canSelect(step.number)),
  );
  protected readonly stepIcons = {
    complete: 'lucideCircleCheck',
    required: 'lucideClock',
    upcoming: 'lucideCircle',
  } as const;
  protected readonly stepBodyId = 'onboarding-step-body';
  protected readonly keyboardHelpId = 'onboarding-step-help';
  protected readonly requiredLabel = $localize`:@@onboarding.shell.required:Next required`;
  protected readonly upcomingLabel = $localize`:@@onboarding.shell.upcoming:Upcoming`;
  protected readonly viewingLabel = $localize`:@@onboarding.shell.viewing:Viewing`;
  protected readonly lockedStepReason = $localize`:@@onboarding.shell.locked-step:Finish earlier steps to open later steps.`;
  protected readonly keyboardHelp = $localize`:@@onboarding.shell.keyboard-help:Use Tab or arrow keys to move between steps. Press Enter or Space to open a step.`;

  /**
   * How far the handoff for the step a create opened has got.
   *
   * `shell/layout/shell.component.ts` focuses `<main>` for every navigation that changes the path,
   * and a create navigates, so it does. Its callback is queued first because that component exists
   * first, which is what lets the handoff queued here run last and win. The step-move pass must
   * therefore not spend this cue before the handoff has run: a heading focused earlier would be
   * replaced by the shell's `<main>` focus, and the visitor's place with it.
   */
  private readonly createdHandoff = signal<'none' | 'queued' | 'ran'>('none');
  /**
   * The page's own heading. It is the one named element every branch of the shell renders, so a
   * retry that fails has somewhere stable to put focus: the retry button itself is replaced by the
   * failure branch it triggers, and dropping the user on `<body>` loses both the position and the
   * announcement (WCAG 2.4.3; state-sheet.md:33).
   */
  private readonly pageHeading = viewChild<ElementRef<HTMLHeadingElement>>('pageHeading');
  /**
   * Set by a retry whose read failed. The render pass at the end of the constructor moves focus
   * and clears it; a signal (not a plain field) because the failure may be published after the
   * render it caused was already queued, and the write is what schedules the pass that focuses.
   */
  private readonly announceReloadFailure = signal(false);
  /**
   * Set when this activation renders the step a create opened. The store bumps `focusToken` before
   * this instance exists, so the token check below would treat the move as already honoured and
   * leave focus on `<body>` where the pressed submit was; this makes the hand-off explicit. A
   * signal, like `announceReloadFailure`: a reused instance must re-run the render pass even though
   * no tracked value changed.
   */
  private readonly announceCreatedStep = signal(false);
  protected readonly orgId = signal('');
  private readonly projectId = signal<string | null>(null);
  /**
   * The last `focusToken` this instance has honoured. Which baseline it starts from depends on
   * the branch the first route answer takes: an activation that takes the create marker keeps the
   * store's initial `0`, so the token the create bumped before this instance existed still lands;
   * an activation that reads adopts `store.focusToken()` instead, because that token belongs to a
   * step moved in an earlier visit and must not move focus again.
   */
  private focusedToken = 0;
  /**
   * Identifies the queued created-step handoff. A reset bumps it, so a callback queued by an
   * activation that has since been abandoned finds itself stale and moves no focus.
   */
  private createdHandoffGeneration = 0;

  protected readonly title = $localize`:@@onboarding.shell.title:Set up your project`;
  protected readonly description = $localize`:@@onboarding.shell.description:Resume from server-confirmed resources. Viewing a step does not change your progress.`;
  protected readonly progressLabel = $localize`:@@onboarding.shell.progress:Setup progress`;
  protected readonly completedLabel = $localize`:@@onboarding.shell.complete:Complete`;
  protected readonly backLabel = $localize`:@@onboarding.shell.back:Back`;
  protected readonly nextLabel = $localize`:@@onboarding.shell.next:Next`;
  protected readonly openProjectLabel = $localize`:@@onboarding.shell.open-project:Open project`;
  protected readonly completionLink = computed(() => {
    const project = this.store.project();
    return this.store.viewStep() === 5 && this.store.currentStep() === 'enabled' && project
      ? ['/orgs', this.orgId(), 'projects', project.id, 'overview']
      : null;
  });
  protected readonly retryLabel = $localize`:@@onboarding.shell.retry:Retry loading setup`;
  protected readonly gatedReason = $localize`:@@onboarding.shell.next-disabled:Complete this step before continuing.`;
  protected readonly finalReason = $localize`:@@onboarding.shell.final-step:There is no next setup step.`;
  protected readonly projectCreatedLabel = $localize`:@@onboarding.shell.project-created:Project created`;
  protected readonly steps: readonly { number: StepNumber; label: string }[] = [
    { number: 1, label: $localize`:@@onboarding.step.project:Create project` },
    { number: 2, label: $localize`:@@onboarding.step.ci:Connect CI` },
    { number: 3, label: $localize`:@@onboarding.step.sample:Sample run` },
    { number: 4, label: $localize`:@@onboarding.step.compare:Compare reports` },
    { number: 5, label: $localize`:@@onboarding.step.enable:Enable collection` },
  ];

  constructor() {
    this.wireCreatedStepNavigation();
    this.wireRouteScope();
    afterRenderEffect(() => this.focusMovedStep());
    afterRenderEffect(() => this.focusReloadFailure());
    effect(() => {
      if (!this.viewport.narrow() || this.stepsExpanded()) return;
      // Return focus before the responsive binding hides the desktop list.
      if (
        this.stepButtons().some(
          ({ nativeElement }) => nativeElement === nativeElement.ownerDocument.activeElement,
        )
      ) {
        this.stepToggle()?.nativeElement.focus();
      }
    });
    effect((onCleanup) => {
      const manager = new FocusKeyManager<FocusableOption>(
        this.stepButtons().map(({ nativeElement }) => ({
          focus: () => nativeElement.focus(),
        })),
      )
        .withHorizontalOrientation('ltr')
        .withVerticalOrientation()
        .withHomeAndEnd()
        .withWrap();
      this.stepNavigation = manager;
      onCleanup(() => manager.destroy());
    });
  }

  /**
   * A create navigates to the step it opened, so its handoff waits for the navigation to end: the
   * app shell focuses `<main>` for that same navigation, and a heading focused before it would
   * lose the move again. A navigation that is cancelled or fails never delivers that step, so it
   * drops the cue instead of leaving it to fire on some later, unrelated navigation.
   */
  private wireCreatedStepNavigation(): void {
    this.router.events
      .pipe(
        filter(
          (event) =>
            event instanceof NavigationEnd ||
            event instanceof NavigationCancel ||
            event instanceof NavigationError,
        ),
        takeUntilDestroyed(),
      )
      .subscribe((event) => {
        if (!(event instanceof NavigationEnd)) {
          // The step the create opened never arrived. Dropping the cue alone would leave its
          // token unspent, and the step pass reads an unspent token as a move the user asked
          // for: adopting it is what keeps an abandoned navigation from moving focus later.
          this.resetCreatedCue();
          this.focusedToken = this.store.focusToken();
          return;
        }
        if (!this.announceCreatedStep() || this.createdHandoff() !== 'none') return;
        this.queueCreatedHandoff();
      });
  }

  /**
   * Parent route carries orgId; the optional child carries projectId. Subscribing to the full
   * chain reloads a reused component for either scope change, without local flags.
   */
  private wireRouteScope(): void {
    combineLatest(this.route.pathFromRoot.map((route) => route.paramMap))
      .pipe(
        map((params) => ({
          orgId: params.map((param) => param.get('orgId')).find((id) => id !== null) ?? '',
          projectId:
            params.map((param) => param.get('projectId')).find((id) => id !== null) ?? null,
        })),
        distinctUntilChanged(
          (previous, current) =>
            previous.orgId === current.orgId && previous.projectId === current.projectId,
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ orgId, projectId }) => {
        this.orgId.set(orgId);
        this.projectId.set(projectId);
        // A cue belongs to the activation that armed it: reaching another scope abandons the old
        // one rather than letting it move focus on a step the create did not open, and a handoff
        // already queued for that scope must not run for this one.
        this.resetCreatedCue();
        // The one activation the create's own load published may render it instead of reading it
        // again — that is the single fan-out after a creation. Every other activation reads: this
        // store is a route-level singleton, so its ready state can be a derivation of an earlier
        // visit. Retry (`reload`) loads unconditionally too.
        if (this.store.consumeCreated(orgId, projectId)) {
          // The create opened this step on the user's behalf, so the handoff below moves focus to
          // its heading — the marker is what says the move is theirs, not a page load's.
          this.announceCreatedStep.set(true);
          // A navigation that is still activating publishes the shell's `<main>` focus only when it
          // ends, and nothing in this pass can run after that: the handoff waits for NavigationEnd,
          // which queues it for the pass where the shell has already moved.
          if (this.router.currentNavigation() === null) this.queueCreatedHandoff();
          return;
        }
        // A revisit inherits the token of a step moved in an earlier activation; adopting it as
        // the baseline keeps the first ready render of that revisit from stealing focus.
        this.focusedToken = this.store.focusToken();
        void this.store.load(orgId, projectId);
      });
  }

  /**
   * Focus follows a step the user moved — next, back, a step click, or the step a create
   * opened — and never the first ready render of a page load or a Retry.
   */
  private focusMovedStep(): void {
    const created = this.announceCreatedStep();
    const token = this.store.focusToken();
    if (this.store.status() !== 'ready' || (!created && token <= this.focusedToken)) {
      return;
    }
    // The step a create opened waits for the post-navigation handoff: the app shell focuses
    // `<main>` for that same navigation, so a heading focused in this pass would lose it again.
    if (created && this.createdHandoff() !== 'ran') {
      return;
    }
    // A ready derivation can land one render before the step that renders the heading: keep the
    // cue rather than spending it, so the pass that mounts the target moves the focus.
    const heading = this.heading();
    if (heading === undefined) return;
    this.focusedToken = token;
    this.announceCreatedStep.set(false);
    heading.nativeElement.focus();
  }

  /**
   * A retry that failed, and only one: the first ready render of a page load, the create's
   * activation and a step move all keep focus where the user left it (the step pass above owns
   * the moved step). Reading the ref inside this pass keeps it tracked, so the pass that renders
   * the failure re-runs it with the heading resolved.
   */
  private focusReloadFailure(): void {
    if (!this.announceReloadFailure()) {
      return;
    }
    const heading = this.pageHeading()?.nativeElement;
    if (heading === undefined) {
      return;
    }
    this.announceReloadFailure.set(false);
    heading.focus();
  }

  /**
   * Queues the created step's handoff for the end of the next render. Registered after the app
   * shell's own callback for the same navigation, so it runs after that `<main>` focus, and once
   * only — a later NavigationEnd for the same activation finds the cue already queued.
   *
   * The callback carries the generation it was queued under. A second activation (another scope,
   * or a cue a cancelled navigation dropped) resets the counter, so the older callback returns
   * without moving focus for a cue that is no longer its own.
   */
  private queueCreatedHandoff(): void {
    if (this.createdHandoff() !== 'none') return;
    this.createdHandoff.set('queued');
    const generation = ++this.createdHandoffGeneration;
    // Registered in the read phase, which every default-phase callback of the same pass runs
    // before: the app shell writes `<main>` as the navigation's first stop there, so the heading
    // becomes the last stop whatever order the two subscribers registered in. The callback is
    // queued once per cue, so a pass with no callback left cannot accumulate them.
    afterNextRender(
      {
        read: () => {
          if (generation !== this.createdHandoffGeneration) return;
          this.createdHandoff.set('ran');
          this.focusCreatedStep();
        },
      },
      { injector: this.injector },
    );
  }

  /**
   * Drops the created-step cue and invalidates any handoff queued for it. A cue belongs to one
   * activation: another scope, or a navigation that never delivers the step, ends it.
   */
  private resetCreatedCue(): void {
    this.announceCreatedStep.set(false);
    this.createdHandoff.set('none');
    this.createdHandoffGeneration += 1;
  }

  /**
   * Hands the created step its focus, now that nothing later in this navigation will move it.
   *
   * The heading is resolved here, never held: the wizard replaces it whenever the step changes, and
   * a cue that finds no mounted heading is kept for the pass that mounts one. Marking the handoff
   * as run first is what tells the step-move pass that the shell's own move has already happened.
   */
  private focusCreatedStep(): void {
    if (!this.announceCreatedStep() || this.store.status() !== 'ready') return;
    const heading = this.heading()?.nativeElement;
    if (heading === undefined || !heading.isConnected) return;
    this.announceCreatedStep.set(false);
    this.focusedToken = this.store.focusToken();
    heading.focus();
  }

  protected focusResources(event: Event): void {
    const resources = this.resourcesLedger()?.nativeElement;
    if (resources === undefined) return;
    // Fragment-only navigation resolves against the locale base, not this route.
    event.preventDefault();
    resources.focus();
    resources.scrollIntoView?.({ block: 'start' });
  }

  protected selectStep(step: StepNumber): void {
    if (!this.canSelect(step)) return;
    this.stepsExpanded.set(false);
    this.store.selectStep(step);
    // Reopening the viewed step does not bump the store's focus token.
    this.heading()?.nativeElement.focus();
  }

  protected navigateSteps(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.viewport.narrow() && this.stepsExpanded()) {
      event.preventDefault();
      this.stepsExpanded.set(false);
      this.stepToggle()?.nativeElement.focus();
      return;
    }
    const index = this.stepButtons().findIndex(
      ({ nativeElement }) => nativeElement === event.target,
    );
    if (index < 0) return;
    this.stepNavigation?.updateActiveItem(index);
    this.stepNavigation?.onKeydown(event);
  }

  protected canSelect(step: StepNumber): boolean {
    const current = this.store.currentStep();
    return current === 'enabled' || step <= current;
  }

  protected reload(): void {
    void this.retryLoad();
  }

  /**
   * The shell's own retry of the page-level read. `load` never rejects — it publishes `error`
   * instead (see `OnboardingStore.load`) — so the status afterwards is the answer, and a failure is
   * handed to the render pass that returns focus to the page heading.
   */
  private async retryLoad(): Promise<void> {
    await this.store.load(this.orgId(), this.projectId());
    if (this.store.status() !== 'error') {
      return;
    }
    this.announceReloadFailure.set(true);
  }
}
