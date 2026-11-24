import { BreakpointObserver } from '@angular/cdk/layout';
import { DOCUMENT, formatNumber } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  Injector,
  LOCALE_ID,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import { HlmAlert } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { SessionStore } from '../../../core/session/session.store';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { CreateRunDialogComponent } from '../actions/create/create-run-dialog.component';
import { CREATE_RUN_COPY } from '../actions/create/create-run.copy';
import { PageHeaderComponent, PageHeaderImports } from '../../../shared/ui/page-header';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { DataTablePagerComponent } from '../../../shared/ui/data-table/data-table-pager.component';
import { RunListTableComponent } from './run-list-table.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { EMPTY_RUN_LIST_FILTERS, RunListStore } from './run-list.store';

@Component({
  selector: 'app-run-list-page',
  imports: [
    FormsModule,
    HlmAlert,
    HlmButton,
    HlmCard,
    HlmCardTitle,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    PageHeaderImports,
    ShellIconComponent,
    NumberValueComponent,
    RequestStateComponent,
    RunListTableComponent,
    DataTablePagerComponent,
    CreateRunDialogComponent,
  ],
  providers: [RunListStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-list.page.html',
  host: {
    class:
      'block min-w-0 text-foreground font-[family-name:var(--eg-font-body)] text-sm leading-[var(--eg-leading-sm)]',
    '[attr.data-run-list-status]': 'store.status()',
  },
})
export class RunListPage {
  protected readonly store = inject(RunListStore);

  private readonly route = inject(ActivatedRoute);

  private readonly router = inject(Router);

  private readonly locale = inject(LOCALE_ID);
  private readonly session = inject(SessionStore);
  protected readonly viewport = inject(NarrowMutationPolicy);

  protected readonly desktopFilters = toSignal(
    inject(BreakpointObserver)
      .observe('(min-width: 768px)')
      .pipe(map(({ matches }) => matches)),
    { initialValue: false },
  );

  private readonly document = inject(DOCUMENT);

  private readonly injector = inject(Injector);

  protected draft = { ...EMPTY_RUN_LIST_FILTERS };
  protected readonly activeFilterLabel = computed(() => {
    const count = formatNumber(
      Object.values(this.store.filters()).filter(Boolean).length,
      this.locale,
    );
    return $localize`:@@runs.list.active-filters:Active filters: ${count}:count:`;
  });
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() => {
    const status = this.store.status();
    return status === 'idle' ? 'loading' : status;
  });
  protected readonly retryReady = signal(true);
  protected readonly createTarget = signal<string | null>(null);
  protected readonly canCreate = computed(() => this.session.has('run:create'));
  protected readonly createCopy = CREATE_RUN_COPY;
  protected projectId: string | null = null;
  private readonly pagingRetryButton =
    viewChild<ElementRef<HTMLButtonElement>>('pagingRetryControl');
  private readonly pagingSection = viewChild<ElementRef<HTMLElement>>('paging');
  private readonly pageHeader = viewChild(PageHeaderComponent);
  protected readonly focusCreateFallback = (): void => this.pageHeader()?.focusTitle();
  protected readonly copy = {
    title: $localize`:@@runs.list.title:Runs`,
    description: $localize`:@@runs.list.description:Review collection state and the latest stored decision. Open a run for evidence completeness.`,
    loadedRuns: $localize`:@@runs.list.page-runs:Runs on this page`,
    failedDecisions: $localize`:@@runs.list.failed-decisions:Stored FAIL decisions`,
    reruns: $localize`:@@runs.list.labeled-reruns:Labeled reruns`,
    register: $localize`:@@runs.list.table-caption:Project runs`,
    filters: $localize`:@@runs.list.filters:Filter runs`,
    filterHint: $localize`:@@runs.list.page-filter-hint:Decision and ref name filter the server list. State and run scope filter this page only. Next searches further.`,
    state: $localize`:@@runs.list.page-state-filter:State on this page`,
    decision: $localize`:@@runs.list.decision-filter:Decision`,
    decisionHint: $localize`:@@runs.list.decision-hint:The latest live evaluation determines the decision. Not evaluated means no live evaluation yet.`,
    pending: $localize`:@@shared.decision-chip.pending:Not evaluated`,
    scope: $localize`:@@runs.list.page-scope-filter:Scope on this page`,
    ref: $localize`:@@runs.list.ref-filter:Ref name (exact match)`,
    any: $localize`:@@runs.list.any:All`,
    created: $localize`:@@shared.run-state.created:Created`,
    queued: $localize`:@@shared.run-state.queued:Queued`,
    running: $localize`:@@shared.run-state.running:Running`,
    collecting: $localize`:@@shared.run-state.collecting:Collecting`,
    complete: $localize`:@@shared.run-state.complete:Complete`,
    incomplete: $localize`:@@shared.run-state.incomplete:Incomplete`,
    canceled: $localize`:@@shared.run-state.canceled:Canceled`,
    original: $localize`:@@runs.list.original:Original run`,
    rerun: $localize`:@@runs.list.rerun:Rerun`,
    reprocess: $localize`:@@runs.list.reprocess:Reprocessed`,
    apply: $localize`:@@runs.list.apply:Apply filters`,
    clear: $localize`:@@runs.list.clear:Clear filters`,
    emptyTitle: $localize`:@@runs.list.empty-title:No runs recorded`,
    emptyDescription: $localize`:@@runs.list.empty-description:Runs will appear here when a producer creates them.`,
    filteredTitle: $localize`:@@runs.list.page-filtered-title:No matching runs on this page`,
    filteredDescription: $localize`:@@runs.list.page-filtered-description:No runs on this page match these filters. Next searches further when available, or clear filters.`,
    emptyPageTitle: $localize`:@@runs.list.empty-page-title:No runs on this page`,
    emptyPageDescription: $localize`:@@runs.list.empty-page-description:Use Previous to revisit a page, or Next to search further when available.`,
    pagination: $localize`:@@runs.list.page-pagination:Run pages`,
    retryMore: $localize`:@@runs.list.page-retry:Retry page`,
    refRejected: $localize`:@@runs.list.ref-invalid:Ref name not applied: use 1 to 255 characters with no control characters.`,
  };
  protected readonly loadedSummary = computed(() => {
    const rows = this.store.rows();
    const byState = new Map<string, number>();
    let failed = 0;
    let reruns = 0;
    for (const row of rows) {
      byState.set(row.state, (byState.get(row.state) ?? 0) + 1);
      if (row.decision === 'FAIL') failed++;
      if (row.rerun_label) reruns++;
    }
    return {
      byState,
      metrics: [
        { label: this.copy.loadedRuns, value: rows.length, icon: 'run' },
        { label: this.copy.collecting, value: byState.get('collecting') ?? 0, icon: 'queue' },
        { label: this.copy.failedDecisions, value: failed, icon: 'shield' },
        { label: this.copy.reruns, value: reruns, icon: 'layers' },
      ] as const,
    };
  });
  protected readonly showing = computed(() => {
    const shown = formatNumber(this.store.visibleRows().length, this.locale);
    return $localize`:@@runs.list.showing:Showing ${shown}:shown:`;
  });
  protected readonly ofLoaded = computed(() => {
    const count = formatNumber(this.store.loadedCount(), this.locale);
    return $localize`:@@runs.list.of-page:Of ${count}:count: runs on this page`;
  });
  protected readonly subtitle = computed(() =>
    this.status() === 'ready'
      ? `${this.copy.loadedRuns}: ${formatNumber(this.store.loadedCount(), this.locale)} · ${this.activeFilterLabel()}`
      : this.copy.description,
  );
  protected readonly stateOptions = [
    { value: '', label: this.copy.any },
    { value: 'created', label: this.copy.created },
    { value: 'queued', label: this.copy.queued },
    { value: 'running', label: this.copy.running },
    { value: 'collecting', label: this.copy.collecting },
    { value: 'complete', label: this.copy.complete },
    { value: 'incomplete', label: this.copy.incomplete },
    { value: 'canceled', label: this.copy.canceled },
  ];
  protected readonly decisionOptions = [
    { value: '', label: this.copy.any },
    { value: 'pending', label: this.copy.pending },
    { value: 'PASS', label: 'PASS' },
    { value: 'FAIL', label: 'FAIL' },
    { value: 'INCONCLUSIVE', label: 'INCONCLUSIVE' },
  ];
  protected readonly scopeOptions = [
    { value: '', label: this.copy.any },
    { value: 'original', label: this.copy.original },
    { value: 'rerun', label: this.copy.rerun },
    { value: 'reprocess', label: this.copy.reprocess },
  ];
  protected readonly stateLabel = (value: unknown) =>
    this.stateOptions.find((option) => option.value === value)?.label ?? this.copy.any;
  protected readonly decisionLabel = (value: unknown) =>
    this.decisionOptions.find((option) => option.value === value)?.label ?? this.copy.any;
  protected readonly scopeLabel = (value: unknown) =>
    this.scopeOptions.find((option) => option.value === value)?.label ?? this.copy.any;
  protected openCreate(): void {
    const projectId = this.projectId;
    if (
      !projectId ||
      !this.canCreate() ||
      !this.viewport.allowsMutation() ||
      this.session.currentProject()?.id !== projectId
    )
      return;
    this.createTarget.set(projectId);
  }

  constructor() {
    // Observe ancestors too: projectId belongs to the shell's project route, not this lazy leaf.
    combineLatest([
      combineLatest(this.route.pathFromRoot.map((route) => route.paramMap)),
      this.route.queryParamMap,
    ])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        const projectId = [...params]
          .reverse()
          .map((param) => param.get('projectId'))
          .find(Boolean);
        if (!projectId) return;
        const raw = Object.fromEntries(
          ['state', 'decision', 'scope', 'ref', 'cursor', 'pageSize'].map((key) => [
            key,
            query.get(key) ?? undefined,
          ]),
        );
        const current = this.store.toQuery();
        // A form submission already updated the store. Its own URL emission must not reset rows.
        if (
          this.projectId !== projectId ||
          Object.keys(raw).some((key) => (raw[key] ?? '') !== (current[key] ?? ''))
        ) {
          this.projectId = projectId;
          void this.store.init(projectId, raw);
          // The URL is the form's source only when it actually changed. A refused ref name is never
          // written to the URL, so re-syncing on an unchanged query would wipe the very value the
          // inline error is about; `init` patches the filters synchronously, before its first await.
          this.draft = { ...this.store.filters() };
        }
      });
    effect((onCleanup) => {
      const seconds = (this.store.pagingError() ?? this.store.loadError())?.retryAfterSeconds;
      this.retryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(() => this.retryReady.set(true), seconds * 1000);
        onCleanup(() => clearTimeout(timer));
      }
    });
  }

  protected apply(): void {
    void this.store.setFilters({ ...this.draft });
    this.syncQuery();
  }

  protected clear(): void {
    this.draft = { state: '', decision: '', scope: '', refName: '' };
    this.apply();
  }

  protected async navigatePage(direction: 'next' | 'previous' | 'retryPage'): Promise<void> {
    const failure = this.store.pagingError();
    if (failure && (!failure.retryable || !this.retryReady())) return;
    const active = this.document.activeElement;
    const ownedRetry = active === this.pagingRetryButton()?.nativeElement;
    await this.store[direction]();
    this.syncQuery();
    if (ownedRetry) {
      afterNextRender(() => this.restorePagingFocus(), { injector: this.injector });
    }
  }

  protected async changePageSize(size: number): Promise<void> {
    const active = this.document.activeElement;
    const owned = active instanceof HTMLElement && active.closest('app-data-table-pager') !== null;
    const request = this.store.setPageSize(size);
    this.syncQuery();
    await request;
    if (owned) {
      afterNextRender(() => this.restorePagingFocus(), { injector: this.injector });
    }
  }

  private syncQuery(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        state: null,
        decision: null,
        scope: null,
        ref: null,
        cursor: null,
        pageSize: null,
        ...this.store.toQuery(),
      },
      queryParamsHandling: 'merge',
    });
  }

  private restorePagingFocus(): void {
    const active = this.document.activeElement;
    if (active instanceof HTMLElement && active !== this.document.body && active.isConnected)
      return;
    const retry = this.pagingRetryButton()?.nativeElement;
    const target = retry && !retry.disabled ? retry : this.pagingSection()?.nativeElement;
    target?.focus();
  }

  protected loadedText(): string {
    const count = formatNumber(this.store.loadedCount(), this.locale);
    return this.store.isFiltered()
      ? $localize`:@@runs.list.page-filtered-count:Filtered from ${count}:count: runs on this page.`
      : $localize`:@@runs.list.page-count:Fetched ${count}:count: runs on this page.`;
  }
}
