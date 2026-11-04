import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';

import { BADGE_CLASSES, BADGE_TONE_CLASSES } from '../chips/badge-classes';

import type { Outcome } from './verdict-values';

@Component({
  selector: 'app-outcome-pill',
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
        i18n="@@shared.outcome-pill.dimension"
        >Outcome:</span
      >
      <span class="verdict__value min-w-0 whitespace-normal [overflow-wrap:anywhere]">{{
        label()
      }}</span>
    </span>
  `,
})
export class OutcomePillComponent {
  readonly outcome = input<Outcome | null | undefined>();

  readonly tone = computed(() => {
    switch (this.outcome()) {
      case 'all_passed':
        return 'success';
      case 'has_failures':
        return 'danger';
      default:
        return 'neutral';
    }
  });
  protected readonly badgeClasses = computed(
    () => `${BADGE_CLASSES} ${BADGE_TONE_CLASSES[this.tone()]}`,
  );

  readonly label = computed(() => {
    switch (this.outcome()) {
      case 'all_passed':
        return $localize`:@@shared.outcome-pill.all-passed:All tests passed`;
      case 'has_failures':
        return $localize`:@@shared.outcome-pill.has-failures:Has failures`;
      case 'no_results':
        return $localize`:@@shared.outcome-pill.no-results:No results`;
      default:
        return $localize`:@@shared.outcome-pill.unavailable:Outcome unavailable`;
    }
  });
}
