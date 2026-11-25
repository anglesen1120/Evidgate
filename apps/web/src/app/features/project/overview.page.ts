import { formatDate, formatNumber } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, LOCALE_ID } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { SessionStore } from '../../core/session/session.store';
import { PageHeaderImports } from '../../shared/ui/page-header';
import { EmptyPatternComponent } from '../../shared/ui/states/empty-pattern.component';
import { DashboardWidgetComponent } from '../../shared/ui/dashboard/dashboard-widget.component';
import {
  DashboardKpiComponent,
  type DashboardMetric,
} from '../../shared/ui/dashboard/dashboard-kpi.component';
import { retryWithDashboardFocus } from '../../shared/ui/dashboard/dashboard-retry-focus';
import { ErrorPatternComponent } from '../../shared/ui/states/error-pattern.component';
import { LoadingPatternComponent } from '../../shared/ui/states/loading-pattern.component';
import { DashboardMeterComponent } from '../../shared/ui/dashboard/dashboard-meter.component';
import { DashboardActivityComponent } from '../../shared/ui/dashboard/dashboard-activity.component';
import {
  DecisionTrendComponent,
  type DashboardTrendPoint,
} from '../../shared/ui/dashboard/decision-trend.component';
import { shortDateFormat } from '../../shared/time/short-date-format';
import { NavViewService } from '../../shell/nav/nav-view';
import { ProjectDashboardStore } from './dashboard/project-dashboard.store';
import { DASHBOARD_COPY } from './dashboard/dashboard.copy';
import { DashboardRunsComponent } from './dashboard/dashboard-runs.component';
import { DashboardMetricComponent } from './dashboard/dashboard-metric.component';
import {
  decisionsByDay,
  expiringWaivers,
  recentFailures,
  runCounts,
  sampleRuns,
} from './dashboard/dashboard-summary';

