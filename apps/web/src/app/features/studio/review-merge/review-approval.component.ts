import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCard, HlmCardContent, HlmCardHeader, HlmCardTitle } from '@evidgate/ui/card';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { ReviewActionPreviewComponent } from './review-action-preview.component';
import { MERGE_COPY } from './studio-review-merge.copy';
import type { StudioReviewMergeViewModel } from './studio-review-merge.fixture';

@Component({
  selector: 'app-review-approval',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AbsoluteTimeComponent,
    HlmCard,
    HlmCardContent,
    HlmCardHeader,
    HlmCardTitle,
    StatusPillComponent,
    ReviewActionPreviewComponent,
  ],
  host: { class: 'block min-w-0 wrap-anywhere' },
  templateUrl: './review-approval.component.html',
})
export class ReviewApprovalComponent {
  readonly view = input.required<StudioReviewMergeViewModel>();
  protected readonly copy = MERGE_COPY;
}
