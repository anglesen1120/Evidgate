import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';

import { BADGE_CLASSES, BADGE_ICON_NAMES, BADGE_TONE_CLASSES } from './badge-classes';

export type StatusTone = 'success' | 'info' | 'warning' | 'danger' | 'neutral';

/** Generic presentation only: the caller supplies a localized label and dimension. */
@Component({
  selector: 'app-status-pill',
  imports: [HlmBadge, HlmIcon],
  host: { class: 'inline-block min-w-0 max-w-full' },
  template: `
    @if (dimension()) {
      <span class="status-pill__dimension whitespace-nowrap">{{ dimension() }}: </span>
    }
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
      <hlm-icon [name]="icons[hasLabel() ? tone() : 'neutral']" />
      <span class="status-pill__label min-w-0 whitespace-nowrap">{{ displayLabel() }}</span>
    </span>
    @if (detail()) {
      <span class="status-pill__detail block whitespace-normal [overflow-wrap:anywhere]">{{
        detail()
      }}</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatusPillComponent {
  readonly tone = input.required<StatusTone>();
  readonly label = input.required<string>();
  readonly dimension = input('');
  /** Optional complete explanation, rendered outside the compact badge. */
  readonly detail = input('');
  protected readonly icons = BADGE_ICON_NAMES;

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
