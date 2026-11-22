import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { StatusPillComponent } from '../../../shared/ui/chips';
import { DesktopMutationDirective } from '../../../shared/ui/narrow';
import { EmptyPatternComponent } from '../../../shared/ui/states';
import { INBOX_COPY } from './failure-inbox.copy';
import { FILTERS, type IncidentId } from './failure-inbox.fixture';
import { FailureInboxStore } from './failure-inbox.store';
import { InboxChoiceComponent } from './inbox-choice.component';

@Component({
  selector: 'app-inbox-queue',
  imports: [
    DesktopMutationDirective,
    EmptyPatternComponent,
    HlmBadge,
    HlmButton,
    HlmCard,
    HlmCheckbox,
    HlmIcon,
    HlmInput,
    HlmLabel,
    InboxChoiceComponent,
    RouterLink,
    StatusPillComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './inbox-queue.component.html',
})
export class InboxQueueComponent {
  protected readonly store = inject(FailureInboxStore);
  readonly review = output<readonly IncidentId[]>();
  protected readonly c = INBOX_COPY;
  protected readonly filters = FILTERS.map((f) => ({
    ...f,
    options: ['all' as const, ...f.choices],
  }));
  protected readonly statuses = [
    { key: 'all', count: 6 },
    { key: 'open', count: 4 },
    { key: 'triaged', count: 2 },
  ] as const;
}
