import { formatNumber, NgTemplateOutlet } from '@angular/common';
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
import { ActivatedRoute } from '@angular/router';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { SessionStore } from '../../../core/session/session.store';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { AUDIT_ACTOR_TYPE_LABELS } from './actor-type-labels';
import { AuditExportComponent } from './audit-export.component';
import { AuditTableComponent } from './audit-table.component';
import { AuditStore, EMPTY_AUDIT_FILTERS, type AuditFilters } from './audit.store';

const VISIBLE_ROW_LIMIT = 25;

@Component({
  selector: 'app-audit-page',
  imports: [
    FormsModule,
    NgTemplateOutlet,
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
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
    class: 'block min-w-0 text-sm text-foreground [&_p]:m-0 [&_code]:font-mono',
    '[attr.data-audit-phase]': 'store.phase()',
  },
})
export class AuditPage {
  protected readonly actorLabel = (value: unknown): string => {
    if (typeof value !== 'string' || !value) return this.copy.anyActor;
    return Object.hasOwn(AUDIT_ACTOR_TYPE_LABELS, value)
      ? AUDIT_ACTOR_TYPE_LABELS[value as keyof typeof AUDIT_ACTOR_TYPE_LABELS]
      : value;
  };
  protected readonly store = inject(AuditStore);
  protected readonly session = inject(SessionStore);
  /**
   * The `audit` sidebar entry mounts this page directly under the app shell, so the page owns the
   * one `h1`; as a Settings tab the shell's `h1` ("Settings") is the page heading and this is an
   * `h2`. The route says which (`audit.routes.ts`), never the page guessing from its position.
   */
  protected readonly ownsPageHeading =
    inject(ActivatedRoute).snapshot.data['pageHeadingLevel'] === 1;
  protected readonly pageHeadingSize = 'page' as const;
  protected readonly sectionHeadingSize = 'section' as const;
  protected readonly subsectionHeadingSize = 'subsection' as const;
  protected readonly draft = signal<AuditFilters>({ ...EMPTY_AUDIT_FILTERS });
  protected readonly invalidRange = signal(false);
  protected readonly offset = signal(0);
  protected readonly visibleRows = computed(() =>
    this.store.rows().slice(this.offset(), this.offset() + VISIBLE_ROW_LIMIT),
  );
  protected readonly nextWindow = computed(
    () => this.offset() + VISIBLE_ROW_LIMIT < this.store.rows().length,
  );
  protected readonly unappliedFilters = computed(() => {
    const draft = this.draft();
    const applied = this.store.filters();
    return (
      draft.actorType !== applied.actorType ||
      draft.action !== applied.action ||
      draft.objectType !== applied.objectType ||
      this.toIso(draft.from) !== applied.from ||
      this.toIso(draft.to) !== applied.to
    );
  });
  protected readonly rowRange = computed(() => {
    const total = this.store.rows().length;
    const start = formatNumber(total === 0 ? 0 : this.offset() + 1, this.locale);
    const end = formatNumber(Math.min(this.offset() + VISIBLE_ROW_LIMIT, total), this.locale);
    return $localize`:@@settings.audit.row-range:Rows ${start}:start:–${end}:end: of ${formatNumber(total, this.locale)}:total: loaded.`;
  });
  protected readonly filtered = computed(() =>
    Object.values(this.store.filters()).some((value) => value !== ''),
  );
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() => {
    const phase = this.store.phase();
    return !this.session.currentOrg() || phase === 'idle' ? 'loading' : phase;
  });
  protected readonly retryReady = signal(true);
  private readonly pagingFailure = signal(false);
  protected readonly pagingAlert = computed(
    () =>
      this.store.pagingError() ??
      (this.pagingFailure() ? { message: this.copy.pagingFailed, code: null } : null),
  );
  private readonly heading = viewChild<ElementRef<HTMLHeadingElement>>('heading');
  private readonly table = viewChild(AuditTableComponent);
  /** The active build locale: the browse bound is a number, grouped the way that locale does. */
  private readonly locale = inject(LOCALE_ID);
  protected readonly copy = {
    title: $localize`:@@settings.audit.title:Audit log`,
    description: $localize`:@@settings.audit.description:Review organization activity and support access. Audit records cannot be edited here.`,
    filters: $localize`:@@settings.audit.filters:Filter activity`,
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
    apply: $localize`:@@settings.audit.apply:Apply filters`,
    clear: $localize`:@@settings.audit.clear:Clear filters`,
    rangeError: $localize`:@@settings.audit.range-error:Enter a valid time range with From no later than To.`,
    emptyTitle: $localize`:@@settings.audit.empty-title:No activity recorded`,
    emptyDescription: $localize`:@@settings.audit.empty-description:Audit events will appear here when activity is recorded.`,
    noMatchTitle: $localize`:@@settings.audit.no-match-title:No results match these filters`,
    noMatchDescription: $localize`:@@settings.audit.no-match-description:Clear or narrow the filters to review audit activity.`,
    pagination: $localize`:@@settings.audit.pagination:Audit activity pages`,
    previous: $localize`:@@settings.audit.previous:Previous rows`,
    next: $localize`:@@settings.audit.next:Next rows`,
    loadMore: $localize`:@@settings.audit.load-more:Load more`,
    loadingMore: $localize`:@@settings.audit.loading-more:Loading more activity…`,
    retry: $localize`:@@settings.audit.paging-retry:Retry loading more`,
    pagingFailed: $localize`:@@settings.audit.paging-failed:The next audit page could not be loaded. The rows already shown are unchanged.`,
  };

  constructor() {
    effect(() => {
      const org = this.session.currentOrg();
      untracked(() => {
        this.draft.set({ ...EMPTY_AUDIT_FILTERS });
        this.invalidRange.set(false);
        this.resetWindow();
        this.store.resetExport();
        if (org) void this.store.load(org.id, { ...EMPTY_AUDIT_FILTERS });
      });
    });
    effect((onCleanup) => {
      const seconds = this.store.loadError()?.retryAfterSeconds;
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

  protected apply(): void {
    const draft = this.draft();
    const from = this.toIso(draft.from);
    const to = this.toIso(draft.to);
    if (from === null || to === null || (from && to && from > to)) {
      this.invalidRange.set(true);
      return;
    }
    this.invalidRange.set(false);
    const org = this.session.currentOrg();
    if (!org) return;
    this.resetWindow();
    this.store.resetExport();
    void this.store.load(org.id, {
      actorType: draft.actorType,
      action: draft.action.slice(0, 128),
      objectType: draft.objectType.slice(0, 64),
      from,
      to,
    });
  }

  protected clear(): void {
    this.draft.set({ ...EMPTY_AUDIT_FILTERS });
    this.apply();
  }

  protected reload(): void {
    const org = this.session.currentOrg();
    if (org) {
      this.resetWindow();
      void this.store.load(org.id);
    }
  }

  protected previous(): void {
    this.offset.update((value) => Math.max(0, value - VISIBLE_ROW_LIMIT));
    this.focusRows();
  }
  protected next(): void {
    if (this.nextWindow()) {
      this.offset.update((value) => value + VISIBLE_ROW_LIMIT);
      this.focusRows();
    }
  }
  protected async loadMore(): Promise<void> {
    const org = this.session.currentOrg();
    if (!org || this.store.paging()) return;
    const filters = this.store.filters();
    this.pagingFailure.set(false);
    try {
      await this.store.loadMore(org.id);
    } catch {
      if (this.session.currentOrg()?.id === org.id && this.store.filters() === filters)
        this.pagingFailure.set(true);
    }
  }
  /**
   * The browse-bound hint. The count is the rows the tab actually holds, not a fixed
   * page-count product: the bound is a number of PAGES, so a server that returns pages shorter
   * than `AUDIT_PAGE_LIMIT` reaches it with fewer rows, and a constant would then claim more rows
   * than are on screen — the same reasoning the export banner applies to its own row count. The
   * number is grouped through the active locale, so `vi` prints `10.000`, not `10,000`.
   */
  protected browseCappedText(rows: number): string {
    return $localize`:@@settings.audit.browse-capped:Showing the newest ${formatNumber(rows, this.locale)}:rows: rows. Narrow the filters to review older activity.`;
  }
  private resetWindow(): void {
    this.offset.set(0);
    this.pagingFailure.set(false);
    this.table()?.resetScroll();
  }
  private focusRows(): void {
    this.table()?.resetScroll();
    this.heading()?.nativeElement.focus();
  }
  private toIso(value: string): string | null {
    if (!value) return '';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }
}
