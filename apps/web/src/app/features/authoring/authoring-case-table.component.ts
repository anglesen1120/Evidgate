import { FieldHintComponent } from '../../shared/ui/forms/field-hint.component';
import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  inject,
  linkedSignal,
  LOCALE_ID,
  resource,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { combineLatest, distinctUntilChanged, map } from 'rxjs';
import { RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { DataTablePagerComponent } from '../../shared/ui/data-table/data-table-pager.component';
import { RowActionComponent } from '../../shared/ui/data-table/row-action.component';
import { InlineFeedbackComponent } from '../../shared/ui/states/inline-feedback.component';
import { LiveStatusComponent } from '../../shared/ui/states/live-status.component';
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
import { CASE_TABLE_COPY, caseTableBrowseCappedHelp } from './authoring-case-table.copy';
import { AuthoringCaseTableStore } from './authoring-case-table.store';

@Component({
  selector: 'app-authoring-case-table',
  imports: [
    FieldHintComponent,
    DecimalPipe,
    DataTablePagerComponent,
    RowActionComponent,
    FormsModule,
    HlmButton,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    InlineFeedbackComponent,
    LiveStatusComponent,
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
  private readonly locale = inject(LOCALE_ID);
  private readonly cap = inject(RETAINED_ROWS_CAP);
  protected readonly copy = {
    ...CASE_TABLE_COPY,
    browseCappedHelp: caseTableBrowseCappedHelp(this.cap, this.locale),
  };
  private readonly routeState = toSignal(
    combineLatest([
      combineLatest(this.route.pathFromRoot.map((route) => route.paramMap)),
      this.route.queryParamMap,
    ]).pipe(
      map(([params, query]) => ({
        orgId: params.map((param) => param.get('orgId')).find(Boolean) ?? null,
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
  protected readonly editorScope = computed(() => {
    const scope = this.routeState();
    return scope?.orgId && scope.projectId
      ? ['/orgs', scope.orgId, 'projects', scope.projectId, 'authoring', 'cases']
      : null;
  });
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
  protected readonly refreshUnavailable = computed(
    () => this.busy() || this.store.loadingMore() || !this.store.retryReady(),
  );
  readonly projectEmpty = input(false);
  readonly loadedInventory = computed(() =>
    this.store.status() === 'ready' && !this.busy() && !this.store.error() && !this.store.filtered()
      ? {
          nodeId: this.store.query().node_id ?? null,
          count: this.store.rows().length,
          partial:
            !!this.store.nextCursor() || !!this.store.routeCursor() || this.store.browseCapped(),
        }
      : null,
  );
  readonly emptyProject = computed(() => {
    const loaded = this.loadedInventory();
    return !!loaded && !loaded.nodeId && loaded.count === 0 && !loaded.partial;
  });
  protected readonly hasRows = computed(() => this.store.rows().length > 0);
  protected readonly loadedStates = computed(() => {
    const counts = { draft: 0, approved: 0, retired: 0 };
    for (const row of this.store.rows()) counts[row.state]++;
    const state = this.store.query().state;
    return {
      draft: !state || state === 'draft' ? counts.draft : null,
      approved: !state || state === 'approved' ? counts.approved : null,
      retired: !state || state === 'retired' ? counts.retired : null,
    };
  });
  protected readonly states = ['draft', 'approved', 'retired'] as const;
  protected quickState(state: AuthoringCaseFilters['state']): void {
    // Only the status changes: unsubmitted search or filter edits stay in the draft.
    this.navigate({
      item_id: null,
      state: null,
      priority: null,
      covered: null,
      q: null,
      cursor: null,
      ...caseFiltersToQuery({ ...this.routeFilters(), state }),
    });
  }
  protected readonly emptyTitle = computed(() =>
    this.store.page() > 1
      ? this.copy.emptyPage
      : this.store.filtered()
        ? this.copy.noMatch
        : this.routeState()?.raw['node_id']
          ? this.copy.emptyCategory
          : this.copy.emptyProject,
  );
  /** The cap notice is inserted with its message, so it announces through this mounted region. */
  protected readonly capAnnouncement = computed(() =>
    this.store.browseCapped() ? `${this.copy.browseCapped}. ${this.copy.browseCappedHelp}` : '',
  );
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
    if (!CASE_PAGE_SIZES.includes(limit)) return;
    this.navigate({ limit, cursor: null });
  }
  protected async movePage(forward: boolean | null): Promise<void> {
    const scope = this.routeState();
    const page = this.store.page();
    const cursor = this.store.routeCursor();
    if (forward === null) await this.store.retry();
    else if (forward) await this.store.loadMore();
    else await this.store.previous();
    if (
      scope !== this.routeState() ||
      (page === this.store.page() && cursor === this.store.routeCursor())
    )
      return;
    this.navigate({ cursor: this.store.routeCursor() }, true);
  }
  protected async refresh(): Promise<void> {
    if (this.refreshUnavailable()) return;
    const scope = this.routeState();
    await this.store.reload();
    if (
      scope !== this.routeState() ||
      this.store.error() ||
      this.store.page() !== 1 ||
      this.store.routeCursor() !== null
    )
      return;
    // Route-driven init sees the accepted cursor, so it preserves the refreshed rows and history.
    if (scope?.raw['cursor']) this.navigate({ cursor: null }, true);
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
