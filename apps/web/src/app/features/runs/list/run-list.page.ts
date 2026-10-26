import { formatNumber } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { combineLatest } from 'rxjs';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { RunListTableComponent } from './run-list-table.component';
import { EMPTY_RUN_LIST_FILTERS, RunListStore } from './run-list.store';

@Component({
  selector: 'app-run-list-page',
  standalone: true,
  imports: [FormsModule, RequestStateComponent, RunListTableComponent],
  providers: [RunListStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-list.page.html',
  styleUrl: './run-list.page.css',
  host: { '[attr.data-run-list-status]': 'store.status()' },
})
export class RunListPage {
  protected readonly store = inject(RunListStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly locale = inject(LOCALE_ID);
  protected draft = { ...EMPTY_RUN_LIST_FILTERS };
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() => {
    const status = this.store.status();
    return status === 'idle' ? 'loading' : status;
  });
  protected readonly retryReady = signal(true);
  private projectId: string | null = null;
  protected readonly copy = {
    title: $localize`:@@runs.list.title:Runs`,
    description: $localize`:@@runs.list.description:Review collection state and stored decisions. Evidence completeness is not provided by this list.`,
    filters: $localize`:@@runs.list.filters:Filter loaded runs`,
    filterHint: $localize`:@@runs.list.filter-hint:State, decision and run scope filter loaded runs only. Ref name filters the server list. Load more to search older runs.`,
    state: $localize`:@@runs.list.state-filter:State in loaded runs`,
    decision: $localize`:@@runs.list.decision-filter:Decision in loaded runs`,
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
    pending: $localize`:@@shared.decision-chip.pending:pending`,
    original: $localize`:@@runs.list.original:Original run`,
    rerun: $localize`:@@runs.list.rerun:Rerun`,
    reprocess: $localize`:@@runs.list.reprocess:Reprocessed`,
    apply: $localize`:@@runs.list.apply:Apply filters`,
    clear: $localize`:@@runs.list.clear:Clear filters`,
    emptyTitle: $localize`:@@runs.list.empty-title:No runs recorded`,
    emptyDescription: $localize`:@@runs.list.empty-description:Runs will appear here when a producer creates them.`,
    filteredTitle: $localize`:@@runs.list.filtered-title:No matching runs in the loaded rows`,
    filteredDescription: $localize`:@@runs.list.filtered-description:These filters apply to loaded runs, not the entire project. Load more when available, or clear filters.`,
    pagination: $localize`:@@runs.list.pagination:More runs`,
    more: $localize`:@@runs.list.more:Load more`,
    loadingMore: $localize`:@@runs.list.loading-more:Loading more runs…`,
    capped: $localize`:@@runs.list.capped:Automatic browsing paused. Load more to continue, or narrow the ref name.`,
    retryMore: $localize`:@@runs.list.retry-more:Retry loading more`,
    refRejected: $localize`:@@runs.list.ref-invalid:Ref name not applied: use 1 to 255 characters with no control characters.`,
  };

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
