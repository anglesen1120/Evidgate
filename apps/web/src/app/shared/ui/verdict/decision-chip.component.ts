import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';

import { BADGE_CLASSES, BADGE_ICON_NAMES, BADGE_TONE_CLASSES } from '../chips/badge-classes';

import type { DecisionValue } from './verdict-values';

@Component({
  selector: 'app-decision-chip',
  imports: [HlmBadge, HlmIcon],
  host: { class: 'inline-block min-w-0 max-w-full' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span
      class="verdict__label whitespace-nowrap"
      [class.sr-only]="compact()"
      i18n="@@shared.decision-chip.dimension"
      >Decision:
    </span>
    <span
      hlmBadge
      [variant]="'outline'"
      class="verdict verdict--decision"
      [class]="badgeClasses()"
      [attr.data-tone]="tone()"
    >
      <hlm-icon [name]="icons[tone()]" />
      @if (value(); as decision) {
        <span class="verdict__value min-w-0 whitespace-nowrap">{{ decision }}</span>
      } @else {
        <span
          class="verdict__value min-w-0 whitespace-nowrap"
          i18n="@@shared.decision-chip.short-unavailable"
          >Unavailable</span
        >
      }
    </span>
    @if (showDetail() ?? explanation() !== null) {
      <span class="verdict__detail block whitespace-normal [overflow-wrap:anywhere]">{{
        detail()
      }}</span>
    }
  `,
})
export class DecisionChipComponent {
  readonly decision = input<DecisionValue | 'pending' | 'unavailable' | null | undefined>();
  /** Read failures retain visible recovery copy unless the caller supplies a separate error area. */
  readonly showDetail = input<boolean>();
  readonly compact = input(false);
  protected readonly icons = BADGE_ICON_NAMES;
  readonly explanation = computed(() =>
    this.decision() === 'unavailable'
      ? $localize`:@@shared.decision-chip.read-unavailable:The decision could not be read. Try again.`
      : null,
  );
  readonly detail = computed(() => {
    const explanation = this.explanation();
    if (explanation) return explanation;
    const value = this.value();
    if (value) return value;
    const state = this.decision();
    return state
      ? $localize`:@@shared.decision-chip.unknown-detail:Unknown release decision: ${state}:state:`
      : $localize`:@@shared.decision-chip.unavailable:Decision unavailable`;
  });

  readonly tone = computed(() => {
    switch (this.decision()) {
      case 'PASS':
        return 'success';
      case 'FAIL':
        return 'danger';
      case 'INCONCLUSIVE':
        return 'warning';
      case null:
      case undefined:
      case 'pending':
        return 'pending';
      default:
        return 'neutral';
    }
  });
  protected readonly badgeClasses = computed(
    () => `${BADGE_CLASSES} border-current ${BADGE_TONE_CLASSES[this.tone()]}`,
  );

  readonly value = computed(() => {
    const decision = this.decision();
    switch (decision) {
      case 'PASS':
      case 'FAIL':
      case 'INCONCLUSIVE':
        // Stored domain values are locale independent; never translate a release verdict.
        return decision;
      case null:
      case undefined:
      case 'pending':
        return $localize`:@@shared.decision-chip.pending:Not evaluated`;
      default:
        return null;
    }
  });
}
