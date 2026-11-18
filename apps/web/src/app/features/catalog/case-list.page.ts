import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, linkedSignal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { PageHeaderImports, SectionHeaderImports } from '../../shared/ui/page-header';
import { RequestStateComponent } from '../../shared/ui/states/request-state.component';
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
    CaseListTableComponent,
  ],
  providers: [CaseListStore],
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
        const raw = Object.fromEntries(
          ['status', 'framework', 'q', 'cursor', 'limit'].map((key) => [key, query.get(key)]),
        );
        if (projectId) this.store.init(projectId, raw);
        return raw;
      }),
    ),
  );
  protected readonly draft = linkedSignal(() => caseFiltersFromQuery(this.routeState() ?? {}));
  protected readonly copy = {
    title: $localize`:@@catalog.title:Test catalog`,
    description: $localize`:@@catalog.description:Browse stable automated test identities in this project. A missing identity is not a failed test.`,
    filters: $localize`:@@catalog.filters:Filter test identities`,
    filterHint: $localize`:@@catalog.filter-hint:Status and framework filter the server list. Search matches name or ID in this page only; it never loads additional pages.`,
    status: $localize`:@@catalog.status:Status`,
    framework: $localize`:@@catalog.framework:Framework`,
    frameworkHint: $localize`:@@catalog.framework-hint:Exact framework name, for example playwright. Leave blank for all frameworks.`,
    search: $localize`:@@catalog.search:Search name or ID in this page`,
    all: $localize`:@@catalog.all:Active and missing`,
    active: $localize`:@@catalog.active:Active`,
    missing: $localize`:@@catalog.missing:Missing`,
    archived: $localize`:@@catalog.archived:Archived`,
    apply: $localize`:@@catalog.apply:Apply filters`,
    clear: $localize`:@@catalog.clear:Clear filters`,
    loading: $localize`:@@catalog.loading:Loading test identities…`,
    emptyTitle: $localize`:@@catalog.empty-title:No test identities in this project`,
    emptyDescription: $localize`:@@catalog.empty-description:Test identities appear here after catalog ingestion. No test outcome is inferred from an empty catalog.`,
    noMatchTitle: $localize`:@@catalog.no-match-title:No matching identities in this page`,
    noMatchDescription: $localize`:@@catalog.no-match-description:Clear the filters to return to the supplied page. A local search does not inspect unloaded results.`,
    more: $localize`:@@catalog.more:More results exist. Only this response page is shown.`,
    contractNote: $localize`:@@catalog.contract-note:Owner is shown as a membership ID. This list does not supply revision, platform, level, purpose or rename history; these columns are not displayed.`,
  };
  protected readonly statusOptions = [
    { value: '', label: this.copy.all },
    { value: 'active', label: this.copy.active },
    { value: 'missing', label: this.copy.missing },
    { value: 'archived', label: this.copy.archived },
  ];
  protected readonly statusLabel = (value: unknown) =>
    this.statusOptions.find((option) => option.value === value)?.label ?? this.copy.all;

  protected apply(): void {
    // Only server filters invalidate a cursor; local search stays on the loaded page.
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
