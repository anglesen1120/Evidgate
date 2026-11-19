import { DecimalPipe, DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
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
  queryTooLong,
  type AuthoringCaseFilters,
} from './authoring-case-filters';
import { CASE_TABLE_COPY } from './authoring-case-table.copy';
import { AuthoringCaseTableStore } from './authoring-case-table.store';

const CHECKLIST_UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

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
  protected readonly draft = linkedSignal(() => caseFiltersFromQuery(this.routeState()?.raw ?? {}));
  protected readonly searchTooLong = computed(() => queryTooLong(this.draft().q));
  protected readonly itemInvalid = computed(
    () => this.draft().itemId !== '' && !CHECKLIST_UUID.test(this.draft().itemId),
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
    if (!cursor || scope !== this.routeState()) return;
    this.navigate({ cursor });
    const first = this.store.rows().find((row) => !before.has(row.case_id));
    if (!first || !owned) return;
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
  private navigate(queryParams: Record<string, string | number | null>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams,
    });
  }
}
