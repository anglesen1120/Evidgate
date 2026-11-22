import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { combineLatest } from 'rxjs';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import {
  EmptyPatternComponent,
  LoadingPatternComponent,
  InlineFeedbackComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { HISTORY_COPY as c } from './investigation-history.copy';
import { checkoutHistory, addressHistory, historyText } from './investigation-history.fixture';
import { HistoryComparisonComponent } from './history-comparison.component';

// Review-only: a case must not inherit another case's attempts or comparison.
// The behavior task will replace these snapshots with the case-history data service.
@Component({
  selector: 'app-investigation-history-review',
  imports: [
    RouterLink,
    HlmButton,
    HlmCardImports,
    HlmIcon,
    PageHeaderImports,
    StatusPillComponent,
    EmptyPatternComponent,
    LoadingPatternComponent,
    InlineFeedbackComponent,
    NotFoundPatternComponent,
    RetryAfterComponent,
    HistoryComparisonComponent,
  ],
  templateUrl: './investigation-history.review.page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-(--eg-space-5) text-sm wrap-anywhere' },
})
export class InvestigationHistoryReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly params = toSignal(
    combineLatest(this.route.pathFromRoot.map((r) => r.paramMap)),
    { requireSync: true },
  );
  protected readonly query = toSignal(this.route.queryParamMap, { requireSync: true });
  protected readonly scope = computed(() => {
    const maps = this.params();
    return [
      'orgs',
      maps.find((p) => p.has('orgId'))?.get('orgId'),
      'projects',
      maps.find((p) => p.has('projectId'))?.get('projectId'),
    ];
  });
  protected readonly state = computed(() => this.query().get('state') ?? 'default');
  protected readonly c = c;
  protected readonly r = checkoutHistory;
  protected readonly a = addressHistory;
  protected readonly text = historyText;
  protected readonly id = computed(() => this.query().get('id') ?? this.r.id);
  protected readonly revision = computed(() => this.query().get('revision') ?? 'ev-3');
  protected readonly valid = computed(() => {
    const id = this.id();
    if (id === this.r.id) return ['ev-2', 'ev-3', 'ev-4'].includes(this.revision());
    return (id === this.a.id || id === 'case-search-4') && this.revision() === 'ev-3';
  });
  protected readonly inbox = computed(() => ['/dev/failure-inbox', ...this.scope()]);
  protected readonly detail = computed(() => ['/dev/failure-detail', ...this.scope()]);
  protected readonly artifact = computed(() => ['/dev/artifact-viewer', ...this.scope()]);
  protected readonly events = computed(() =>
    this.state() === 'one' ? this.r.events.slice(0, 1) : this.r.events,
  );
  protected readonly tiles = computed(
    () =>
      [
        {
          label: c.completeness,
          value: c.incomplete,
          hint: this.state() === 'one' ? c.beforeSet : c.absent,
          icon: 'lucideCircle',
        },
        { label: c.outcome, value: c.failed, hint: c.retained, icon: 'lucideOctagonX' },
        { label: c.caseRev, value: 'cr-9', hint: this.r.key, icon: 'lucideInfo' },
        {
          label: c.revisions,
          value: String(this.events().length),
          hint: this.state() === 'one' ? 'ev-2' : c.sameRun,
          icon: 'lucideActivity',
        },
      ] as const,
  );
  protected select(revision: string): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { revision },
      queryParamsHandling: 'merge',
    });
  }
  protected reload(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { state: null },
      queryParamsHandling: 'merge',
    });
  }
}
