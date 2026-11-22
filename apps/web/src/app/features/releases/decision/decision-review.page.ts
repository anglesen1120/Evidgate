import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { StatusPillComponent } from '../../../shared/ui/chips';
import {
  DesktopMutationDirective,
  DesktopRequiredNoticeComponent,
} from '../../../shared/ui/narrow';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import {
  EmptyPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
} from '../../../shared/ui/states';
import {
  DecisionChipComponent,
  EvidenceBadgeComponent,
  OutcomePillComponent,
} from '../../../shared/ui/verdict';
import { DecisionExceptions } from './decision-exceptions.component';
import { DecisionPreview } from './decision-preview.component';
import { decisionCopy } from './decision-review.copy';
import { decisionFixture, runFacts } from './decision-review.fixture';

// Review-only until the behavior ships; the data service will replace the fixture,
// not the recorded evaluation or the scope of its exceptions.
@Component({
  selector: 'app-release-decision-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    PageHeaderImports,
    HlmButton,
    HlmCard,
    HlmIcon,
    DesktopMutationDirective,
    DesktopRequiredNoticeComponent,
    StatusPillComponent,
    DecisionChipComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
    LoadingPatternComponent,
    EmptyPatternComponent,
    InlineFeedbackComponent,
    DecisionExceptions,
    DecisionPreview,
  ],
  templateUrl: './decision-review.page.html',
  host: { class: 'grid min-w-0 gap-(--eg-space-5) wrap-anywhere text-foreground' },
})
export class ReleaseDecisionReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly params = toSignal(this.route.queryParamMap);
  protected readonly view = computed(() => decisionFixture(this.params()?.get('state') ?? null));
  protected readonly copy = decisionCopy;
  protected readonly facts = runFacts;
  protected restore(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { state: null },
      queryParamsHandling: 'merge',
    });
  }
}
