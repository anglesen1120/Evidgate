import { FieldHintComponent } from '../../../shared/ui/forms/field-hint.component';
import { formatNumber } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  LOCALE_ID,
  computed,
  inject,
  linkedSignal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { rxResource } from '@angular/core/rxjs-interop';
import { map, of, startWith, timer } from 'rxjs';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { SessionStore } from '../../../core/session/session.store';
import { createFormFeedback } from '../../../shared/forms/form-feedback';
import { FieldFeedbackComponent } from '../../../shared/forms/field-feedback.component';
import { FormErrorSummaryComponent } from '../../../shared/forms/form-error-summary.component';
import { SectionHeaderComponent } from '../../../shared/ui/page-header/section-header.component';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { clampTimerDelay } from '../../../shared/time/timer-delay';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { DataTablePagerComponent } from '../../../shared/ui/data-table/data-table-pager.component';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import {
  SECURITY_EVENTS_VERSION,
  SECURITY_SEVERITY_LABELS,
  SECURITY_TYPE_LABELS,
} from './security-events.config';
import { SecurityEventsTableComponent } from './security-events-table.component';
import { securityEventRange } from './security-events-range';
import {
  EMPTY_SECURITY_FILTERS,
  SecurityEventsStore,
  type SecurityFilters,
} from './security-events.store';

