import { PercentPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, TemplateRef, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import type { DataTableCellContext, DataTableColumn } from '../../../shared/ui/data-table/data-table-column';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import {
  EmptyPatternComponent,
  ErrorPatternComponent,
  ForbiddenPatternComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import { QUEUE_COPY as c, viewCandidate, viewCandidateSummary } from './healing-queue.copy';
import {
  ADDRESS,
  ageMinutes,
  CANDIDATES,
  eligibleForReview,
  QUEUE_CONTEXT,
  SLA_SPECIMEN,
  SNAPSHOT,
  type QueueCandidateViewModel,
} from './healing-queue.fixture';
import { FILTERS, HealingQueueStore, type QueueFilter } from './healing-queue.store';
import { QueueDetailComponent } from './queue-detail.component';
import { QueueStatusComponent } from './queue-status.component';

type CandidateCell = TemplateRef<DataTableCellContext<QueueCandidateViewModel>>;

// Review-only until the healing queue behaviour lands. Its data service will replace the
// frozen queue; review previews must not submit decisions against real candidate hashes.
@Component({
  selector: 'app-healing-queue-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    PercentPipe,
    DataTableComponent,
    RouterLink,
    HlmButton,
    HlmCard,
    HlmCheckbox,
    HlmDialogImports,
    HlmIcon,
    HlmInput,
    HlmLabel,
    AbsoluteTimeComponent,
    PageHeaderImports,
    EmptyPatternComponent,
    ErrorPatternComponent,
    ForbiddenPatternComponent,
    LoadingPatternComponent,
    NotFoundPatternComponent,
    QueueDetailComponent,
    QueueStatusComponent,
  ],
  providers: [HealingQueueStore],
  templateUrl: './healing-queue.review.page.html',
  styleUrl: './healing-queue.css',
  host: { class: 'block min-w-0' },
})
export class HealingQueueReviewPage {
  protected readonly store = inject(HealingQueueStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly selectCell = viewChild.required<CandidateCell>('selectCell');
  private readonly subjectCell = viewChild.required<CandidateCell>('subjectCell');
  private readonly statusCell = viewChild.required<CandidateCell>('statusCell');
  private readonly shadowCell = viewChild.required<CandidateCell>('shadowCell');
  private readonly actionCell = viewChild.required<CandidateCell>('actionCell');
  protected readonly rowId = (row: QueueCandidateViewModel) => row.id;
  protected readonly viewLabel = viewCandidate;
  protected readonly summaryLabel = viewCandidateSummary;
  protected readonly columns = computed<readonly DataTableColumn<QueueCandidateViewModel>[]>(() => [
    { id: 'select', header: c.choose, role: 'actions', cell: this.rowId, cellTemplate: this.selectCell() },
    { id: 'candidate', header: c.candidate, role: 'identity', overflow: 'wrap', cell: (row) => c[row.kind], cellTemplate: this.subjectCell() },
    { id: 'original', header: c.original, priority: 'primary', cell: (row) => row.failureRef },
    { id: 'status', header: c.status, role: 'status', overflow: 'wrap', cell: () => c.pending, cellTemplate: this.statusCell() },
    { id: 'shadow', header: c.shadow, priority: 'primary', cell: () => c.shadow, cellTemplate: this.shadowCell() },
    { id: 'view', header: c.view, role: 'actions', cell: () => c.view, cellTemplate: this.actionCell() },
  ]);
  private readonly params = toSignal(this.route.paramMap);
  private readonly query = toSignal(this.route.queryParamMap);
  protected readonly c = c;
  protected readonly address = ADDRESS;
  protected readonly canReview = eligibleForReview;
  protected readonly filters = FILTERS;
  protected readonly context = QUEUE_CONTEXT;
  protected readonly requested = computed(() => this.query()?.get('id') ?? ADDRESS.id);
  protected readonly active = computed(() => CANDIDATES.find((row) => row.id === this.requested()));
  protected readonly state = computed(() => {
    const state = this.query()?.get('state') ?? 'default';
    if (state === 'permission-denied') return '403';
    if (state === '403') return state;
    if (
      ['over-sla', '412', 'reloaded', 'disabled', 'unavailable'].includes(state) &&
      this.requested() !== ADDRESS.id
    )
      return '404';
    return this.active() ? state : '404';
  });
  protected readonly populated = computed(() =>
    ['default', 'success', 'confirmation', 'over-sla'].includes(this.state()),
  );
  protected readonly instant = computed(() =>
    this.state() === 'over-sla' ? SLA_SPECIMEN : SNAPSHOT,
  );
  protected readonly available = computed<readonly QueueCandidateViewModel[]>(() =>
    this.state() === 'over-sla' ? [ADDRESS] : CANDIDATES,
  );
  protected readonly eligible = computed(() => this.available().filter(eligibleForReview));
  protected readonly checked = computed(() =>
    this.store.checked().filter((row) => this.available().includes(row)),
  );
  protected readonly dialog = computed(() =>
    this.populated()
      ? (this.store.dialog() ?? (this.state() === 'confirmation' ? 'bulk' : null))
      : null,
  );
  protected readonly visible = computed(() => {
    const query = this.store.search().trim().toLocaleLowerCase();
    const rows = this.available().filter(
      (row) =>
        this.matches(row, this.store.filter()) &&
        (!query ||
          [row.title, row.id, row.hash, row.failureRef, row.failure].some((field) =>
            field.toLocaleLowerCase().includes(query),
          )),
    );
    return rows.sort((a, b) => {
      if (a.createdAt === null)
        return b.createdAt === null
          ? this.store.oldest()
            ? a.id.localeCompare(b.id)
            : b.id.localeCompare(a.id)
          : 1;
      if (b.createdAt === null) return -1;
      return (this.store.oldest() ? 1 : -1) * a.createdAt.localeCompare(b.createdAt);
    });
  });
  protected readonly visibleDetail = computed(
    () => this.visible().find((row) => row.id === this.active()?.id) ?? this.visible()[0],
  );
  protected readonly totals = computed(() => ({
    all: this.available().length,
    sla: this.available().filter((row) => this.matches(row, 'sla')).length,
    locator: this.available().filter((row) => row.kind === 'locator').length,
    wait: this.available().filter((row) => row.kind === 'wait').length,
    samples: this.available().reduce((sum, row) => sum + (row.samples?.total ?? 0), 0),
    passed: this.available().reduce((sum, row) => sum + (row.samples?.passed ?? 0), 0),
  }));
  protected readonly shadowRoute = computed(() => this.path('shadow-detail'));
  protected readonly approvalRoute = computed(() => this.path('healing-approval'));
  protected age(row: QueueCandidateViewModel): number | null {
    return ageMinutes(row, this.instant());
  }
  protected matches(row: QueueCandidateViewModel, filter: QueueFilter): boolean {
    return (
      filter === 'all' || (filter === 'sla' ? (this.age(row) ?? 0) > 24 * 60 : row.kind === filter)
    );
  }
  protected closeDialog(): void {
    this.store.open(null);
    if (this.state() === 'confirmation') this.go('default');
  }
  protected confirm(): void {
    if (!this.checked().length) return;
    this.store.confirm();
    if (this.state() === 'confirmation') this.go('success');
  }
  protected go(state: string): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { state },
      queryParamsHandling: 'merge',
    });
  }
  private path(screen: string): string[] {
    return [
      '/dev',
      screen,
      'orgs',
      this.params()?.get('orgId') ?? '',
      'projects',
      this.params()?.get('projectId') ?? '',
    ];
  }
}
