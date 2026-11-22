import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { QualityBreakdownComponent } from './quality-breakdown.component';
import { QualityOutcomeMixComponent } from './quality-outcome-mix.component';
import { QualityHistoryComponent } from './quality-history.component';
import { qualityFixture, type QualityOverviewViewModel } from './quality-overview.fixture';
import { noticeFor, qualityState, showsSnapshot, type QualityState } from './quality-review-states';

// Review-only until the historical-quality behavior lands. Its data service replaces the fixture;
// a missing observation must remain distinct from a measured zero.
@Component({
  selector: 'app-quality-overview-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    HlmButton,
    HlmCardImports,
    HlmIcon,
    ShellIconComponent,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    PageHeaderImports,
    InlineFeedbackComponent,
    QualityHistoryComponent,
    QualityBreakdownComponent,
    QualityOutcomeMixComponent,
  ],
  host: { class: 'grid min-w-0 grid-cols-[minmax(0,1fr)] gap-4 wrap-anywhere' },
  templateUrl: './quality-overview-review.page.html',
})
export class QualityOverviewReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly model = input<QualityOverviewViewModel>(qualityFixture);
  private readonly query = toSignal(this.route.queryParamMap);
  protected readonly from = computed(() => this.query()?.get('from') ?? this.model().from);
  protected readonly to = computed(() => this.query()?.get('to') ?? this.model().to);
  protected readonly branch = computed(() => this.query()?.get('branch') ?? this.model().branch);
  protected readonly project = computed(() => this.query()?.get('project') ?? this.model().project);
  protected readonly periodForm = computed(
    () =>
      new FormGroup({
        from: new FormControl(this.from(), { nonNullable: true }),
        to: new FormControl(this.to(), { nonNullable: true }),
      }),
  );
  protected readonly state = computed(() => {
    const selected = qualityState(this.query()?.get('state'));
    if (!showsSnapshot(selected)) return selected;
    const period = this.periodState(this.from(), this.to(), this.branch(), this.project());
    return period === 'range' ? selected : period;
  });
  protected readonly showFilters = computed(
    () => !['early', '401', '403', '404'].includes(this.state()),
  );
  protected readonly hasSnapshot = computed(() => showsSnapshot(this.state()));
  protected readonly notice = computed(() => noticeFor(this.state()));
  protected readonly metrics = computed(() =>
    this.model().metrics.map((metric) => {
      if (this.state() === 'zero' && metric.key === 'incomplete') {
        return { ...metric, value: this.copy.noData, description: this.copy.zero };
      }
      if (this.state() === 'unavailable' && (metric.key === 'p50' || metric.key === 'p95')) {
        return { ...metric, value: this.copy.unavailable, description: this.copy.noPercentiles };
      }
      return metric;
    }),
  );
  protected readonly copy = {
    title: $localize`:@@review.quality.title:Quality overview`,
    completeRuns: $localize`:@@review.quality.view-runs:View complete runs`,
    preview: $localize`:@@review.quality.preview-note:Drill-downs are unavailable in this review.`,
    actionUnavailable: $localize`:@@review.quality.action-unavailable:This action is unavailable in this review.`,
    from: $localize`:@@review.quality.from:From · UTC`,
    to: $localize`:@@review.quality.to:To · UTC`,
    branch: $localize`:@@review.quality.branch:Branch`,
    apply: $localize`:@@review.quality.apply:Apply period`,
    reset: $localize`:@@review.quality.reset:Reset period`,
    noData: $localize`:@@review.quality.no-data:No data`,
    unavailable: $localize`:@@review.quality.unavailable:Unavailable`,
    zero: $localize`:@@review.quality.zero-reason:No eligible denominator; there is no rate.`,
    noPercentiles: $localize`:@@review.quality.no-percentiles:Panel unavailable. Percentiles were not supplied; no estimate is shown.`,
    asOf: $localize`:@@review.quality.as-of:Data as of`,
    window: $localize`:@@review.quality.window:30-day window · up to 366 daily points`,
    decisionTime: $localize`:@@review.quality.decision-time:Time to decision`,
    median: $localize`:@@review.quality.median-latency:Median latency`,

    upper: $localize`:@@review.quality.percentile-latency:95th percentile`,
    evaluated: $localize`:@@review.quality.view-evaluated:View evaluated runs`,
    health: $localize`:@@review.quality.health:Collection health`,

    noSamples: $localize`:@@review.quality.no-samples:No data — 0 samples`,
    incompleteRuns: $localize`:@@review.quality.incomplete-runs:View incomplete runs`,
    decisions: $localize`:@@review.quality.decisions:Stored decisions`,
    historyOnly: $localize`:@@review.quality.history-only:This overview describes history, not the current release decision.`,
    release: $localize`:@@review.quality.release:Open release view`,
    source: $localize`:@@review.quality.source:Source boundaries`,
    boundaries: $localize`:@@review.quality.boundaries:Exact read permissions, day bucketing, latency definitions, watermark and range limits remain open. No rates or percentiles are calculated here.`,
    footnote: $localize`:@@review.quality.fixture-note:G4 reference · Illustrative fixture, not live data.`,
  };

  protected apply(): void {
    const { from, to } = this.periodForm().getRawValue();
    this.selectPeriod(from, to, this.branch(), this.project());
  }

  protected reset(): void {
    this.periodForm().reset({ from: this.model().from, to: this.model().to });
    const { from, to, branch, project } = this.model();
    this.selectPeriod(from, to, branch, project);
  }

  private selectPeriod(from: string, to: string, branch: string, project: string): void {
    const state = this.periodState(from, to, branch, project);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams: { state, from, to, branch, project },
    });
  }

  private periodState(from: string, to: string, branch: string, project: string): QualityState {
    const days = (Date.parse(to) - Date.parse(from)) / 86_400_000;
    if (!Number.isFinite(days) || days < 0 || days >= 366) return 'invalid';
    const supplied = this.model();
    return from === supplied.from &&
      to === supplied.to &&
      branch === supplied.branch &&
      project === supplied.project
      ? 'range'
      : 'unprovided';
  }
}
