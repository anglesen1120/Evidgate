import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';
import { BADGE_CLASSES, BADGE_TONE_CLASSES } from '../../../shared/ui/chips/badge-classes';
import { StatusPillComponent } from '../../../shared/ui/chips';
import type { RepairReviewContext } from './repair-review-context';
import { SHADOW_COPY } from './shadow.copy';

@Component({
  selector: 'app-proposal-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmBadge, HlmIcon, StatusPillComponent],
  template: `<div class="flex flex-wrap items-center gap-2">
    @if (context().validation()) {
      <span hlmBadge [variant]="'secondary'" [class]="validated"
        ><hlm-icon name="lucideCheck" />{{ copy.validated }}</span
      >
    }
    @switch (context().state()) {
      @case ('approved') {
        <app-status-pill [tone]="'success'" [label]="copy.approved" />
      }
      @case ('rejected') {
        <app-status-pill [tone]="'danger'" [label]="copy.rejected" />
      }
      @case ('applied') {
        <app-status-pill [tone]="'neutral'" [label]="copy.applied" />
      }
      @case ('revoked') {
        <app-status-pill [tone]="'neutral'" [label]="copy.revoked" />
      }
      @default {
        <span hlmBadge [variant]="'secondary'" [attr.data-tone]="'pending'" [class]="pending"
          ><hlm-icon name="lucideClock" />{{ status() }}</span
        >
      }
    }
  </div>`,
})
export class ProposalStatusComponent {
  readonly context = input.required<RepairReviewContext>();
  protected readonly copy = SHADOW_COPY;
  protected readonly validated = BADGE_CLASSES + ' ' + BADGE_TONE_CLASSES.info;
  protected readonly pending = BADGE_CLASSES + ' ' + BADGE_TONE_CLASSES.pending;
  protected readonly status = computed(() => {
    const state = this.context().state();
    if (state === 'proposed' || state === 'running') return this.copy[state];
    return this.context().validation() ? this.copy.awaiting : this.copy.noCurrent;
  });
}
