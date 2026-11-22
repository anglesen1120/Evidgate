import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmCard, HlmCardContent, HlmCardHeader, HlmCardTitle } from '@evidgate/ui/card';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import { EmptyPatternComponent } from '../../../shared/ui/states/empty-pattern.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { ReviewActionPreviewComponent } from './review-action-preview.component';
import { ReviewApprovalComponent } from './review-approval.component';
import { ReviewHistoryComponent } from './review-history.component';
import { ReviewPatchComponent } from './review-patch.component';
import { MERGE_COPY } from './studio-review-merge.copy';
import { mergeReviewView } from './studio-review-merge.fixture';

// Review-only until the behavior lands: the real data service will replace the local fixture.
@Component({
  selector: 'app-studio-review-merge-review',
  imports: [
    PageHeaderImports,
    HlmBadge,
    HlmCard,
    HlmCardHeader,
    HlmCardTitle,
    HlmCardContent,
    EmptyPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    CopyableIdentifierComponent,
    ReviewActionPreviewComponent,
    ReviewApprovalComponent,
    ReviewHistoryComponent,
    ReviewPatchComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'mx-auto grid w-full max-w-[1440px] min-w-0 gap-(--eg-space-5) text-foreground wrap-anywhere',
  },
  templateUrl: './studio-review-merge-review.page.html',
})
export class StudioReviewMergeReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly params = toSignal(this.route.queryParamMap);
  protected readonly view = computed(() => mergeReviewView(this.params()?.get('state') ?? null));
  protected readonly copy = MERGE_COPY;

  protected show(state: 'default' | 'reloaded'): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { state },
      queryParamsHandling: 'merge',
    });
  }
}
