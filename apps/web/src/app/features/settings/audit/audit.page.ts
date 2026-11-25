import { FieldHintComponent } from '../../../shared/ui/forms/field-hint.component';
import { FilterToolbarComponent } from '../../../shared/ui/forms/filter-toolbar.component';
import { toSignal } from '@angular/core/rxjs-interop';
import { formatNumber } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  LOCALE_ID,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, type ParamMap } from '@angular/router';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { SessionStore } from '../../../core/session/session.store';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTablePagerComponent } from '../../../shared/ui/data-table/data-table-pager.component';
import { SectionHeaderComponent } from '../../../shared/ui/page-header/section-header.component';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { AUDIT_ACTOR_TYPE_LABELS } from './actor-type-labels';
import { AuditExportComponent } from './audit-export.component';
import { AuditTableComponent } from './audit-table.component';
import {
  AUDIT_EXPORT_CAP,
  AuditStore,
  EMPTY_AUDIT_FILTERS,
  type AuditFilters,
} from './audit.store';
import { supportAccessOf } from './support-access';

@Component({
  selector: 'app-audit-page',
  imports: [
    FilterToolbarComponent,
    FieldHintComponent,
    FormsModule,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmIcon,
    ShellIconComponent,
    StatusPillComponent,
    DataTablePagerComponent,
    SectionHeaderComponent,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    RequestStateComponent,
    AuditTableComponent,
    AuditExportComponent,
  ],
  providers: [AuditStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './audit.page.html',
  host: {
    class:
      '@container grid min-w-0 grid-cols-[minmax(0,1fr)] gap-4 wrap-anywhere text-sm text-foreground [&_p]:m-0 [&_code]:font-mono',
    '[attr.data-audit-phase]': 'store.phase()',
  },
})
export class AuditPage {
  protected readonly store = inject(AuditStore);

  protected readonly session = inject(SessionStore);

  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly locale = inject(LOCALE_ID);
  private readonly query = toSignal(this.route.queryParamMap, { requireSync: true });
  private readonly toolbar = viewChild.required(FilterToolbarComponent);

  /**
   * The `audit` sidebar entry mounts this page directly under the app shell, so the page owns the
   * one `h1`; as a Settings tab the shell's `h1` ("Settings") is the page heading and this is an
   * `h2`. The route says which (`audit.routes.ts`), never the page guessing from its position.
   */
  protected readonly ownsPageHeading = this.route.snapshot.data['pageHeadingLevel'] === 1;

  protected readonly secondaryCount = computed(() => {
    const { actorType, objectType, from, to } = this.store.filters();
    return Number(!!actorType) + Number(!!objectType) + Number(!!from) + Number(!!to);
  });

  protected readonly actorLabel = (value: unknown): string => {
    if (typeof value !== 'string' || !value) return this.copy.anyActor;
    return Object.hasOwn(AUDIT_ACTOR_TYPE_LABELS, value)
      ? AUDIT_ACTOR_TYPE_LABELS[value as keyof typeof AUDIT_ACTOR_TYPE_LABELS]
      : value;
  };
  protected readonly pageHeadingSize = 'page' as const;
  protected readonly subsectionHeadingSize = 'subsection' as const;
  protected readonly draft = signal<AuditFilters>({ ...EMPTY_AUDIT_FILTERS });
  protected readonly invalidRange = signal(false);
  protected readonly unappliedFilters = computed(() => {
    const draft = this.draft();
    const applied = this.store.filters();
    return (
      draft.actorType !== applied.actorType ||
      draft.action !== applied.action ||
      draft.objectType !== applied.objectType ||
      this.toIso(draft.from, applied.from) !== applied.from ||
      this.toIso(draft.to, applied.to) !== applied.to
    );
  });
  protected readonly filtered = computed(() =>
    Object.values(this.store.filters()).some((value) => value !== ''),
  );
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() => {
    const phase = this.store.phase();
    return !this.session.currentOrg() || phase === 'idle' ? 'loading' : phase;
  });
  protected readonly retryReady = signal(true);
  protected readonly pagingAlert = this.store.pagingError;
  private readonly heading = viewChild<ElementRef<HTMLHeadingElement>>('heading');
  private readonly table = viewChild(AuditTableComponent);
  protected readonly copy = {
    title: $localize`:@@settings.audit.title:Audit log`,
    loading: $localize`:@@settings.audit.loading:Loading audit activity…`,
    description: $localize`:@@settings.audit.description:Trace administrative changes to an actor, request and immutable audit record.`,
    newestFirst: $localize`:@@settings.audit.newest-first:Newest first`,
    actorType: $localize`:@@settings.audit.actor-type:Actor type`,
    anyActor: $localize`:@@settings.audit.any-actor:Any actor`,
    user: AUDIT_ACTOR_TYPE_LABELS.user,
    ciProducer: AUDIT_ACTOR_TYPE_LABELS.ci_producer,
    agent: AUDIT_ACTOR_TYPE_LABELS.agent,
    operator: AUDIT_ACTOR_TYPE_LABELS.platform_operator,
    system: AUDIT_ACTOR_TYPE_LABELS.system,
    action: $localize`:@@settings.audit.filter-action:Action`,
    objectType: $localize`:@@settings.audit.object-type:Object type`,
    actionHint: $localize`:@@settings.audit.action-hint:Use the exact action code, for example auth.login or run.create.`,
    objectHint: $localize`:@@settings.audit.object-hint:Use the exact object type, for example run or policy_revision.`,
    from: $localize`:@@settings.audit.from:From (inclusive)`,
    to: $localize`:@@settings.audit.to:To (exclusive)`,
    dateHint: $localize`:@@settings.audit.date-hint:Enter times in your local time zone; results and exports use UTC.`,
    filters: $localize`:@@settings.audit.filter-toggle:Filters`,
    apply: $localize`:@@settings.audit.apply:Apply filters`,
    clear: $localize`:@@settings.audit.clear:Clear filters`,
    rangeError: $localize`:@@settings.audit.range-error:Enter a valid time range with From no later than To.`,
    emptyTitle: $localize`:@@settings.audit.empty-title:No activity recorded`,
    emptyDescription: $localize`:@@settings.audit.empty-description:Audit events will appear here when activity is recorded.`,
    noMatchTitle: $localize`:@@settings.audit.no-match-title:No results match these filters`,
    noMatchDescription: $localize`:@@settings.audit.no-match-description:Clear or narrow the filters to review audit activity.`,
    loadingPage: $localize`:@@settings.audit.loading-page:Loading activity page…`,
    retry: $localize`:@@settings.audit.page-retry:Retry loading page`,
  };
  protected readonly activity = $localize`:@@settings.audit-activity:Administrative activity`;
  protected readonly summary = computed(() => {
    const rows = this.store.rows();
    const supportCount = rows.reduce((n, row) => n + Number(supportAccessOf(row) !== null), 0);
    return [
      {
        label: $localize`:@@settings.audit.recorded-activity:Recorded activity`,
        caption: $localize`:@@settings.audit-loaded-records:Loaded records`,
        value: formatNumber(rows.length, this.locale),
        icon: 'clock',
      },
      {
        label: $localize`:@@settings.audit.support:Support access`,
        caption: $localize`:@@settings.audit-loaded-support:Loaded support records`,
        value: formatNumber(supportCount, this.locale),
        icon: 'people',
      },
      {
        label: $localize`:@@settings.audit.export-bound:Export bound`,
        caption: $localize`:@@settings.audit-export-limit:Maximum rows per export`,
        value: formatNumber(AUDIT_EXPORT_CAP, this.locale),
        icon: 'folder',
      },
      {
        label: $localize`:@@settings.audit-record-scope:Record scope`,
        caption: this.session.currentOrg()?.name,
        value: $localize`:@@settings.audit.export-organization:Organization`,
        icon: 'shield',
      },
    ] as const;
  });

  constructor() {
    let lastOrg: string | undefined;
    let lastQuery: ParamMap | undefined;
    effect(() => {
      const org = this.session.currentOrg();
      const query = this.query();
      untracked(() => {
        if (!org) {
          lastOrg = undefined;
          this.draft.set({ ...EMPTY_AUDIT_FILTERS });
          this.store.clearScope();
          return;
        }
        // A session scope change without route navigation must not carry the former org's filters.
        const scopeChanged = lastOrg !== undefined && org.id !== lastOrg && query === lastQuery;
        const filters = scopeChanged ? { ...EMPTY_AUDIT_FILTERS } : auditFiltersFromQuery(query);
        lastQuery = query;
        if (scopeChanged && this.filtered()) this.writeQuery(filters);
        if (org.id === lastOrg && sameFilters(filters, this.store.filters())) return;
        lastOrg = org.id;
        this.draft.set({
          ...filters,
          from: localDateTime(filters.from),
          to: localDateTime(filters.to),
        });
        this.invalidRange.set(false);
        this.table()?.resetScroll();
        this.store.resetExport();
        void this.store.load(org.id, filters);
      });
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

  protected updateDraft(change: Partial<AuditFilters>): void {
    this.draft.update((draft) => ({ ...draft, ...change }));
    this.store.resetExport();
  }

  protected searchAction(): void {
    const filters = this.store.filters();
    this.draft.set({
      ...filters,
      action: this.draft().action,
      from: localDateTime(filters.from),
      to: localDateTime(filters.to),
    });
    this.apply();
  }

  protected apply(): void {
    const draft = this.draft();
    const applied = this.store.filters();
    const from = this.toIso(draft.from, applied.from);
    const to = this.toIso(draft.to, applied.to);
    if (from === null || to === null || (from && to && from > to)) {
      this.invalidRange.set(true);
      return;
    }
    this.invalidRange.set(false);
    const org = this.session.currentOrg();
    if (!org) return;
    this.toolbar().close();
    const filters = {
      ...draft,
      action: draft.action.slice(0, 128),
      objectType: draft.objectType.slice(0, 64),
      from,
      to,
    };
    if (sameFilters(filters, this.store.filters()) && this.store.page() > 1)
      void this.store.load(org.id, filters);
    this.writeQuery(filters);
  }

  private writeQuery(filters: AuditFilters): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams: {
        actor_type: filters.actorType || null,
        action: filters.action || null,
        object_type: filters.objectType || null,
        from: filters.from || null,
        to: filters.to || null,
      },
    });
  }

  protected clear(): void {
    this.draft.set({ ...EMPTY_AUDIT_FILTERS });
    this.apply();
  }

  protected reload(): void {
    const org = this.session.currentOrg();
    if (org) {
      this.table()?.resetScroll();
      void this.store.load(org.id);
    }
  }

  protected async previous(): Promise<void> {
    await this.navigate('previous');
  }

  protected async next(): Promise<void> {
    await this.navigate('next');
  }

  protected async retryPage(): Promise<void> {
    if (this.retryReady()) await this.navigate('retryPage');
  }

  protected async changePageSize(size: number): Promise<void> {
    const org = this.session.currentOrg();
    if (!org) return;
    await this.store.setPageSize(org.id, size);
    if (this.session.currentOrg()?.id !== org.id) return;
    this.table()?.resetScroll();
    // The shared pager hands focus to its range when it removes its controls; focusing the heading here would override that.
    const controlsRemain =
      this.store.hasNext() || this.store.page() > 1 || this.store.pageSize() > 20;
    if (controlsRemain) this.heading()?.nativeElement.focus();
  }

  private async navigate(direction: 'next' | 'previous' | 'retryPage'): Promise<void> {
    const org = this.session.currentOrg();
    if (!org) return;
    const filters = this.store.filters();
    await this.store[direction](org.id);
    if (
      this.session.currentOrg()?.id === org.id &&
      this.store.filters() === filters &&
      !this.store.pagingError()
    )
      this.focusRows();
  }
  private focusRows(): void {
    this.table()?.resetScroll();
    this.heading()?.nativeElement.focus();
  }
  private toIso(value: string, applied: string): string | null {
    if (!value) return '';
    // Preserve an unchanged instant even in the repeated hour when local daylight saving ends.
    if (value === localDateTime(applied)) return applied;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }
}

function auditFiltersFromQuery(query: ParamMap): AuditFilters {
  const actor = query.get('actor_type') ?? '';
  return {
    actorType: Object.hasOwn(AUDIT_ACTOR_TYPE_LABELS, actor)
      ? (actor as AuditFilters['actorType'])
      : '',
    action: (query.get('action') ?? '').slice(0, 128),
    objectType: (query.get('object_type') ?? '').slice(0, 64),
    ...canonicalRange(query.get('from'), query.get('to')),
  };
}

function sameFilters(a: AuditFilters, b: AuditFilters): boolean {
  return (
    a.actorType === b.actorType &&
    a.action === b.action &&
    a.objectType === b.objectType &&
    a.from === b.from &&
    a.to === b.to
  );
}

function canonicalDate(value: string | null): string {
  if (!value) return '';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : date.toISOString();
}

// apply() refuses a reversed range; dropping it here keeps the URL from seeding a query apply() would reject.
function canonicalRange(from: string | null, to: string | null): { from: string; to: string } {
  const start = canonicalDate(from);
  const end = canonicalDate(to);
  return start && end && start > end ? { from: '', to: '' } : { from: start, to: end };
}

function localDateTime(value: string): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, -1);
}
