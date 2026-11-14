import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';

import { BADGE_CLASSES, BADGE_TONE_CLASSES } from '../../../shared/ui/chips/badge-classes';
import { quotaMetricLabel } from './quota-metric-label';
import { QuotaStore } from './quota.store';

/**
 * The service pages the per-project breakdown (50 projects unless the request asks for another
 * page size), and one page can still be more rows than the tab should grow with: the panel renders
 * the first few and counts the rest. The org totals beside them already cover every project, so no
 * figure here is a page's sum.
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
  protected readonly notMeasured = $localize`:@@quota.panel.not-measured:Not measured yet.`;

  constructor() {
    this.quota.load();
  }
}
