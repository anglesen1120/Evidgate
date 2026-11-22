import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { BADGE_CLASSES, BADGE_TONE_CLASSES } from '../../../shared/ui/chips/badge-classes';
import { DesktopMutationDirective } from '../../../shared/ui/narrow';
import { INBOX_COPY } from './failure-inbox.copy';
import {
  ORIGINAL,
  RUN_EVIDENCE,
  type IncidentId,
  type IncidentViewModel,
} from './failure-inbox.fixture';
import { FailureInboxStore } from './failure-inbox.store';

@Component({
  selector: 'app-inbox-preview',
  imports: [
    AbsoluteTimeComponent,
    DesktopMutationDirective,
    HlmBadge,
    HlmButton,
    HlmCard,
    HlmIcon,
    RouterLink,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './inbox-preview.component.html',
})
export class InboxPreviewComponent {
  protected readonly store = inject(FailureInboxStore);
  readonly record = input.required<IncidentViewModel>();
  readonly review = output<readonly IncidentId[]>();
  protected readonly c = INBOX_COPY;
  protected readonly original = ORIGINAL;
  protected readonly run = RUN_EVIDENCE;
  protected readonly pendingClasses = `${BADGE_CLASSES} ${BADGE_TONE_CLASSES.pending}`;
}
