import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCard, HlmCardContent, HlmCardHeader, HlmCardTitle } from '@evidgate/ui/card';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { EmptyPatternComponent } from '../../../shared/ui/states/empty-pattern.component';
import { MERGE_COPY } from './studio-review-merge.copy';
import type { StudioReviewMergeViewModel } from './studio-review-merge.fixture';

@Component({
  selector: 'app-review-patch',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmCard,
    HlmCardHeader,
    HlmCardTitle,
    HlmCardContent,
    StatusPillComponent,
    EmptyPatternComponent,
  ],
  host: { class: 'block min-w-0 wrap-anywhere' },
  templateUrl: './review-patch.component.html',
})
export class ReviewPatchComponent {
  readonly view = input.required<StudioReviewMergeViewModel>();
  protected readonly copy = MERGE_COPY;
}
