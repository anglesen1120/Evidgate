import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import type { Outcome } from './verdict-values';

@Component({
  selector: 'app-outcome-pill',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="verdict" [attr.data-tone]="tone()">
      <span class="verdict__label" i18n="@@shared.outcome-pill.dimension">Outcome:</span>
      <span class="verdict__value">{{ label() }}</span>
    </span>
  `,
  styleUrl: './verdict.css',
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
