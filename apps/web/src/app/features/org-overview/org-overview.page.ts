import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  linkedSignal,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type { AuditEntryDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { isApiRequestError, type ApiError } from '../../core/http/api-error';
import { mapNetworkError } from '../../core/http/error-mapper';
import { AbsoluteTimeComponent } from '../../shared/time/absolute-time.component';
import { DashboardMeterComponent } from '../../shared/ui/dashboard/dashboard-meter.component';
import { retryWithDashboardFocus } from '../../shared/ui/dashboard/dashboard-retry-focus';
import { DashboardWidgetComponent } from '../../shared/ui/dashboard/dashboard-widget.component';
import {
  DataTableComponent,
  type DataTableColumn,
} from '../../shared/ui/data-table/data-table.component';
import type { DataTableCellContext } from '../../shared/ui/data-table/data-table-column';
import { RowActionComponent } from '../../shared/ui/data-table/row-action.component';
import { CopyableIdentifierComponent } from '../../shared/ui/values/copyable-identifier.component';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { DecisionChipComponent } from '../../shared/ui/verdict/decision-chip.component';
import { DashboardActivityComponent } from '../../shared/ui/dashboard/dashboard-activity.component';
import { OrgOverviewStore, type DashboardProject } from './org-overview.store';

@Component({
  selector: 'app-org-overview',
  imports: [
    DecimalPipe,
    RouterLink,
    HlmButton,
    HlmDialogImports,
    RowActionComponent,
    CopyableIdentifierComponent,
    AbsoluteTimeComponent,
    DashboardMeterComponent,
    DashboardWidgetComponent,
    DataTableComponent,
    PageHeaderComponent,
    DecisionChipComponent,
    DashboardActivityComponent,
  ],
  providers: [OrgOverviewStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-(--eg-space-7)' },
  templateUrl: './org-overview.page.html',
  styleUrl: './org-overview.page.css',
})
export class OrgOverviewPage {
  protected readonly store = inject(OrgOverviewStore);
  protected readonly title = $localize`:@@orgoverview.title:Organization overview`;
  protected readonly description = computed(() => this.store.session.currentOrg()?.name ?? '');
  protected readonly inspectedProject = linkedSignal<
    readonly DashboardProject[],
    { row: DashboardProject; trigger: HTMLElement } | null
  >({ source: () => this.store.projectRows(), computation: () => null });
  protected readonly copy = {
    projects: $localize`:@@orgoverview.projects:My projects`,
    projectId: $localize`:@@orgoverview.project-id:Project ID`,
    projectsDescription: $localize`:@@orgoverview.projects-description:Projects you can access in this organization.`,
    emptyProjects: $localize`:@@orgoverview.empty-projects:No projects to show.`,
    members: $localize`:@@orgoverview.members:Members and invitations`,
    usage: $localize`:@@orgoverview.usage:Organization usage`,
    active: $localize`:@@orgoverview.active-members:Active members`,
    pending: $localize`:@@orgoverview.pending-invites:Pending invitations`,
    membersLoaded: $localize`:@@orgoverview.members-loaded:Loaded memberships`,
    membersPartial: $localize`:@@orgoverview.members-partial:Loaded memberships; more available`,
    ai: $localize`:@@orgoverview.ai-tokens:AI tokens`,
    notMetered: $localize`:@@orgoverview.not-metered:Not metered`,
    results: $localize`:@@orgoverview.results:Results`,
    used: $localize`:@@quota.panel.used:Used`,
    limit: $localize`:@@quota.panel.limit:Limit`,
    unavailable: $localize`:@@quota.panel.unavailable:Unavailable`,
    latestUnavailable: $localize`:@@orgoverview.latest-unavailable:Latest unavailable`,
    noRuns: $localize`:@@orgoverview.no-runs:No runs yet`,
    runError: $localize`:@@orgoverview.run-error:Couldn’t load latest run.`,
    loading: $localize`:@@orgoverview.loading:Loading`,
  };
  private readonly decisionTemplate =
    viewChild<TemplateRef<DataTableCellContext<DashboardProject>>>('decisionCell');
  private readonly timeTemplate =
    viewChild<TemplateRef<DataTableCellContext<DashboardProject>>>('timeCell');
  private readonly viewTemplate =
    viewChild<TemplateRef<DataTableCellContext<DashboardProject>>>('viewCell');
  protected readonly projectId = (row: DashboardProject): string => row.project.id;
  protected readonly projectsVisible = computed(
    () => this.store.canProjects() && this.error(this.store.projects.error())?.kind !== 'forbidden',
  );
  protected readonly membersVisible = computed(
    () => this.store.canMembers() && this.error(this.store.members.error())?.kind !== 'forbidden',
  );
  protected readonly usageVisible = computed(
    () => this.store.canUsage() && this.error(this.store.usage.error())?.kind !== 'forbidden',
  );
  protected readonly activityVisible = computed(
    () => this.store.canAudit() && this.error(this.store.activity.error())?.kind !== 'forbidden',
  );
  protected readonly anyWidgetVisible = computed(
    () =>
      this.projectsVisible() ||
      this.membersVisible() ||
      this.usageVisible() ||
      this.activityVisible(),
  );
  protected readonly runsVisible = computed(
    () =>
      this.store.canRuns() && this.store.projectRows().some((row) => row.runStatus !== 'hidden'),
  );
  protected readonly runErrors = computed(() =>
    this.store.projectRows().some((row) => row.runStatus === 'error'),
  );
  protected readonly columns = computed<readonly DataTableColumn<DashboardProject>[]>(() => {
    const decision = this.decisionTemplate();
    const time = this.timeTemplate();
    const view = this.viewTemplate();
    return [
      {
        id: 'name',
        header: $localize`:@@orgoverview.project-column:Project`,
        role: 'identity',
        cell: (row) => row.project.name,
      },
      ...(this.runsVisible() && this.store.decisionsVisible() && decision
        ? [
            {
              id: 'decision',
              header: $localize`:@@orgoverview.decision-column:Latest decision`,
              role: 'status' as const,
              cell: () => '',
              cellTemplate: decision,
            },
          ]
        : []),
      ...(this.runsVisible() && time
        ? [
            {
              id: 'time',
              header: $localize`:@@orgoverview.time-column:Latest run (UTC)`,
              role: 'date' as const,
              width: { min: 'calc(24ch + var(--eg-space-4) * 2)' },
              cell: () => '',
              cellTemplate: time,
            },
          ]
        : []),
      ...(view
        ? [
            {
              id: 'view',
              header: $localize`:@@orgoverview.action-column:Action`,
              role: 'actions' as const,
              cell: () => '',
              width: { min: 'max-content', preferred: '1%' },
              cellTemplate: view,
            },
          ]
        : []),
    ];
  });
  protected readonly results = computed(() =>
    this.store.usage.hasValue()
      ? (this.store.usage.value().metrics.find((metric) => metric.metric === 'results') ?? null)
      : null,
  );
  protected readonly events = computed<readonly AuditEntryDto[]>(() =>
    this.store.activity.hasValue() ? this.store.activity.value().items : [],
  );
  protected error(value: unknown): ApiError | null {
    return value ? (isApiRequestError(value) ? value.error : mapNetworkError()) : null;
  }
  protected retryRuns(summary: HTMLElement): void {
    retryWithDashboardFocus(summary, () => this.store.runs.reload());
  }
  protected viewProject(name: string): string {
    return $localize`:@@orgoverview.view-project:View project ${name}:name:`;
  }
}
