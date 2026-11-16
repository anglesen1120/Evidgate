import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';

import { BADGE_CLASSES, BADGE_ICON_NAMES, BADGE_TONE_CLASSES } from '../chips/badge-classes';

import type { Outcome } from './verdict-values';

@Component({
  selector: 'app-outcome-pill',
  imports: [HlmBadge, HlmIcon],
  host: { class: 'inline-block min-w-0 max-w-full' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span
      class="verdict__label whitespace-nowrap"
      [class.sr-only]="compact()"
      i18n="@@shared.outcome-pill.dimension"
      >Outcome:
    </span>
    <span
      hlmBadge
      [variant]="'secondary'"
      class="verdict"
      [class]="badgeClasses()"
      [attr.data-tone]="tone()"
    >
      <hlm-icon [name]="icons[tone()]" />
      <span class="verdict__value min-w-0 whitespace-nowrap">{{ compactLabel() }}</span>
    </span>
    @if (showDetail()) {
      <span class="verdict__detail block whitespace-normal [overflow-wrap:anywhere]">{{
        detail()
      }}</span>
    }
  `,
})
export class OutcomePillComponent {
  readonly outcome = input<Outcome | null | undefined>();
  readonly showDetail = input(false);
  readonly compact = input(false);
  protected readonly icons = BADGE_ICON_NAMES;

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
  readonly detail = computed(() => {
    const state = this.outcome();
    return state && state !== 'all_passed' && state !== 'has_failures' && state !== 'no_results'
      ? $localize`:@@shared.outcome-pill.unknown-detail:Unknown test outcome: ${state}:state:`
      : this.label();
  });
  readonly compactLabel = computed(() => {
    switch (this.outcome()) {
      case 'all_passed':
        return $localize`:@@shared.outcome-pill.short.all-passed:All tests passed`;
      case 'has_failures':
        return $localize`:@@shared.outcome-pill.short.has-failures:Has failures`;
      case 'no_results':
        return $localize`:@@shared.outcome-pill.short.no-results:No results`;
      default:
        return $localize`:@@shared.outcome-pill.short.unavailable:Unavailable`;
    }
  });
}
