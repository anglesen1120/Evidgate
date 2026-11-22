import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmTableImports } from '@evidgate/ui/table';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { DecisionChipComponent } from '../../../shared/ui/verdict';
import { QUEUE_COPY } from './healing-queue.copy';
import {
  AFTER,
  BATCHES,
  BEFORE,
  eligibleForReview,
  QUEUE_CONTEXT,
  type QueueCandidateViewModel,
} from './healing-queue.fixture';
import { QueueStatusComponent } from './queue-status.component';

@Component({
  selector: 'app-queue-detail',
  imports: [
    RouterLink,
    HlmButton,
    HlmCard,
    HlmIcon,
    HlmTableImports,
    AbsoluteTimeComponent,
    DecisionChipComponent,
    QueueStatusComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './queue-detail.component.html',
  styleUrl: './healing-queue.css',
})
export class QueueDetailComponent {
  readonly record = input.required<QueueCandidateViewModel>();
  protected readonly canReview = eligibleForReview;
  readonly shadowRoute = input.required<readonly string[]>();
  readonly approvalRoute = input.required<readonly string[]>();
  protected readonly c = QUEUE_COPY;
  protected readonly context = QUEUE_CONTEXT;
  protected readonly before = BEFORE;
  protected readonly after = AFTER;
  protected readonly batches = BATCHES;
}