@Component({
  selector: 'app-security-events-page',
  imports: [
    FieldHintComponent,
    FormsModule,
    HlmBadge,
    HlmButton,
    HlmCardImports,
    HlmIcon,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    PageHeaderComponent,
    SectionHeaderComponent,
    ShellIconComponent,
    RequestStateComponent,
    SecurityEventsTableComponent,
    DataTablePagerComponent,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
  ],
  providers: [SecurityEventsStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: '@container grid min-w-0 grid-cols-[minmax(0,1fr)] gap-4 wrap-anywhere',
    '[attr.data-security-phase]': 'store.phase()',
  },
  templateUrl: './security-events.page.html',
})
export class SecurityEventsPage {
  protected readonly session = inject(SessionStore);
  protected readonly store = inject(SecurityEventsStore);
  private readonly locale = inject(LOCALE_ID);
  // The route says whether this page owns the h1 or is a Settings tab under the shell's h1.
  protected readonly ownsPageHeading =
    inject(ActivatedRoute, { optional: true })?.snapshot.data['pageHeadingLevel'] === 1;
  protected readonly draft = linkedSignal(() => {
    this.session.currentOrg();
    return { ...EMPTY_SECURITY_FILTERS };
  });
  private readonly range = computed(() => securityEventRange(this.draft().from, this.draft().to));
  protected readonly retryReady = rxResource({
    params: () => this.store.loadError(),
    stream: ({ params }) =>
      params?.retryAfterSeconds && params.retryAfterSeconds > 0
        ? timer(clampTimerDelay(params.retryAfterSeconds * 1000)).pipe(
            map(() => true),
            startWith(false),
          )
        : of(true),
  });
  private readonly heading = viewChild<ElementRef<HTMLElement>>('results');
  protected readonly types = Object.entries(SECURITY_TYPE_LABELS).map(([value, label]) => ({
    value,
    label,
  }));
  protected readonly tableStatus = computed(() =>
    this.store.hasPage() ? 'ready' : this.store.phase(),
  );
  protected readonly pageSummary = computed(
    () =>
      $localize`:@@settings.security.page-summary:Page ${formatNumber(this.store.pageIndex() + 1, this.locale)}:page: · ${formatNumber(this.store.rows().length, this.locale)}:rows: visible events on this page.`,
  );
  protected readonly copy = {
    title: $localize`:@@settings.security.title:Security events`,
    description: $localize`:@@settings.security.intro:Inspect authorized event metadata without exposing request bodies or foreign targets.`,
    boundary: $localize`:@@settings.security.boundary:Metadata only. Severity is supplied by the server, not a security certification. Cross-tenant targets and attempted secrets are not disclosed.`,
    activity: $localize`:@@settings.security-activity:Recent activity`,
    safeData: $localize`:@@settings.security-safe-data:Safe event data`,
    newest: $localize`:@@settings.security.newest-first:Newest first`,
    readOnly: $localize`:@@settings.security.read-only:Read only`,
    scope: $localize`:@@settings.scope-label:Organization settings`,
    filters: $localize`:@@settings.security.filters:Filter activity`,
    project: $localize`:@@settings.security.project:Project id`,
    projectHint: $localize`:@@settings.security.project-hint:Optional exact project UUID. Leave blank for all projects.`,
    type: $localize`:@@settings.security.type:Event type`,
    severity: $localize`:@@settings.security.severity:Severity`,
    allTypes: $localize`:@@settings.security.all-types:All V1 event types`,
    allSeverities: $localize`:@@settings.security.all-severities:All severities`,
    onPage: $localize`:@@settings.count-on-page:On this page`,
    from: $localize`:@@settings.security.from:From (inclusive)`,
    to: $localize`:@@settings.security.to:To (exclusive)`,
    timeHint: $localize`:@@settings.security.time-hint:Enter local times; results use UTC. The upper bound is exclusive.`,
    invalid: $localize`:@@settings.security.invalid:Enter valid dates with From earlier than To.`,
    apply: $localize`:@@settings.security.apply:Apply filters`,
    clear: $localize`:@@settings.security.clear:Clear filters`,
    loading: $localize`:@@settings.security.loading:Loading security events…`,
    empty: $localize`:@@settings.security.empty:No security events recorded`,
    emptyDescription: $localize`:@@settings.security.empty-description:Recorded V1 security events will appear here.`,
    noMatch: $localize`:@@settings.security.no-match:No results match these filters`,
    noMatchDescription: $localize`:@@settings.security.no-match-description:Clear or change the filters to inspect another scope or time range.`,
    capped: $localize`:@@settings.security.capped:Browsing limit reached. Narrow the filters to review older activity.`,
    hidden: $localize`:@@settings.security.hidden:Injection-attempt events are not displayed in this V1 view. A page may contain no visible V1 events; continue to the next page when available.`,
  };
  protected readonly subtitle = computed(() => {
    if (this.store.phase() !== 'ready') return this.copy.description;
    const org = this.session.currentOrg()?.name;
    return [
      org ? escapeFormatChars(org) : this.copy.scope,
      SECURITY_EVENTS_VERSION,
      this.pageSummary(),
    ].join(' · ');
  });
  protected readonly pageCounts = computed(() => {
    const rows = this.store.rows();
    const severity = { info: 0, warning: 0, critical: 0 };
    let support = 0;
    for (const row of rows) {
      if (row.severity === 'info' || row.severity === 'warning' || row.severity === 'critical')
        severity[row.severity]++;
      if (row.type === 'support_access') support++;
    }
    return { severity, support, total: rows.length };
  });
  protected readonly severityOptions = [
    { value: '' as const, label: this.copy.allSeverities },
    ...(Object.keys(SECURITY_SEVERITY_LABELS) as (keyof typeof SECURITY_SEVERITY_LABELS)[]).map(
      (value) => ({ value, label: SECURITY_SEVERITY_LABELS[value] }),
    ),
  ];
  protected readonly summary = computed(() => {
    const counts = this.pageCounts();
    const onPage = this.copy.onPage;
    return [
      {
        label: $localize`:@@settings.security.visible:Visible events`,
        hint: onPage,
        value: formatNumber(counts.total, this.locale),
        icon: 'audit',
      },
      {
        label: $localize`:@@settings.security.critical:Critical events`,
        hint: onPage,
        value: formatNumber(counts.severity.critical, this.locale),
        icon: 'shield-alert',
      },
      {
        label: $localize`:@@settings.security.support-scoped:Support-scoped`,
        hint: onPage,
        value: formatNumber(counts.support, this.locale),
        icon: 'people',
      },
      {
        label: $localize`:@@settings.security-event-catalog:Event catalog`,
        value: SECURITY_EVENTS_VERSION,
        icon: 'folder',
        hint: $localize`:@@settings.security.metadata-only:Metadata only`,
      },
    ] as const;
  });
  protected readonly feedback = computed(() => {
    this.session.currentOrg();
    return createFormFeedback([
      {
        id: 'security-from',
        label: this.copy.from,
        hint: this.copy.timeHint,
        error: () => (this.range().invalidFrom ? this.copy.invalid : null),
      },
      {
        id: 'security-to',
        label: this.copy.to,
        hint: this.copy.timeHint,
        error: () => (this.range().invalidTo ? this.copy.invalid : null),
      },
    ]);
  });
  protected updateDraft(change: Partial<SecurityFilters>): void {
    this.draft.update((value) => ({ ...value, ...change }));
  }
  protected apply(): void {
    if (!this.feedback().submit()) return;
    const draft = this.draft();
    const { from, to } = this.range();
    this.store.apply({
      ...draft,
      projectId: draft.projectId.trim(),
      from: from?.toISOString() ?? '',
      to: to?.toISOString() ?? '',
    });
  }
  protected clear(): void {
    this.heading()?.nativeElement.focus();
    this.feedback().reset();
    this.draft.set({ ...EMPTY_SECURITY_FILTERS });
    this.apply();
  }
}
