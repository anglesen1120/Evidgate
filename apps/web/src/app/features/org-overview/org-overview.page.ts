import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type { AuditEntryDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { isApiRequestError, type ApiError } from '../../core/http/api-error';
import { mapNetworkError } from '../../core/http/error-mapper';
import { AbsoluteTimeComponent } from '../../shared/time/absolute-time.component';
import { DashboardKpiComponent } from '../../shared/ui/dashboard/dashboard-kpi.component';
import { DashboardMeterComponent } from '../../shared/ui/dashboard/dashboard-meter.component';
import { DashboardWidgetComponent } from '../../shared/ui/dashboard/dashboard-widget.component';
import {
  DataTableComponent,
  type DataTableColumn,
} from '../../shared/ui/data-table/data-table.component';
import type { DataTableCellContext } from '../../shared/ui/data-table/data-table-column';
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
    AbsoluteTimeComponent,
    DashboardKpiComponent,
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
  protected readonly copy = {
    projects: $localize`:@@orgoverview.projects:My projects`,
    projectsDescription: $localize`:@@orgoverview.projects-description:Projects you can access in this organization.`,
    emptyProjects: $localize`:@@orgoverview.empty-projects:No projects to show.`,
    members: $localize`:@@orgoverview.members:Members and invitations`,
    usage: $localize`:@@orgoverview.usage:Organization usage`,
    active: $localize`:@@orgoverview.active-members:Active members`,
    pending: $localize`:@@orgoverview.pending-invites:Pending invitations`,
    ai: $localize`:@@orgoverview.ai-tokens:AI tokens`,
    notMetered: $localize`:@@orgoverview.not-metered:Not metered`,
    results: $localize`:@@orgoverview.results:Results`,
    used: $localize`:@@quota.panel.used:Used`,
    limit: $localize`:@@quota.panel.limit:Limit`,
    unavailable: $localize`:@@quota.panel.unavailable:Unavailable`,
    latestUnavailable: $localize`:@@orgoverview.latest-unavailable:Latest unavailable`,
    noRuns: $localize`:@@orgoverview.no-runs:No runs yet`,
    runError: $localize`:@@orgoverview.run-error:Couldn’t load latest run.`,
  };
  private readonly decisionTemplate =
    viewChild<TemplateRef<DataTableCellContext<DashboardProject>>>('decisionCell');
  private readonly timeTemplate =
    viewChild<TemplateRef<DataTableCellContext<DashboardProject>>>('timeCell');
  private readonly viewTemplate =
    viewChild<TemplateRef<DataTableCellContext<DashboardProject>>>('viewCell');
  protected readonly projectId = (row: DashboardProject): string => row.project.id;
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
      ...(this.store.canRuns() && decision && time
        ? [
            {
              id: 'decision',
              header: $localize`:@@orgoverview.decision-column:Latest decision`,
              role: 'status' as const,
              cell: () => '',
              cellTemplate: decision,
            },
            {
              id: 'time',
              header: $localize`:@@orgoverview.time-column:Latest run (UTC)`,
              role: 'date' as const,
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
              width: {
                min: 'calc(14ch + var(--eg-space-4) * 2)',
                preferred: '1%',
                max: 'calc(14ch + var(--eg-space-4) * 2)',
              },
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
  protected viewProject(name: string): string {
    return $localize`:@@orgoverview.view-project:View project ${name}:name:`;
  }
  protected projectCount(): string {
    const count = this.store.projectRows().length;
    return this.store.projects.hasValue() && this.store.projects.value().more
      ? $localize`:@@orgoverview.projects-more:Showing ${count}:count: projects; more available`
      : $localize`:@@orgoverview.projects-count:Showing ${count}:count: projects`;
  }
  protected memberContext(): string {
    const page = this.store.members.hasValue() ? this.store.members.value() : undefined;
    const count = page?.items.length ?? 0;
    return page?.next_cursor
      ? $localize`:@@orgoverview.members-sample:Among ${count}:count: memberships shown; more available`
      : $localize`:@@orgoverview.members-complete:Among ${count}:count: memberships`;
  }
}
