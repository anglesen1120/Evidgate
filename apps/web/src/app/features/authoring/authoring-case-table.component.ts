import { DecimalPipe, DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  linkedSignal,
  resource,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { combineLatest, distinctUntilChanged, map } from 'rxjs';
import { InlineFeedbackComponent } from '../../shared/ui/states/inline-feedback.component';
import { RequestStateComponent } from '../../shared/ui/states/request-state.component';
import { AuthoringCaseRowsComponent } from './authoring-case-rows.component';
import {
  CASE_PAGE_SIZES,
  CASE_QUERY_KEYS,
  EMPTY_CASE_FILTERS,
  caseFiltersFromQuery,
  caseFiltersToQuery,
  isCaseUuid,
  queryTooLong,
  type AuthoringCaseFilters,
} from './authoring-case-filters';
import { CASE_TABLE_COPY } from './authoring-case-table.copy';
import { AuthoringCaseTableStore } from './authoring-case-table.store';

@Component({
  selector: 'app-authoring-case-table',
  imports: [
    DecimalPipe,
    FormsModule,
    HlmButton,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    InlineFeedbackComponent,
    RequestStateComponent,
    AuthoringCaseRowsComponent,
  ],
  providers: [AuthoringCaseTableStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './authoring-case-table.component.html',
  styleUrl: './authoring-case-table.component.css',
  host: { class: 'block min-w-0' },
})
export class AuthoringCaseTableComponent {
  protected readonly store = inject(AuthoringCaseTableStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  protected readonly copy = CASE_TABLE_COPY;
  private readonly routeState = toSignal(
    combineLatest([
      combineLatest(this.route.pathFromRoot.map((route) => route.paramMap)),
      this.route.queryParamMap,
    ]).pipe(
      map(([params, query]) => ({
        projectId:
          [...params]
            .reverse()
            .map((param) => param.get('projectId'))
            .find(Boolean) ?? null,
        raw: Object.fromEntries(CASE_QUERY_KEYS.map((key) => [key, query.get(key)])),
      })),
      distinctUntilChanged((left, right) => JSON.stringify(left) === JSON.stringify(right)),
    ),
  );
  private readonly request = resource({
    params: () => this.routeState(),
    loader: async ({ params }) => {
      if (params.projectId) await this.store.init(params.projectId, params.raw);
    },
  });
  // Cursor-only URL changes must not wipe an unapplied draft, so the source compares filter
  // values, not the whole query string. Both sides come from `caseFiltersFromQuery`, whose key
  // order is fixed, so serialising them is an exact comparison.
  private readonly routeFilters = computed(
    () => caseFiltersFromQuery(this.routeState()?.raw ?? {}),
    { equal: (left, right) => JSON.stringify(left) === JSON.stringify(right) },
  );
  protected readonly draft = linkedSignal(() => this.routeFilters());
  protected readonly searchTooLong = computed(() => queryTooLong(this.draft().q));
  protected readonly itemInvalid = computed(
    () => this.draft().itemId !== '' && !isCaseUuid(this.draft().itemId),
  );
  protected readonly busy = computed(
    () => this.store.status() === 'loading' || this.request.isLoading(),
  );
  protected readonly hasRows = computed(() => this.store.rows().length > 0);
  protected readonly emptyTitle = computed(() =>
    this.store.filtered()
      ? this.copy.noMatch
      : this.routeState()?.raw['node_id']
        ? this.copy.emptyCategory
        : this.copy.emptyProject,
  );
  protected readonly pageSizes = computed(() =>
    [...new Set<number>([...CASE_PAGE_SIZES, this.draft().limit])].sort(
      (left, right) => left - right,
    ),
  );
  protected readonly pageLabel = (value: unknown) => String(value);
  protected readonly unavailableFilters = [
    { id: 'source', label: this.copy.source, help: this.copy.sourceHelp },
    { id: 'owner', label: this.copy.owner, help: this.copy.ownerHelp },
  ];
  protected readonly filterFields = [
    {
      key: 'priority',
      label: this.copy.priority,
      options: ['', 'critical', 'high', 'medium', 'low'].map((value) => ({
        value,
        label: value ? this.copy[value as 'critical' | 'high' | 'medium' | 'low'] : this.copy.all,
      })),
    },
    {
      key: 'state',
      label: this.copy.state,
      options: ['', 'draft', 'approved', 'retired'].map((value) => ({
        value,
        label: value ? this.copy[value as 'draft' | 'approved' | 'retired'] : this.copy.all,
      })),
    },
    {
      key: 'covered',
      label: this.copy.coverage,
      options: [
        { value: '', label: this.copy.all },
        { value: 'true', label: this.copy.covered },
        { value: 'false', label: this.copy.uncovered },
      ],
    },
  ] as const;
  protected readonly choiceLabel = (value: unknown) =>
    this.filterFields.flatMap((field) => field.options).find((option) => option.value === value)
      ?.label ?? this.copy.all;
  private readonly rows = viewChild(AuthoringCaseRowsComponent);
  private readonly loadedCount = viewChild<ElementRef<HTMLElement>>('loadedCount');

  protected update(patch: Partial<AuthoringCaseFilters>): void {
    this.draft.update((draft) => ({ ...draft, ...patch }));
  }
  protected choose(key: 'priority' | 'state' | 'covered', value: unknown): void {
    this.draft.set(
      caseFiltersFromQuery({
        ...caseFiltersToQuery(this.draft()),
        [key]: typeof value === 'string' ? value : '',
      }),
    );
  }
  protected apply(): void {
    if (this.searchTooLong() || this.itemInvalid()) return;
    this.navigate({
      item_id: null,
      state: null,
      priority: null,
      covered: null,
      q: null,
      cursor: null,
      ...caseFiltersToQuery(this.draft()),
    });
  }
  protected clear(): void {
    this.draft.set({ ...EMPTY_CASE_FILTERS, limit: this.draft().limit });
    this.apply();
  }
  /** The refused route value is dropped from the URL itself; the draft cannot carry it out. */
  protected clearInvalidLink(): void {
    if (this.store.invalidLinkField() === 'node') {
      this.navigate({ node_id: null, cursor: null });
      return;
    }
    this.draft.update((draft) => ({ ...draft, itemId: '' }));
    this.navigate({ item_id: null, cursor: null });
  }
  protected setPageSize(value: unknown): void {
    const limit = Number(value);
    if (!Number.isInteger(limit) || limit < 1 || limit > 200) return;
    this.navigate({ limit, cursor: null });
  }
  protected async loadMore(event: Event): Promise<void> {
    const source = event.currentTarget;
    const owned = source instanceof HTMLElement && this.document.activeElement === source;
    const before = new Set(this.store.rows().map((row) => row.case_id));
    const scope = this.routeState();
    const scopeKey = this.store.scopeKey();
    const cursor = await this.store.loadMore();
    if (scope !== this.routeState()) return;
    // A pager that could not continue removes the control the visitor was on. When that
    // interaction still owns focus, hand it to the surviving loaded count rather than leaving it
    // on a detached node; a pager that still has a cursor stays where the visitor put it.
    if (!cursor) {
      if (this.store.nextCursor() === null && owned) this.focusLoadedCount(source, scopeKey);
      return;
    }
    // A page step is a position in the same result, not a new history entry: Back must return to
    // the filters that produced the walk instead of stepping through every page.
    this.navigate({ cursor }, true);
    const first = this.store.rows().find((row) => !before.has(row.case_id));
    if (!owned) return;
    // An accepted page can also be the last one while adding nothing (an empty or duplicate-only
    // tail): the pager is gone, so the same surviving target takes the focus.
    if (!first) {
      if (this.store.nextCursor() === null) this.focusLoadedCount(source, scopeKey);
      return;
    }
    afterNextRender(
      () => {
        if (
          scopeKey === this.store.scopeKey() &&
          (this.document.activeElement === source ||
            this.document.activeElement === this.document.body)
        )
          this.rows()?.focusRow(first.case_id);
      },
      { injector: this.injector },
    );
  }
  private focusLoadedCount(source: EventTarget | null, scopeKey: string): void {
    afterNextRender(
      () => {
        if (scopeKey !== this.store.scopeKey()) return;
        const active = this.document.activeElement;
        if (active !== source && active !== this.document.body) return;
        this.loadedCount()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }
  protected refresh(): void {
    // The store restarts at the first page, so the URL cursor must not outlive the refresh.
    if (this.routeState()?.raw['cursor']) this.navigate({ cursor: null }, true);
    void this.store.reload();
  }
  private navigate(queryParams: Record<string, string | number | null>, replaceUrl = false): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams,
      replaceUrl,
    });
  }
}
