import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';

import { BADGE_CLASSES, BADGE_TONE_CLASSES } from '../chips/badge-classes';

import type { Completeness } from './verdict-values';

@Component({
  selector: 'app-evidence-badge',
  imports: [HlmBadge],
  host: { class: 'inline-block min-w-0 max-w-full' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span
      hlmBadge
      [variant]="'secondary'"
      class="verdict"
      [class]="badgeClasses()"
      [attr.data-tone]="tone()"
    >
      <span
        class="verdict__label min-w-0 whitespace-normal [overflow-wrap:anywhere]"
        i18n="@@shared.evidence-badge.dimension"
        >Completeness:</span
      >
      <span class="verdict__value min-w-0 whitespace-normal [overflow-wrap:anywhere]">{{
        label()
      }}</span>
    </span>
  `,
})
export class EvidenceBadgeComponent {
  readonly completeness = input<Completeness | null | undefined>();

  readonly tone = computed(() => {
    switch (this.completeness()) {
      case 'complete':
        return 'success';
      case 'incomplete':
        return 'warning';
      default:
        return 'neutral';
    }
  });
  protected readonly badgeClasses = computed(
    () => `${BADGE_CLASSES} ${BADGE_TONE_CLASSES[this.tone()]}`,
  );

  readonly label = computed(() => {
    switch (this.completeness()) {
      case 'complete':
        return $localize`:@@shared.evidence-badge.complete:Evidence complete`;
      case 'incomplete':
        return $localize`:@@shared.evidence-badge.incomplete:Evidence incomplete`;
      default:
        return $localize`:@@shared.evidence-badge.unavailable:Evidence status unavailable`;
    }
  });
}
