import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';
import { QUEUE_COPY } from './healing-queue.copy';

@Component({
  selector: 'app-queue-status',
  imports: [HlmBadge, HlmIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (kind()) {
      @case ('pending') {
        <span
          hlmBadge
          [variant]="'secondary'"
          class="pending bg-(--eg-neutral-bg) text-(--eg-neutral-fg)"
          ><hlm-icon name="lucideClock" />{{ c.pending }}</span
        >
      }
      @case ('validated') {
        <span hlmBadge [variant]="'secondary'" class="bg-(--eg-info-bg) text-(--eg-info-fg)"
          ><hlm-icon name="lucideCheck" />{{ c.validated }}</span
        >
      }
      @case ('overdue') {
        <span hlmBadge [variant]="'secondary'" class="bg-(--eg-warning-bg) text-(--eg-warning-fg)"
          ><hlm-icon name="lucideClock" />{{ c.sla }}</span
        >
      }
      @case ('sandbox') {
        <span hlmBadge [variant]="'outline'"
          ><hlm-icon name="lucideActivity" />{{ c.sandbox }}</span
        >
      }
    }
  `,
})
export class QueueStatusComponent {
  readonly kind = input.required<'pending' | 'validated' | 'sandbox' | 'overdue'>();
  protected readonly c = QUEUE_COPY;
}
