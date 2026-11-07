import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { hlm } from '@evidgate/ui/utils';

import { TenantStore } from '../../core/session/tenant.store';
import { quotaMetricLabel } from '../../features/settings/quota-retention/quota-metric-label';
import { QuotaStore } from '../../features/settings/quota-retention/quota.store';
import { BADGE_CLASSES, BADGE_TONE_CLASSES } from '../../shared/ui/chips/badge-classes';

@Component({
  selector: 'app-quota-badge',
  imports: [RouterLink, HlmBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'min-w-0 max-w-full',
    '[class.hidden]': 'quota.badge() === null',
    'aria-live': 'polite',
    'aria-atomic': 'true',
  },
  template: `
    @if (quota.badge(); as badge) {
      @if (tenant.orgId(); as orgId) {
        <a
          hlmBadge
          [variant]="'outline'"
          [class]="badge.severity === 'blocked' ? blockedClasses : warnClasses"
          [routerLink]="['/orgs', orgId, 'settings', 'quota-retention']"
          [attr.data-quota-severity]="badge.severity"
          data-quota-badge
        >
          @if (badge.severity === 'blocked') {
            <span i18n="@@quota.badge.blocked">Quota limit reached:</span>
          } @else {
            <span i18n="@@quota.badge.warned">Approaching quota limit:</span>
          }
          <span>{{ metricLabel(badge.metric) }}</span>
        </a>
      }
    }
  `,
})
export class QuotaBadgeComponent {
  protected readonly quota = inject(QuotaStore);
  protected readonly tenant = inject(TenantStore);
  protected readonly metricLabel = quotaMetricLabel;
  // `hlm` merges, so the link's touch target replaces the chip minimum height and width the domain
  // badge classes set instead of competing with them in stylesheet order.
  private readonly linkClasses = hlm(
    BADGE_CLASSES,
    'min-h-(--eg-control-touch) min-w-(--eg-control-touch) no-underline hover:underline focus-visible:outline-solid focus-visible:outline-(length:--eg-focus-width) focus-visible:outline-offset-2 focus-visible:outline-ring',
  );
  protected readonly warnClasses = hlm(this.linkClasses, BADGE_TONE_CLASSES.warning);
  protected readonly blockedClasses = hlm(this.linkClasses, BADGE_TONE_CLASSES.danger);

  constructor() {
    this.quota.load();
  }
}
