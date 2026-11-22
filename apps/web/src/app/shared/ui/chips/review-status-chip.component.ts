import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';
import { BADGE_CLASSES, BADGE_ICON_NAMES, BADGE_TONE_CLASSES } from './badge-classes';

/** Presentation evidence supplied by the caller, never a permission or inferred decision. */
export type ReviewStatus =
  | 'waiting'
  | 'over-sla'
  | 'validated'
  | {
      readonly approval: {
        readonly reviewer: string;
        readonly at: string;
        readonly candidateHash: string;
      };
    };

const states = {
  waiting: { label: $localize`:@@shared.review-status.waiting:Awaiting review`, tone: 'pending' },
  'over-sla': { label: $localize`:@@shared.review-status.overdue:Over SLA`, tone: 'warning' },
  validated: { label: $localize`:@@shared.review-status.validated:Validated`, tone: 'info' },
  approved: { label: $localize`:@@shared.review-status.approved:Approved`, tone: 'success' },
  unavailable: {
    label: $localize`:@@shared.review-status.unavailable:Unavailable`,
    tone: 'neutral',
  },
} as const;

@Component({
  selector: 'app-review-status-chip',
  imports: [HlmBadge, HlmIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-block min-w-0 max-w-full' },
  template: `
    <span hlmBadge [variant]="'secondary'" [attr.data-tone]="view().tone" [class]="classes()">
      <hlm-icon [name]="icons[view().tone]" />
      <span>{{ view().label }}</span>
    </span>
  `,
})
export class ReviewStatusChipComponent {
  readonly state = input.required<ReviewStatus | null>();
  protected readonly icons = BADGE_ICON_NAMES;
  protected readonly view = computed(() => {
    const state = this.state();
    if (state === 'waiting' || state === 'over-sla' || state === 'validated') return states[state];
    const record = typeof state === 'object' ? state?.approval : null;
    return typeof record?.reviewer === 'string' &&
      record.reviewer.trim() &&
      typeof record.at === 'string' &&
      record.at.trim() &&
      typeof record.candidateHash === 'string' &&
      record.candidateHash.trim()
      ? states.approved
      : states.unavailable;
  });
  protected readonly classes = computed(
    () => `${BADGE_CLASSES} ${BADGE_TONE_CLASSES[this.view().tone]}`,
  );
}
