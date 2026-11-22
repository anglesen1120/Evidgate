import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { PageHeaderComponent } from '../../../shared/ui/page-header';
import { RepairEvidenceComponent } from '../shadow/repair-evidence.component';
import { reviewContext } from '../shadow/repair-review-context';
import { REPAIR } from '../shadow/repair-review.fixture';
import { RepairReviewNoticeComponent } from '../shadow/repair-review-notice.component';
import { SHADOW_COPY } from '../shadow/shadow.copy';
import { ApprovalDecisionComponent } from './approval-decision.component';
import { APPROVAL_COPY } from './approval.copy';

// Review-only until the approval behavior lands. Its data service will replace this fixture;
// preparing a preview must never imply that a human decision was persisted.
@Component({
  selector: 'app-healing-approval-review-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    HlmButton,
    PageHeaderComponent,
    RepairEvidenceComponent,
    RepairReviewNoticeComponent,
    ApprovalDecisionComponent,
  ],
  host: { class: 'grid min-w-0 gap-3 text-sm text-foreground' },
  templateUrl: './healing-approval.review.page.html',
  styleUrl: '../shadow/repair-panels.css',
})
export class HealingApprovalReviewPage {
  protected readonly ctx = reviewContext('approval');
  protected readonly labels = SHADOW_COPY;
  protected readonly copy = APPROVAL_COPY;
  protected readonly record = REPAIR;
}
