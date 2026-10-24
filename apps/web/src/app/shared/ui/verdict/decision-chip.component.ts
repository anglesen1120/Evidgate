import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import type { DecisionValue } from './verdict-values';

@Component({
  selector: 'app-decision-chip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="verdict verdict--decision" [attr.data-tone]="tone()">
      <span class="verdict__label" i18n="@@shared.decision-chip.dimension">Decision:</span>
      @if (value(); as decision) {
        <span class="verdict__value">{{ decision }}</span>
      } @else {
        <span class="verdict__value" i18n="@@shared.decision-chip.unavailable">
          Decision unavailable
        </span>
      }
    </span>
  `,
  styleUrl: './verdict.css',
})
export class DecisionChipComponent {
  readonly decision = input<DecisionValue | 'pending' | null | undefined>();

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
        // AC8 requires this literal in both locales; the vi catalog also preserves it.
        return $localize`:@@shared.decision-chip.pending:pending`;
      default:
        return null;
    }
  });
}
