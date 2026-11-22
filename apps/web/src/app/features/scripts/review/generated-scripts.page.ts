import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTableImports } from '@evidgate/ui/table';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { PageHeaderComponent, HeaderActionsDirective } from '../../../shared/ui/page-header';
import { EmptyPatternComponent } from '../../../shared/ui/states/empty-pattern.component';
import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import {
  generatedScriptsFixture,
  type GeneratedScriptsViewModel,
  type ScriptRowViewModel,
} from './generated-scripts.fixture';
import { scriptCopy } from './generated-scripts.copy';
import { scriptNotices, type GeneratedScriptsState } from './generated-scripts.states';
import { isNoticeKey, matchesReviewRecord } from './scripts-review-state';

type SortColumn = 'id' | 'manualCase' | 'status' | 'stability' | 'pr';
// Review-only until script inspection lands. The real data service will replace the fixture.
@Component({
  selector: 'app-generated-scripts-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    RouterLink,
    HlmButton,
    HlmCardImports,
    HlmDialogImports,
    HlmIcon,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    HlmTableImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    PageHeaderComponent,
    HeaderActionsDirective,
    EmptyPatternComponent,
    LoadingPatternComponent,
    InlineFeedbackComponent,
  ],
  host: { class: 'grid min-w-0 gap-4' },
  templateUrl: './generated-scripts.page.html',
})
export class GeneratedScriptsPage {
  private readonly route = inject(ActivatedRoute);
  protected readonly router = inject(Router);
  readonly viewModel = input<GeneratedScriptsViewModel>(generatedScriptsFixture);
  private readonly params = toSignal(this.route.queryParamMap);
  private readonly requested = computed(() => {
    const params = this.params();
    const id = params?.get('generated');
    const code = params?.get('case');
    if (!id && !code) return undefined;
    return this.viewModel().scripts.find(
      (r) => (!id || r.id === id) && (!code || r.manualCase === code || r.automated === code),
    );
  });
  protected readonly state = linkedSignal<GeneratedScriptsState>(() => {
    const params = this.params();
    const key = params?.get('state');
    if (key === 'denied' || key === '403') return '403';
    const requested = this.requested();
    if (
      !matchesReviewRecord(params, {
        project: ['p-storefront'],
        run: ['run-1842'],
        evidence: ['ev-3'],
        generated: requested ? [requested.id] : [],
        case: requested
          ? [requested.manualCase, ...(requested.automated ? [requested.automated] : [])]
          : [],
        revision: requested ? [requested.revision] : [],
      })
    )
      return 'unavailable';
    if (!key || key === 'ready' || key === 'default' || key === 'success')
      return this.viewModel().status;
    return isNoticeKey(key, scriptNotices) ? key : '404';
  });
  protected readonly c = scriptCopy;
  protected readonly notice = computed(() => {
    const k = this.state();
    return k === 'ready' ? null : scriptNotices[k];
  });
  private readonly noMatchPreview = computed(() => this.state() === 'nomatch');
  protected readonly search = linkedSignal(() => (this.noMatchPreview() ? 'gs-9999' : ''));
  protected readonly statusFilter = signal('');
  protected readonly prFilter = signal('');
  protected readonly sort = signal<{ column: SortColumn; ascending: boolean }>({
    column: 'id',
    ascending: true,
  });
  protected readonly selectedId = linkedSignal(
    () => this.requested()?.id ?? this.viewModel().scripts[0]?.id ?? '',
  );
  protected readonly selected = computed(() =>
    this.viewModel().scripts.find((r) => r.id === this.selectedId()),
  );
  private readonly inventory = computed(() => {
    const scripts = this.viewModel().scripts;
    return this.state() === 'mixed'
      ? scripts.map((row) =>
          row.id === 'gs-0042'
            ? { ...row, status: scriptNotices.mixed.title, tone: 'neutral' as const }
            : row,
        )
      : scripts;
  });
  protected readonly statuses = computed(() => [
    ...new Set(this.inventory().map((row) => row.status)),
  ]);
  protected readonly prs = [this.c.merged, this.c.notCreated, this.c.open, this.c.closed];
  protected readonly columns = [
    { label: this.c.script, key: 'id' },
    { label: this.c.manual, key: 'manualCase' },
    { label: this.c.automated, key: null },
    { label: this.c.status, key: 'status' },
    { label: this.c.stability, key: 'stability' },
    { label: this.c.pr, key: 'pr' },
  ] as const;
  protected readonly rows = computed(() => {
    const term = this.search().trim().toLocaleLowerCase(),
      status = this.statusFilter(),
      pr = this.prFilter();
    const rows = this.inventory().filter(
      (r) =>
        (!status || r.status === status) &&
        (!pr || this.delivery(r) === pr) &&
        [r.id, r.manualCase, r.automated, r.title].some((v) =>
          v?.toLocaleLowerCase().includes(term),
        ),
    );
    const { column, ascending } = this.sort();
    return rows.sort(
      (a, b) =>
        this.sortValue(a, column).localeCompare(this.sortValue(b, column), undefined, {
          numeric: true,
        }) * (ascending ? 1 : -1),
    );
  });
  protected order(column: SortColumn): void {
    this.sort.update((s) => ({ column, ascending: s.column !== column || !s.ascending }));
  }
  protected delivery(row: ScriptRowViewModel): string {
    return this.state() === 'closed' && row.id === 'gs-0051' ? this.c.closed : row.pr;
  }
  protected stability(row: ScriptRowViewModel): string {
    return this.state() === 'mixed' && row.id === 'gs-0042' ? this.c.mixedBuild : row.stability;
  }
  private sortValue(row: ScriptRowViewModel, column: SortColumn): string {
    return column === 'pr'
      ? this.delivery(row)
      : column === 'stability'
        ? this.stability(row)
        : row[column];
  }
  protected reset(): void {
    this.search.set('');
    this.statusFilter.set('');
    this.prFilter.set('');
    if (this.state() === 'nomatch') this.state.set('ready');
  }
  protected restore(): void {
    if (this.state() === 'nomatch') this.reset();
    else this.state.set('ready');
  }
}
