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
import { PageHeaderImports } from '../../../shared/ui/page-header';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { RunListTableComponent } from './run-list-table.component';
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
    RequestStateComponent,
    RunListTableComponent,
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
  protected draft = { ...EMPTY_RUN_LIST_FILTERS };
  protected readonly desktopFilters = toSignal(
    inject(BreakpointObserver)
      .observe('(min-width: 768px)')
      .pipe(map(({ matches }) => matches)),
    { initialValue: false },
  );
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
  private projectId: string | null = null;
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly loadMoreButton = viewChild<ElementRef<HTMLButtonElement>>('loadMoreControl');
  private readonly pagingRetryButton =
    viewChild<ElementRef<HTMLButtonElement>>('pagingRetryControl');
  private readonly pagingSection = viewChild<ElementRef<HTMLElement>>('paging');
  protected readonly copy = {
    title: $localize`:@@runs.list.title:Runs`,
    description: $localize`:@@runs.list.description:Review collection state and the latest stored decision. Open a run for evidence completeness.`,
    filters: $localize`:@@runs.list.filters:Filter runs`,
    filterHint: $localize`:@@runs.list.filter-hint:Decision and ref name filter the server list. State and run scope filter loaded runs only. Load more to search older runs.`,
    state: $localize`:@@runs.list.state-filter:State in loaded runs`,
    decision: $localize`:@@runs.list.decision-filter:Decision`,
    decisionHint: $localize`:@@runs.list.decision-hint:The latest live evaluation determines the decision. Not evaluated means no live evaluation yet.`,
    pending: $localize`:@@shared.decision-chip.pending:Not evaluated`,
    scope: $localize`:@@runs.list.scope-filter:Scope in loaded runs`,
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
    filteredTitle: $localize`:@@runs.list.filtered-title:No matching runs in the loaded rows`,
    filteredDescription: $localize`:@@runs.list.filtered-description:No loaded runs match these filters. Load more when available, or clear filters.`,
    pagination: $localize`:@@runs.list.pagination:More runs`,
    more: $localize`:@@runs.list.more:Load more`,
    loadingMore: $localize`:@@runs.list.loading-more:Loading more runs…`,
    capped: $localize`:@@runs.list.capped:Automatic browsing paused. Load more to continue, or narrow the ref name.`,
    retryMore: $localize`:@@runs.list.retry-more:Retry loading more`,
    refRejected: $localize`:@@runs.list.ref-invalid:Ref name not applied: use 1 to 255 characters with no control characters.`,
  };
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
          ['state', 'decision', 'scope', 'ref'].map((key) => [key, query.get(key) ?? undefined]),
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
    const query = this.store.toQuery();
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { state: null, decision: null, scope: null, ref: null, ...query },
      queryParamsHandling: 'merge',
    });
  }

  protected clear(): void {
    this.draft = { state: '', decision: '', scope: '', refName: '' };
    this.apply();
  }

  /**
   * The next cursor page (or the retry of one), keeping the visitor's place.
   *
   * Both controls are removed by a successful follow — Load more once the cursor ends, the retry
   * button once the failure clears — and neither is disabled for its own request, so the control
   * that had focus stays focusable while the page is on its way. When the render does remove it,
   * focus moves to the continuation instead of dropping to `body`: the first row this follow
   * appended, else the control that replaced it, else the paging region. A visitor who moved focus
   * elsewhere keeps it.
   */
  protected async loadMore(): Promise<void> {
    const active: unknown = this.document.activeElement;
    const owned =
      active instanceof HTMLElement &&
      (active === this.loadMoreButton()?.nativeElement ||
        active === this.pagingRetryButton()?.nativeElement);
    const shownBefore = this.store.visibleRows().length;
    try {
      await this.store.loadMore();
    } finally {
      if (owned) {
        afterNextRender(() => this.handoffAfterLoadMore(shownBefore), {
          injector: this.injector,
        });
      }
    }
  }

  private handoffAfterLoadMore(shownBefore: number): void {
    const active: unknown = this.document.activeElement;
    // The browser moves focus to `body` when the control under it is removed; a test environment
    // may leave it on the detached node instead, so both count as the same loss. Any other live
    // element means the visitor moved on, and their own focus wins.
    const lost =
      !(active instanceof HTMLElement) || active === this.document.body || !active.isConnected;
    if (!lost) return;
    const retry = this.pagingRetryButton()?.nativeElement;
    const loadMore = this.loadMoreButton()?.nativeElement;
    const appended = this.host.nativeElement.querySelectorAll('[data-run-row]')[shownBefore];
    const appendedLink =
      appended instanceof HTMLElement ? appended.querySelector<HTMLElement>('a') : null;
    // A control that is disabled at this moment cannot take focus, so it is not a candidate: a
    // follow that failed again with a Retry-After wait leaves the retry button disabled, and
    // focusing it would silently leave the place on `body`.
    const usable = (candidate: HTMLElement | null | undefined): candidate is HTMLElement =>
      candidate instanceof HTMLElement && !candidate.matches(':disabled');
    const target = [
      this.store.pagingError() !== null ? retry : undefined,
      appendedLink,
      loadMore,
      retry,
      this.pagingSection()?.nativeElement,
    ].find(usable);
    if (target === undefined) return;
    const addedTabIndex = !target.hasAttribute('tabindex');
    if (addedTabIndex) target.setAttribute('tabindex', '-1');
    target.focus();
    if (addedTabIndex) {
      target.addEventListener('blur', () => target.removeAttribute('tabindex'), { once: true });
    }
  }

  /**
   * The loaded-row count, worded for what the list is actually showing: with no filter it is a
   * plain count, and only a filtered view says "filtered from", because only then is the number
   * larger than what the table shows.
   */
  protected loadedText(): string {
    return this.store.isFiltered()
      ? $localize`:@@runs.list.loaded-count:Filtered from ${formatNumber(this.store.loadedCount(), this.locale)}:count: loaded runs.`
      : $localize`:@@runs.list.loaded-plain:Loaded ${formatNumber(this.store.loadedCount(), this.locale)}:count: runs.`;
  }
}
