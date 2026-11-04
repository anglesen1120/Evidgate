import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { hlm } from '@evidgate/ui/utils';

import { BADGE_CLASSES, BADGE_TONE_CLASSES } from '../chips/badge-classes';

import type { DecisionValue } from './verdict-values';

@Component({
  selector: 'app-decision-chip',
  imports: [HlmBadge, HlmTooltip],
  host: { class: 'inline-block min-w-0 max-w-full' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span
      hlmBadge
      [variant]="'outline'"
      class="verdict verdict--decision"
      [class]="badgeClasses()"
      [attr.data-tone]="tone()"
      [attr.title]="explanation()"
      [hlmTooltip]="explanation() ? explanationContent : null"
      [tooltipDisabled]="!explanation()"
    >
      <span
        class="verdict__label min-w-0 whitespace-normal [overflow-wrap:anywhere]"
        i18n="@@shared.decision-chip.dimension"
        >Decision:</span
      >
      @if (value(); as decision) {
        <span class="verdict__value min-w-0 whitespace-normal [overflow-wrap:anywhere]">{{
          decision
        }}</span>
      } @else {
        <span
          class="verdict__value min-w-0 whitespace-normal [overflow-wrap:anywhere]"
          i18n="@@shared.decision-chip.unavailable"
        >
          Decision unavailable
        </span>
      }
    </span>
    <ng-template #explanationContent>
      <span
        class="block max-w-[calc(100vw-var(--eg-space-8)*2)] whitespace-normal [overflow-wrap:anywhere]"
        >{{ explanation() }}</span
      >
    </ng-template>
  `,
})
export class DecisionChipComponent {
  readonly decision = input<DecisionValue | 'pending' | 'unavailable' | null | undefined>();
  readonly explanation = computed(() =>
    this.decision() === 'unavailable'
      ? $localize`:@@shared.decision-chip.read-unavailable:The decision could not be read. Try again.`
      : null,
  );

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
  protected readonly badgeClasses = computed(() =>
    hlm(
      BADGE_CLASSES,
      'border-current',
      BADGE_TONE_CLASSES[this.tone()],
      this.explanation() ? 'cursor-help' : '',
    ),
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
