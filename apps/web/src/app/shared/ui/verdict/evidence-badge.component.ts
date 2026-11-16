import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';

import { BADGE_CLASSES, BADGE_ICON_NAMES, BADGE_TONE_CLASSES } from '../chips/badge-classes';

import type { Completeness } from './verdict-values';

@Component({
  selector: 'app-evidence-badge',
  imports: [HlmBadge, HlmIcon],
  host: { class: 'inline-block min-w-0 max-w-full' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span
      class="verdict__label whitespace-nowrap"
      [class.sr-only]="compact()"
      i18n="@@shared.evidence-badge.dimension"
      >Completeness:
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
export class EvidenceBadgeComponent {
  readonly completeness = input<Completeness | null | undefined>();
  readonly showDetail = input(false);
  readonly compact = input(false);
  protected readonly icons = BADGE_ICON_NAMES;

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
  readonly detail = computed(() => {
    const state = this.completeness();
    return state && state !== 'complete' && state !== 'incomplete'
      ? $localize`:@@shared.evidence-badge.unknown-detail:Unknown evidence status: ${state}:state:`
      : this.label();
  });
  readonly compactLabel = computed(() => {
    switch (this.completeness()) {
      case 'complete':
        return $localize`:@@shared.evidence-badge.short.complete:Complete`;
      case 'incomplete':
        return $localize`:@@shared.evidence-badge.short.incomplete:Incomplete`;
      default:
        return $localize`:@@shared.evidence-badge.short.unavailable:Unavailable`;
    }
  });
}
