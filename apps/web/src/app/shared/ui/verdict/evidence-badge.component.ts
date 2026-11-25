import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';
import { hlm } from '@evidgate/ui/utils';

import {
  BADGE_ICON_NAMES,
  COMPACT_VERDICT_CLASSES,
  QUIET_BADGE_CLASSES,
} from '../chips/badge-classes';

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
      [attr.aria-hidden]="compact() ? 'true' : null"
      i18n="@@shared.evidence-badge.dimension"
      >Completeness:
    </span>
    <span
      hlmBadge
      [variant]="'ghost'"
      class="verdict"
      [class]="compact() ? compactClasses : badgeClasses"
      [attr.role]="compact() ? 'img' : null"
      [attr.aria-label]="compact() ? compactName() : null"
      [attr.data-tone]="tone()"
    >
      <hlm-icon
        [name]="compact() ? 'lucideCircle' : icons[tone()]"
        [class.text-(--eg-success-fg)]="compact() && tone() === 'success'"
        [class.text-(--eg-warning-fg)]="compact() && tone() === 'warning'"
      />
      <span class="verdict__value min-w-0 whitespace-nowrap">{{
        compact() ? compactLabel() : standaloneLabel()
      }}</span>
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
  // The compact label must never be clipped by its own box: a narrow column
  // makes the table fall back to records instead of cutting the word.
  protected readonly compactClasses = hlm(COMPACT_VERDICT_CLASSES, 'min-w-max');
  protected readonly compactName = computed(
    () =>
      $localize`:@@shared.evidence-badge.compact-name:Completeness: ${this.compactLabel()}:value:`,
  );

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
  protected readonly badgeClasses = QUIET_BADGE_CLASSES;

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
  // Standalone wording follows its visible dimension prefix, rather than repeating “Evidence”.
  protected readonly standaloneLabel = computed(() => {
    switch (this.completeness()) {
      case 'complete':
        return $localize`:@@shared.evidence-badge.full.complete:Complete`;
      case 'incomplete':
        return $localize`:@@shared.evidence-badge.full.incomplete:Incomplete`;
      default:
        return $localize`:@@shared.evidence-badge.full.unavailable:Unavailable`;
    }
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
