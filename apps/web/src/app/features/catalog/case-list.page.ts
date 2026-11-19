import { DecimalPipe, DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  Injector,
  linkedSignal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { SessionStore } from '../../core/session/session.store';
import { PageHeaderImports, SectionHeaderImports } from '../../shared/ui/page-header';
import { RequestStateComponent } from '../../shared/ui/states/request-state.component';
import { InlineFeedbackComponent } from '../../shared/ui/states/inline-feedback.component';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { CatalogMembersReader } from './catalog-members.reader';
import { CaseListTableComponent } from './case-list-table.component';
import {
  CaseListStore,
  caseFiltersFromQuery,
  caseFiltersToQuery,
  EMPTY_CASE_FILTERS,
  type CaseListFilters,
} from './case-list.store';

@Component({
  selector: 'app-case-list-page',
  imports: [
    DecimalPipe,
    FormsModule,
    HlmButton,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    PageHeaderImports,
    SectionHeaderImports,
    RequestStateComponent,
    InlineFeedbackComponent,
    CaseListTableComponent,
  ],
  providers: [CaseListStore, CatalogMembersReader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './case-list.page.html',
  host: {
    class: 'block min-w-0 text-sm text-foreground',
    '[attr.data-catalog-status]': 'store.status()',
  },
})
export class CaseListPage {
  protected readonly store = inject(CaseListStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly members = inject(CatalogMembersReader);
  private readonly viewport = inject(NarrowMutationPolicy);
  private readonly injector = inject(Injector);
  private readonly session = inject(SessionStore);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly routeState = toSignal(
    combineLatest([
      combineLatest(this.route.pathFromRoot.map((route) => route.paramMap)),
      this.route.queryParamMap,
    ]).pipe(
      map(([params, query]) => {
        const projectId = [...params]
          .reverse()
          .map((param) => param.get('projectId'))
          .find(Boolean);
        const orgId = [...params]
          .reverse()
          .map((param) => param.get('orgId'))
          .find(Boolean);
        const raw = Object.fromEntries(
          ['status', 'framework', 'q', 'cursor', 'limit'].map((key) => [key, query.get(key)]),
        );
        if (projectId) this.store.init(projectId, raw);
        return { ...raw, orgId };
      }),
    ),
  );
  protected readonly draft = linkedSignal(() => caseFiltersFromQuery(this.routeState() ?? {}));
  protected readonly copy = {
    title: $localize`:@@catalog.title:Test catalog`,
    description: $localize`:@@catalog.description:Browse stable automated test identities in this project. A missing identity is not a failed test.`,
    filters: $localize`:@@catalog.filters:Filter test identities`,
    filterHint: $localize`:@@catalog.loaded-filter-hint:Status and framework filter the server list. Search matches name or ID in loaded results only; it never loads more results.`,
    status: $localize`:@@catalog.status:Status`,
    framework: $localize`:@@catalog.framework:Framework`,
    frameworkHint: $localize`:@@catalog.framework-hint:Exact framework name, for example playwright. Leave blank for all frameworks.`,
    search: $localize`:@@catalog.loaded-search:Search name or ID in loaded results`,
    all: $localize`:@@catalog.all:Active and missing`,
    active: $localize`:@@catalog.active:Active`,
    missing: $localize`:@@catalog.missing:Missing`,
    archived: $localize`:@@catalog.archived:Archived`,
    apply: $localize`:@@catalog.apply:Apply filters`,
    clear: $localize`:@@catalog.clear:Clear filters`,
    loading: $localize`:@@catalog.loading:Loading test identities…`,
    emptyTitle: $localize`:@@catalog.empty-title:No test identities in this project`,
    emptyDescription: $localize`:@@catalog.empty-description:Test identities appear here after catalog ingestion. No test outcome is inferred from an empty catalog.`,
    noMatchTitle: $localize`:@@catalog.loaded-no-match-title:No matching identities in loaded results`,
    noMatchDescription: $localize`:@@catalog.loaded-no-match-description:Clear the filters to return to loaded results, or load more identities below. Local search does not inspect unloaded results.`,
    contractNote: $localize`:@@catalog.available-fields-note:This list does not supply revision, platform, level, purpose or rename history; these columns are not displayed.`,
    browseCapped: $localize`:@@catalog.browse-capped:Case list is at its load limit`,
    browseCappedHelp: $localize`:@@catalog.browse-capped-help:Narrow the status or framework filters to reach cases beyond the 10,000 loaded here. Search only checks loaded results.`,
    pagination: $localize`:@@catalog.pagination:Catalog pagination`,
    pageSize: $localize`:@@catalog.page-size:Page size`,
    loadMore: $localize`:@@catalog.load-more:Load more`,
    loadingMore: $localize`:@@catalog.loading-more:Loading more identities…`,
    loadMoreFailed: $localize`:@@catalog.load-more-failed:More identities could not be loaded. Loaded results are unchanged.`,
    retryMore: $localize`:@@catalog.retry-more:Retry loading more`,
    ownersLoading: $localize`:@@catalog.owners-loading:Loading owner choices…`,
    ownersFailed: $localize`:@@catalog.owners-failed:Owner choices could not be loaded. Existing ownership is unchanged.`,
    retryOwners: $localize`:@@catalog.retry-owners:Retry owner choices`,
    ownersEmpty: $localize`:@@catalog.owners-empty:No active members are available to assign. Existing ownership is unchanged.`,
    permissionReason: $localize`:@@catalog.owner-permission-reason:case:write is required to assign an owner.`,
    narrowReason: $localize`:@@catalog.owner-narrow-reason:Assign owners on a screen at least 768 pixels wide. Owner details remain readable here.`,
    ownersHint: $localize`:@@catalog.owner-choices-hint:Choose an active member by email. Clearing an owner is not supported.`,
  };
  protected readonly statusOptions = [
    { value: '', label: this.copy.all },
    { value: 'active', label: this.copy.active },
    { value: 'missing', label: this.copy.missing },
    { value: 'archived', label: this.copy.archived },
  ];
  protected readonly statusLabel = (value: unknown) =>
    this.statusOptions.find((option) => option.value === value)?.label ?? this.copy.all;
  private readonly table = viewChild(CaseListTableComponent);
  private readonly loadedSummary = viewChild<ElementRef<HTMLElement>>('loadedSummary');
  protected readonly pageSizes = [25, 50, 100, 200] as const;
  protected readonly pageSizeLabel = (value: unknown) => String(value);
  protected readonly canReadMembers = computed(() => this.session.has('membership:read'));
  protected readonly canAssign = computed(
    () => this.store.canAssign() && this.canReadMembers() && this.viewport.canMutate(),
  );
  protected readonly ownerHelp = computed(() => {
    if (!this.canReadMembers()) return this.copy.ownersEmpty;
    if (!this.store.canAssign()) return this.copy.permissionReason;
    if (!this.viewport.canMutate()) return this.copy.narrowReason;
    if (this.members.status() === 'loading') return this.copy.ownersLoading;
    if (this.members.status() === 'error') return this.copy.ownersFailed;
    for (const member of this.members.members().values()) {
      if (member.status === 'active') return this.copy.ownersHint;
    }
    return this.copy.ownersEmpty;
  });

  constructor() {
    effect(() => {
      const orgId = this.routeState()?.orgId;
      const permitted = this.canReadMembers();
      untracked(() => {
        if (orgId && permitted) this.members.init(orgId);
        else this.members.clear();
      });
    });
    this.destroyRef.onDestroy(() => this.members.clear());
  }

  protected setPageSize(size: number): void {
    this.store.setPageSize(size);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams: { limit: size, cursor: null },
    });
  }

  protected async loadMore(event?: Event): Promise<void> {
    const source = event?.currentTarget ?? this.document.activeElement;
    const initiated =
      source instanceof HTMLElement &&
      source.matches('[data-catalog-load-more], [data-catalog-page-error] button');
    const ownsFocus = () =>
      initiated &&
      (this.document.activeElement === source ||
        this.document.activeElement === this.document.body);
    const previous = new Set(this.store.rows().map((row) => row.id));
    const scope = this.routeState();
    const appended = await this.store.loadMore();
    if (!appended || scope !== this.routeState() || !ownsFocus()) return;
    const first = this.store.visibleRows().find((row) => !previous.has(row.id));
    afterNextRender(
      () => {
        if (!ownsFocus() || scope !== this.routeState()) return;
        if (first) this.table()?.focusRow(first.id, ownsFocus);
        else this.loadedSummary()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }

  protected async assignOwner(change: { caseId: string; membershipId: string }): Promise<void> {
    if (!this.canReadMembers() || !this.viewport.allowsMutation() || !this.store.canAssign())
      return;
    await this.store.assignOwner(change.caseId, change.membershipId);
  }

  protected apply(): void {
    // Only server filters invalidate a cursor; local search stays on loaded results.
    const filters = caseFiltersFromQuery(caseFiltersToQuery(this.draft()));
    const previous = this.store.filters();
    const serverChanged =
      filters.status !== previous.status || filters.framework !== previous.framework;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams: {
        status: null,
        framework: null,
        q: null,
        ...(serverChanged ? { cursor: null } : {}),
        ...caseFiltersToQuery(filters),
      },
    });
  }

  protected updateDraft(patch: Partial<CaseListFilters>): void {
    this.draft.update((draft) => ({ ...draft, ...patch }));
  }

  protected clear(): void {
    this.draft.set({ ...EMPTY_CASE_FILTERS });
    this.apply();
  }
}
