import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';
import { hlm } from '@evidgate/ui/utils';

import { TenantStore } from '../../core/session/tenant.store';
import { quotaMetricLabel } from '../../features/settings/quota-retention/quota-metric-label';
import { QuotaStore } from '../../features/settings/quota-retention/quota.store';
import {
  BADGE_CLASSES,
  BADGE_ICON_NAMES,
  BADGE_TONE_CLASSES,
} from '../../shared/ui/chips/badge-classes';

@Component({
  selector: 'app-quota-badge',
  imports: [RouterLink, HlmBadge, HlmIcon],
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
          [attr.data-quota-scope]="badge.scope"
          data-quota-badge
        >
          <hlm-icon [name]="badge.severity === 'blocked' ? icons.danger : icons.warning" />
          @if (badge.severity === 'blocked') {
            <span i18n="@@quota.badge.blocked">Quota limit reached:</span>
          } @else {
            <span i18n="@@quota.badge.warned">Approaching quota limit:</span>
          }
          <span>&ngsp;{{ metricLabel(badge.metric) }}</span>
          @if (badge.scope === 'project') {
            &ngsp;
            <span i18n="@@quota.badge.project-scope">(project)</span>
          }
        </a>
      }
    }
  `,
})
export class QuotaBadgeComponent {
  protected readonly quota = inject(QuotaStore);
  protected readonly tenant = inject(TenantStore);
  protected readonly metricLabel = quotaMetricLabel;
  protected readonly icons = BADGE_ICON_NAMES;
  // This control explains a warning, unlike a compact table chip: wrap the full metric and scope.
  private readonly linkClasses = hlm(
    BADGE_CLASSES,
    'min-h-(--eg-control-touch) min-w-(--eg-control-touch) max-w-full no-underline hover:underline focus-visible:outline-solid focus-visible:outline-(length:--eg-focus-width) focus-visible:outline-offset-(--eg-focus-offset) focus-visible:outline-ring',
    '[&_.spartan-badge__label]:whitespace-normal [&_.spartan-badge__label>span]:whitespace-normal',
  );
  protected readonly warnClasses = hlm(this.linkClasses, BADGE_TONE_CLASSES.warning);
  protected readonly blockedClasses = hlm(this.linkClasses, BADGE_TONE_CLASSES.danger);

  constructor() {
    this.quota.load();
  }
}