@Component({
  selector: 'app-project-overview',
  imports: [
    HlmButton,
    RouterLink,
    PageHeaderImports,
    EmptyPatternComponent,
    DashboardWidgetComponent,
    DashboardKpiComponent,
    ErrorPatternComponent,
    LoadingPatternComponent,
    DashboardMeterComponent,
    DecisionTrendComponent,
    DashboardRunsComponent,
    DashboardMetricComponent,
    DashboardActivityComponent,
  ],
  providers: [ProjectDashboardStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-foreground' },
  templateUrl: './overview.page.html',
  styleUrl: './overview.page.css',
})
export class ProjectOverviewPage {
  protected readonly store = inject(ProjectDashboardStore);
  private readonly session = inject(SessionStore);
  private readonly locale = inject(LOCALE_ID);
  private readonly nav = inject(NavViewService);
  protected readonly copy = DASHBOARD_COPY;
  readonly project = this.session.currentProject;
  protected readonly context = computed(() =>
    [this.project()?.name, this.session.currentOrg()?.name].filter(Boolean).join(' · '),
  );
  protected readonly rows = computed(() => sampleRuns(this.store.runs.value()));
  protected readonly counts = computed(() => runCounts(this.rows(), this.store.decisionsVisible()));
  protected readonly latest = computed(() => this.rows().slice(-5).reverse());
  protected readonly retrySummary = retryWithDashboardFocus;
  protected readonly primaryAction = computed(() => {
    const items = this.nav.groups().flatMap((group) => group.items);
    return ['runs', 'onboarding', 'authoring']
      .map((id) => items.find((item) => item.id === id))
      .find((item) => item !== undefined);
  });
  protected readonly setupLink = computed(
    () =>
      this.nav
        .groups()
        .flatMap((group) => group.items)
        .find((item) => item.id === 'onboarding')?.link,
  );
  protected readonly registerScope = computed(() => {
    if (!this.store.runs.value() || !this.latest().length) return '';
    const count = formatNumber(this.latest().length, this.locale);
    return this.store.runs.value()?.complete
      ? $localize`:@@project.dashboard.register-recent:Showing the ${count}:count: most recent runs in the sample.`
      : $localize`:@@project.dashboard.register-loaded:Showing ${count}:count: runs from the oldest loaded pages.`;
  });
  protected readonly recentTitle = computed(() =>
    this.store.runs.value()?.complete
      ? this.copy.runs
      : $localize`:@@project.dashboard.loaded-runs:Loaded runs (not the latest)`,
  );
  protected readonly metrics = computed<readonly DashboardMetric[]>(() => {
    const sample = this.store.runs.value();
    const counts = this.counts();
    const total = formatNumber(counts.total, this.locale);
    const detail = !sample
      ? ''
      : sample.complete
        ? $localize`:@@project.dashboard.sample-loaded:Loaded sample`
        : $localize`:@@project.dashboard.sample-partial:Oldest pages only; newer runs are not loaded.`;
    return [
      { id: 'runs', label: this.recentTitle(), value: sample ? counts.total : null, detail },
      ...(this.store.decisionsVisible()
        ? (['pass', 'fail', 'inconclusive'] as const).map((id) => {
            const count = formatNumber(counts[id], this.locale);
            return {
              id,
              label: id.toUpperCase(),
              value: !sample
                ? null
                : !counts.total
                  ? this.copy.noData
                  : `${formatNumber((counts[id] / counts.total) * 100, this.locale, '1.0-1')}%`,
              detail: sample
                ? $localize`:@@project.dashboard.sample-fraction:${count}:count: / ${total}:total: runs`
                : '',
            };
          })
        : []),
      {
        id: 'collecting',
        label: this.copy.collecting,
        value: sample ? counts.collecting : null,
        detail: sample ? $localize`:@@project.dashboard.sample-of:Of ${total}:total: runs` : '',
      },
    ];
  });
  protected readonly trend = computed<readonly DashboardTrendPoint[]>(() =>
    decisionsByDay(this.rows(), this.store.decisionsVisible()).map((day) => ({
      id: day.day,
      label: formatDate(`${day.day}T00:00:00Z`, shortDateFormat(this.locale), this.locale, 'UTC'),
      segments: [
        ...(this.store.decisionsVisible()
          ? [
              { id: 'pass', label: 'PASS', value: day.pass, tone: 'success' as const },
              { id: 'fail', label: 'FAIL', value: day.fail, tone: 'danger' as const },
              {
                id: 'inconclusive',
                label: 'INCONCLUSIVE',
                value: day.inconclusive,
                tone: 'neutral' as const,
              },
            ]
          : []),
        { id: 'pending', label: this.copy.pending, value: day.pending, tone: 'neutral' as const },
      ],
    })),
  );
  protected readonly failures = computed(() =>
    this.store.runs.value()?.complete === true ? recentFailures(this.rows(), Date.now()) : null,
  );
  protected readonly failureContext = computed(() => {
    if (this.failures() === null) return this.copy.failuresUnavailable;
    const total = formatNumber(this.counts().fail, this.locale);
    return $localize`:@@project.dashboard.failure-scope:In the last 7 days; ${total}:total: FAIL in the whole sample.`;
  });
  protected readonly waivers = computed(() =>
    expiringWaivers(this.store.waivers.value()?.items ?? [], Date.now()),
  );
  protected readonly quarantines = computed(
    () =>
      this.store.quarantine.value()?.items.filter((row) => row.state === 'active').length ?? null,
  );
  protected readonly checklist = computed(
    () =>
      this.store.tree.value()?.items.reduce((total, node) => total + node.item_count, 0) ?? null,
  );
  protected readonly bindingCounts = computed(() => {
    const rows = this.store.bindings.value()?.items ?? [];
    return {
      active: rows.filter((row) => row.status === 'active').length,
      pending: rows.filter((row) => row.status === 'pending_verification').length,
      disabled: rows.filter((row) => row.status === 'disabled').length,
    };
  });
  protected readonly incidentsVisible = computed(() => this.session.has('healing:triage'));
  protected readonly attentionVisible = computed(
    () =>
      (this.store.runs.visible() && this.store.decisionsVisible()) ||
      this.store.waivers.visible() ||
      this.store.quarantine.visible() ||
      this.incidentsVisible(),
  );
  protected readonly authoringVisible = computed(
    () => this.store.cases.visible() || this.store.tree.visible() || this.store.changes.visible(),
  );
  protected readonly anyVisible = computed(
    () =>
      this.store.runs.visible() ||
      this.attentionVisible() ||
      this.authoringVisible() ||
      this.store.bindings.visible() ||
      this.store.usage.visible() ||
      this.store.audit.visible(),
  );
  protected readonly ai = computed(() =>
    this.store.usage.value()?.metrics.find((metric) => metric.metric === 'ai_tokens'),
  );
  protected readonly results = computed(() =>
    this.store.usage.value()?.metrics.find((metric) => metric.metric === 'results'),
  );
  protected readonly quotaDescription = computed(() => {
    const metric = this.results();
    if (metric?.used === null || metric?.used === undefined) return this.copy.unavailable;
    const used = formatNumber(metric.used, this.locale);
    if (metric.limit === null)
      return $localize`:@@project.dashboard.quota-no-limit:${used}:used: results used · Limit unavailable`;
    const limit = formatNumber(metric.limit, this.locale);
    return $localize`:@@project.dashboard.quota-used:${used}:used: of ${limit}:limit: results used`;
  });
  protected readonly aiDescription = computed(() => {
    const metric = this.ai();
    if (metric?.used === null || metric?.used === undefined) return this.copy.notMetered;
    const used = formatNumber(metric.used, this.locale);
    if (metric.limit === null)
      return $localize`:@@project.dashboard.ai-no-limit:${used}:used: tokens used · Limit unavailable`;
    const limit = formatNumber(metric.limit, this.locale);
    return $localize`:@@project.dashboard.ai-used:${used}:used: of ${limit}:limit: tokens used`;
  });
}
