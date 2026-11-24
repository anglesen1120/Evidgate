import { DecimalPipe, formatNumber, Location } from '@angular/common';
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
  LOCALE_ID,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, NavigationStart, Router } from '@angular/router';
import { combineLatest, distinctUntilChanged, filter, map, tap } from 'rxjs';
import type { CaseDto } from '@evidgate/sdk';
import type { CatalogSelection } from './case-record-dialog.component';
import { CATALOG_RECORD } from './case-record.copy';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { SessionStore } from '../../core/session/session.store';
import { PageHeaderImports, SectionHeaderImports } from '../../shared/ui/page-header';
import { RequestStateComponent } from '../../shared/ui/states/request-state.component';
import { InlineFeedbackComponent } from '../../shared/ui/states/inline-feedback.component';
import { DataTablePagerComponent } from '../../shared/ui/data-table/data-table-pager.component';
import { RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { ShellIconComponent } from '../../shell/layout/shell-icon.component';
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
    HlmCard,
    HlmCardTitle,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    PageHeaderImports,
    SectionHeaderImports,
    RequestStateComponent,
    InlineFeedbackComponent,
    DataTablePagerComponent,
    CaseListTableComponent,
    ShellIconComponent,
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
  private readonly session = inject(SessionStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly locale = inject(LOCALE_ID);
  private readonly cap = inject(RETAINED_ROWS_CAP);
  private readonly location = inject(Location);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly recordNavigation = {};
  protected readonly recordCopy = CATALOG_RECORD;
  private readonly query = toSignal(this.route.queryParamMap);
  protected readonly selected = linkedSignal<CatalogSelection | null>(() => {
    const query = this.query();
    const record = query?.get('record');
    const mode = query?.get('mode');
    if (!record?.startsWith('case:') || (mode !== 'view' && mode !== 'edit')) return null;
    const id = record.slice(5);
    return id ? { id, mode } : null;
  });
  protected readonly readableSelection = computed(() =>
    this.session.has('case:read') ? this.selected() : null,
  );
  protected readonly recordUnavailable = computed(() => {
    const record = this.selected();
    return (
      !!record &&
      this.store.status() === 'ready' &&
      !this.store.visibleRows().some((row) => row.id === record.id)
    );
  });
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
        return {
          orgId,
          projectId,
          status: query.get('status'),
          framework: query.get('framework'),
          q: query.get('q'),
          cursor: query.get('cursor'),
          limit: query.get('limit'),
        };
      }),
      distinctUntilChanged(
        (a, b) =>
          a.orgId === b.orgId &&
          a.projectId === b.projectId &&
          a.status === b.status &&
          a.framework === b.framework &&
          a.q === b.q &&
          a.cursor === b.cursor &&
          a.limit === b.limit,
      ),
      tap((state) => {
        if (state.projectId) this.store.init(state.projectId, state);
      }),
    ),
  );
  protected readonly draft = linkedSignal(() => caseFiltersFromQuery(this.routeState() ?? {}));
  protected readonly copy = {
    title: $localize`:@@catalog.title:Test catalog`,
    description: $localize`:@@catalog.description:Browse stable automated test identities in this project. A missing identity is not a failed test.`,
    results: $localize`:@@catalog.results-oldest-first:Catalog results (oldest first)`,
    filters: $localize`:@@catalog.filters:Filter test identities`,
    filterHint: $localize`:@@catalog.page-filter-hint:Status and framework filter the server list. Search matches name or ID on the current page only.`,
    status: $localize`:@@catalog.status:Status`,
    framework: $localize`:@@catalog.framework:Framework`,
    frameworkHint: $localize`:@@catalog.framework-hint:Exact framework name, for example playwright. Leave blank for all frameworks.`,
    search: $localize`:@@catalog.page-search:Search name or ID on this page`,
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
    noMatchDescription: $localize`:@@catalog.page-no-match-description:Clear the filters or browse another page. Local search only checks the current page.`,
    contractNote: $localize`:@@catalog.available-fields-note:This list does not supply revision, platform, level, purpose or rename history; these columns are not displayed.`,
    browseCapped: $localize`:@@catalog.browse-capped:Case list is at its load limit`,
    browseCappedHelp: $localize`:@@catalog.browse-capped-help:Narrow the status or framework filters to reach cases beyond the ${formatNumber(this.cap, this.locale)}:cap: loaded here. Search only checks loaded results.`,
    loadMoreFailed: $localize`:@@catalog.page-load-failed:The requested page could not be loaded. The displayed page is unchanged.`,
    retryMore: $localize`:@@catalog.retry-page:Retry page`,
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
  ] as const;
  protected readonly loadedCounts = computed(() => {
    const counts = { active: 0, missing: 0, archived: 0 };
    for (const row of this.store.rows()) counts[row.status]++;
    const status = this.store.filters().status;
    // Without an explicit status the catalog API returns active and missing, never archived.
    return {
      '': !status ? counts.active + counts.missing : null,
      active: !status || status === 'active' ? counts.active : null,
      missing: !status || status === 'missing' ? counts.missing : null,
      archived: status === 'archived' ? counts.archived : null,
    };
  });
  protected readonly showLoadedSummary = computed(
    () =>
      this.store.status() === 'ready' ||
      (this.store.status() === 'loading' && this.store.rows().length > 0),
  );
  protected readonly subtitle = computed(() =>
    this.store.status() === 'loading' && this.showLoadedSummary()
      ? this.copy.results
      : this.store.status() !== 'ready'
        ? this.copy.description
        : this.copy.results +
          ' · ' +
          $localize`:@@catalog.case-view.loaded-count:Loaded identities: ${formatNumber(this.store.rows().length, this.locale)}:count:`,
  );
  protected quickStatus(status: CaseListFilters['status']): void {
    const applied = this.store.filters();
    this.apply({ ...applied, status: applied.status === status ? '' : status });
  }
  protected readonly statusLabel = (value: unknown) =>
    this.statusOptions.find((option) => option.value === value)?.label ?? this.copy.all;
  private readonly table = viewChild(CaseListTableComponent);
  /** The cap notice is inserted with its message; the mounted summary region speaks it. */
  protected readonly capAnnouncement = computed(() =>
    this.store.browseCapped() ? `${this.copy.browseCapped}. ${this.copy.browseCappedHelp}` : '',
  );
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
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationStart),
        takeUntilDestroyed(),
      )
      .subscribe((event) => {
        const navigation = this.router.currentNavigation();
        const table = this.table();
        if (
          !navigation ||
          !this.readableSelection() ||
          !table ||
          navigation.extras.info === this.recordNavigation
        )
          return;
        if (this.changesSecurityScope(event.url)) {
          this.selected.set(null);
          return;
        }
        let inspecting = true;
        const allowed = table.requestLeave(() => {
          if (inspecting) return;
          if (event.navigationTrigger === 'popstate') this.location.back();
          else {
            table.restoreOpener();
            void this.router.navigateByUrl(event.url, { info: this.recordNavigation });
          }
        });
        inspecting = false;
        if (!allowed) {
          const targetState = this.location.getState();
          navigation.abort();
          if (event.navigationTrigger === 'popstate') {
            // The router's default cancellation replaces the destination history entry.
            // Keep that exact destination behind the still-open editor for a confirmed Back.
            this.location.replaceState(event.url, '', targetState);
            this.location.go(this.router.url, '', this.recordState(true));
          }
        } else {
          table.restoreOpener();
        }
      });
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

  protected retryPage(): void {
    const host = this.host.nativeElement;
    const document = host.ownerDocument;
    const origin = document.activeElement;
    const restore = !!origin && !!host.querySelector('[data-catalog-page-error]')?.contains(origin);
    void this.store.retryPage();
    if (!restore) return;
    afterNextRender(
      () => {
        if (document.activeElement !== origin && document.activeElement !== document.body) return;
        const heading = host.querySelector<HTMLElement>('#catalog-results-heading');
        if (heading) {
          heading.tabIndex = -1;
          heading.focus();
        }
      },
      { injector: this.injector },
    );
  }

  protected setPageSize(size: number): void {
    this.store.setPageSize(size);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams: { limit: size, cursor: null },
    });
  }

  protected readonly assignOwner = async (row: CaseDto, membershipId: string): Promise<boolean> => {
    if (
      !this.canReadMembers() ||
      !this.viewport.allowsMutation() ||
      !this.store.canAssign() ||
      this.members.members().get(membershipId)?.status !== 'active'
    )
      return false;
    const current = this.store.visibleRows().find((r) => r.id === row.id);
    if (
      !current ||
      current.version !== row.version ||
      current.org_id !== row.org_id ||
      current.project_id !== row.project_id ||
      this.store.ownerPending(row.id)
    )
      return false;
    await this.store.assignOwner(row.id, membershipId);
    const updated = this.store.visibleRows().find((r) => r.id === row.id);
    return !this.store.ownerFailure(row.id) && updated?.owner_membership_id === membershipId;
  };
  protected readonly reloadOwner = (id: string) => this.store.reloadOwner(id);

  protected setRecord(next: CatalogSelection | null): void {
    const previous = this.selected();
    this.selected.set(next);
    const state: unknown = this.location.getState();
    const fromList =
      !!state &&
      typeof state === 'object' &&
      'catalogDialogFromList' in state &&
      state.catalogDialogFromList === true;
    if (!next && fromList) {
      this.location.back();
      return;
    }
    const target = this.router.parseUrl(this.router.url);
    target.queryParams = { ...target.queryParams };
    if (next) {
      target.queryParams['record'] = 'case:' + next.id;
      target.queryParams['mode'] = next.mode;
    } else {
      delete target.queryParams['record'];
      delete target.queryParams['mode'];
    }
    void this.router.navigateByUrl(target, {
      replaceUrl: !next || previous !== null,
      state: this.recordState(!!next && (previous === null || fromList)),
      info: this.recordNavigation,
    });
  }
  private recordState(fromList: boolean) {
    return { catalogDialogFromList: fromList };
  }
  private changesSecurityScope(url: string): boolean {
    const path =
      this.router.parseUrl(url).root.children['primary']?.segments.map((segment) => segment.path) ??
      [];
    if (path.includes('sign-in') || path.includes('logout') || path.includes('forbidden'))
      return true;
    const org = path.indexOf('orgs');
    const project = path.indexOf('projects');
    const current = this.routeState();
    return (
      (org >= 0 && path[org + 1] !== current?.orgId) ||
      (project >= 0 && path[project + 1] !== current?.projectId)
    );
  }

  protected apply(draft: CaseListFilters = this.draft()): void {
    // Search is local to the displayed server page; the store resets it to page one.
    const filters = caseFiltersFromQuery(caseFiltersToQuery(draft));
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
