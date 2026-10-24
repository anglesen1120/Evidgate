import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import type { Completeness } from './verdict-values';

@Component({
  selector: 'app-evidence-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="verdict" [attr.data-tone]="tone()">
      <span class="verdict__label" i18n="@@shared.evidence-badge.dimension">Completeness:</span>
      <span class="verdict__value">{{ label() }}</span>
    </span>
  `,
  styleUrl: './verdict.css',
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
