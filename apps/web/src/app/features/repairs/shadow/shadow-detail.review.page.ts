import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { PageHeaderComponent } from '../../../shared/ui/page-header';
import { InlineFeedbackComponent } from '../../../shared/ui/states';
import { ProposalStatusComponent } from './proposal-status.component';
import { RepairEvidenceComponent } from './repair-evidence.component';
import { reviewContext } from './repair-review-context';
import { RepairReviewNoticeComponent } from './repair-review-notice.component';
import { REPAIR } from './repair-review.fixture';
import { SHADOW_COPY } from './shadow.copy';

// Review-only until shadow inspection lands. Its data service will replace the pinned fixture;
// a sandbox sample cannot substitute for the original CI failure.
@Component({
  selector: 'app-shadow-detail-review-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    PageHeaderComponent,
    InlineFeedbackComponent,
    HlmButton,
    HlmCardImports,
    RouterLink,
    ProposalStatusComponent,
    RepairEvidenceComponent,
    RepairReviewNoticeComponent,
  ],
  host: { class: 'grid min-w-0 gap-3 text-sm text-foreground' },
  templateUrl: './shadow-detail.review.page.html',
  styleUrl: './repair-panels.css',
})
export class ShadowDetailReviewPage {
  protected readonly ctx = reviewContext('shadow');
  protected readonly copy = SHADOW_COPY;
  protected readonly record = REPAIR;
}
