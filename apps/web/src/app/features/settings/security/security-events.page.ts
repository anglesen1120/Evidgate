import { formatNumber } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  LOCALE_ID,
  computed,
  inject,
  input,
  linkedSignal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { rxResource } from '@angular/core/rxjs-interop';
import { map, of, startWith, timer } from 'rxjs';
import { HlmButton } from '@evidgate/ui/button';
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
import { SECURITY_SEVERITY_LABELS, SECURITY_TYPE_LABELS } from './security-events.config';
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
    FormsModule,
    HlmButton,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    PageHeaderComponent,
    SectionHeaderComponent,
    RequestStateComponent,
    SecurityEventsTableComponent,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
  ],
  providers: [SecurityEventsStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0', '[attr.data-security-phase]': 'store.phase()' },
  templateUrl: './security-events.page.html',
})
export class SecurityEventsPage {
  protected readonly session = inject(SessionStore);
  protected readonly store = inject(SecurityEventsStore);
  private readonly locale = inject(LOCALE_ID);
  readonly pageHeadingLevel = input<1 | 2>(2);
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
  protected readonly severities = Object.entries(SECURITY_SEVERITY_LABELS).map(
    ([value, label]) => ({ value, label }),
  );
  protected readonly pageSizes = [25, 50, 100, 200];
  protected readonly pageSummary = computed(
    () =>
      $localize`:@@settings.security.page-summary:Page ${formatNumber(this.store.pageIndex() + 1, this.locale)}:page: · ${formatNumber(this.store.rows().length, this.locale)}:rows: visible events on this page.`,
  );
  protected readonly copy = {
    title: $localize`:@@settings.security.title:Security events`,
    description: $localize`:@@settings.security.description:Inspect authorized security metadata across the organization. Event records are read-only; times are shown in UTC.`,
    boundary: $localize`:@@settings.security.boundary:Metadata only. Severity is supplied by the server, not a security certification. Cross-tenant targets and attempted secrets are not disclosed.`,
    filters: $localize`:@@settings.security.filters:Filter activity`,
    project: $localize`:@@settings.security.project:Project id`,
    projectHint: $localize`:@@settings.security.project-hint:Optional exact project UUID. Leave blank for all projects.`,
    type: $localize`:@@settings.security.type:Event type`,
    severity: $localize`:@@settings.security.severity:Severity`,
    allTypes: $localize`:@@settings.security.all-types:All V1 event types`,
    allSeverities: $localize`:@@settings.security.all-severities:All severities`,
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
    previous: $localize`:@@settings.security.previous:Previous page`,
    next: $localize`:@@settings.security.next:Next page`,
    pageSize: $localize`:@@settings.security.page-size:Rows per page`,
    pagination: $localize`:@@settings.security.pagination:Security activity pages`,
    capped: $localize`:@@settings.security.capped:Browsing limit reached. Narrow the filters to review older activity.`,
    hidden: $localize`:@@settings.security.hidden:Injection-attempt events are not displayed in this V1 view. A page may contain no visible V1 events; continue to the next page when available.`,
  };
  protected readonly feedback = computed(() => {
    this.session.currentOrg();
    return createFormFeedback([
      {
        id: 'security-from',
        label: this.copy.from,
        describedBy: 'security-time-hint',
        error: () => (this.range().invalidFrom ? this.copy.invalid : null),
      },
      {
        id: 'security-to',
        label: this.copy.to,
        describedBy: 'security-time-hint',
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
  protected move(next: boolean): void {
    if (next) this.store.next();
    else this.store.previous();
    this.heading()?.nativeElement.focus();
  }
}
