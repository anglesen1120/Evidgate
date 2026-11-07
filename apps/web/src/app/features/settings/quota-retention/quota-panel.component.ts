import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';

import { BADGE_CLASSES, BADGE_TONE_CLASSES } from '../../../shared/ui/chips/badge-classes';
import { quotaMetricLabel } from './quota-metric-label';
import { QuotaStore } from './quota.store';

/**
 * The per-project breakdown is unbounded on the wire, so the panel renders the first few rows and
 * counts the rest instead of growing the tab with every project the org has.
 */
const PROJECT_ROW_LIMIT = 5;

@Component({
  selector: 'app-quota-panel',
  imports: [DecimalPipe, HlmBadge, HlmButton, HlmCardImports],
  templateUrl: './quota-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 wrap-anywhere', '[attr.data-quota-state]': 'quota.loadState()' },
})
export class QuotaPanelComponent {
  protected readonly quota = inject(QuotaStore);
  protected readonly metricLabel = quotaMetricLabel;
  protected readonly projectRowLimit = PROJECT_ROW_LIMIT;
  protected readonly warnClasses = `${BADGE_CLASSES} ${BADGE_TONE_CLASSES.warning}`;
  protected readonly blockedClasses = `${BADGE_CLASSES} ${BADGE_TONE_CLASSES.danger}`;

  constructor() {
    this.quota.load();
  }
}
