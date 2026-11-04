import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';

import { BADGE_CLASSES, BADGE_TONE_CLASSES } from './badge-classes';

export type StatusTone = 'success' | 'info' | 'warning' | 'danger' | 'neutral';

/** Generic presentation only: the caller supplies a localized label and dimension. */
@Component({
  selector: 'app-status-pill',
  imports: [HlmBadge],
  host: { class: 'inline-block min-w-0 max-w-full' },
  template: `
    <span
      hlmBadge
      [variant]="'secondary'"
      [class]="badgeClasses()"
      class="status-pill"
      [class.status-pill--success]="hasLabel() && tone() === 'success'"
      [class.status-pill--info]="hasLabel() && tone() === 'info'"
      [class.status-pill--warning]="hasLabel() && tone() === 'warning'"
      [class.status-pill--danger]="hasLabel() && tone() === 'danger'"
      [class.status-pill--neutral]="!hasLabel() || tone() === 'neutral'"
    >
      @if (dimension()) {
        <span class="status-pill__dimension min-w-0 whitespace-normal [overflow-wrap:anywhere]"
          >{{ dimension() }}:</span
        >
      }
      <span class="status-pill__label min-w-0 whitespace-normal [overflow-wrap:anywhere]">{{
        displayLabel()
      }}</span>
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatusPillComponent {
  readonly tone = input.required<StatusTone>();
  readonly label = input.required<string>();
  readonly dimension = input('');

  protected readonly hasLabel = computed(() => Boolean(this.label()?.trim()));
  protected readonly badgeClasses = computed(
    () => `${BADGE_CLASSES} ${BADGE_TONE_CLASSES[this.hasLabel() ? this.tone() : 'neutral']}`,
  );
  protected readonly displayLabel = computed(() =>
    this.hasLabel()
      ? this.label()
      : $localize`:@@shared.status-pill.unavailable:Status unavailable`,
  );
}
