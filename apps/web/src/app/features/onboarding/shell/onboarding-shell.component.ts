import {
  ChangeDetectionStrategy,
  Component,
  afterRenderEffect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import type { ElementRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { combineLatest, distinctUntilChanged, map } from 'rxjs';

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
  styleUrls: ['../../../shared/ui/states/state-panel.css', './onboarding-shell.component.css'],
})
export class OnboardingShellComponent {
  protected readonly store = inject(OnboardingStore);
  private readonly route = inject(ActivatedRoute);
  private readonly heading = viewChild<ElementRef<HTMLHeadingElement>>('stepHeading');
  protected readonly orgId = signal('');
  private readonly projectId = signal<string | null>(null);
  /**
   * The last `focusToken` this instance has honoured. Which baseline it starts from depends on
   * the branch the first route answer takes: an activation that takes the create marker keeps the
   * store's initial `0`, so the token the create bumped before this instance existed still lands
   * (S71 F2); an activation that reads adopts `store.focusToken()` instead, because that token
   * belongs to a step moved in an earlier visit and must not move focus again (S71 N1).
   */
  private focusedToken = 0;

  protected readonly title = $localize`:@@onboarding.shell.title:Set up your project`;
  protected readonly description = $localize`:@@onboarding.shell.description:Resume from server-confirmed resources. Viewing a step does not change your progress.`;
  protected readonly progressLabel = $localize`:@@onboarding.shell.progress:Setup progress`;
  protected readonly completedLabel = $localize`:@@onboarding.shell.complete:Complete`;
  protected readonly incompleteLabel = $localize`:@@onboarding.shell.incomplete:Not complete`;
  protected readonly backLabel = $localize`:@@onboarding.shell.back:Back`;
  protected readonly nextLabel = $localize`:@@onboarding.shell.next:Next`;
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
    // Parent route carries orgId; the optional child carries projectId. Subscribe to the
    // full chain so a reused component reloads for either scope change, without local flags.
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
        // The one activation the create's own load published may render it instead of reading it
        // again — that is the single fan-out after a creation (S71 F1). Every other activation
        // reads: this store is a route-level singleton, so its ready state can be a derivation of
        // an earlier visit (S71 N1). Retry (`reload`) loads unconditionally too.
        if (this.store.consumeCreated(orgId, projectId)) {
          return;
        }
        // A revisit inherits the token of a step moved in an earlier activation; adopting it as
        // the baseline keeps the first ready render of that revisit from stealing focus (N1).
        this.focusedToken = this.store.focusToken();
        void this.store.load(orgId, projectId);
      });

    afterRenderEffect(() => {
      // Focus follows a step the user moved — next, back, a step click, or the step a create
      // opened — and never the first ready render of a page load or a Retry (S71 F2).
      const token = this.store.focusToken();
      if (this.store.status() !== 'ready' || token <= this.focusedToken) {
        return;
      }
      this.focusedToken = token;
      this.heading()?.nativeElement.focus();
    });
  }

  protected canSelect(step: StepNumber): boolean {
    const current = this.store.currentStep();
    return current === 'enabled' || step <= current;
  }

  protected reload(): void {
    void this.store.load(this.orgId(), this.projectId());
  }
}
