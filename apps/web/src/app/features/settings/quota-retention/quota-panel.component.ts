import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  TemplateRef,
  viewChild,
} from '@angular/core';
import type { UsageSummaryProjectDto } from '@evidgate/sdk';
import { HlmCardImports } from '@evidgate/ui/card';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../shared/ui/data-table/data-table-column';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { quotaMetricLabel } from './quota-metric-label';
import { QuotaStore } from './quota.store';

type ProjectCell = TemplateRef<DataTableCellContext<UsageSummaryProjectDto>>;

@Component({
  selector: 'app-quota-panel',
  imports: [
    HlmCardImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    DataTableComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    NumberValueComponent,
  ],
  templateUrl: './quota-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0', '[attr.data-quota-state]': 'quota.loadState()' },
})
export class QuotaPanelComponent {
  protected readonly quota = inject(QuotaStore);
  protected readonly metricLabel = quotaMetricLabel;
  protected readonly sectionSize = 'section' as const;
  protected readonly subsectionSize = 'subsection' as const;
  protected readonly loadingLayout = 'cards' as const;
  protected readonly destructive = 'destructive' as const;
  protected readonly off = 'off' as const;
  protected readonly warningTone = 'warning' as const;
  protected readonly dangerTone = 'danger' as const;
  protected readonly projectId = (row: UsageSummaryProjectDto): string => row.project_id;
  private readonly usedCell = viewChild.required<ProjectCell>('usedCell');
  private readonly limitCell = viewChild.required<ProjectCell>('limitCell');
  private readonly stateCell = viewChild.required<ProjectCell>('stateCell');
  protected readonly columns = computed<readonly DataTableColumn<UsageSummaryProjectDto>[]>(() => [
    {
      id: 'project',
      header: this.copy.project,
      role: 'identity',
      kind: 'identifier',
      priority: 'primary',
      cell: (row) => row.project_id,
    },
    {
      id: 'used',
      header: this.copy.used,
      role: 'number',
      cell: (row) => row.used,
      cellTemplate: this.usedCell(),
    },
    {
      id: 'limit',
      header: this.copy.limit,
      role: 'number',
      cell: (row) => row.limit ?? this.copy.unavailable,
      cellTemplate: this.limitCell(),
    },
    {
      id: 'state',
      header: this.copy.recordedState,
      role: 'text',
      priority: 'primary',
      cell: (row) =>
        row.blocked_at ? this.copy.blocked : row.warned_at ? this.copy.warned : this.copy.noAlert,
      cellTemplate: this.stateCell(),
    },
  ]);
  protected readonly copy = {
    title: $localize`:@@quota.panel.title:Quota & retention`,
    description: $localize`:@@quota.panel.description:Organization-wide counters and limits supplied by the service. Quota state does not change recorded test outcomes.`,
    loading: $localize`:@@quota.panel.loading:Loading usage summary…`,
    period: $localize`:@@quota.panel.period:Billing period`,
    empty: $localize`:@@quota.panel.empty:No usage counters were supplied for this period.`,
    used: $localize`:@@quota.panel.used:Used`,
    limit: $localize`:@@quota.panel.limit:Limit`,
    unavailable: $localize`:@@quota.panel.unavailable:Unavailable`,
    notMeasured: $localize`:@@quota.panel.not-measured:Not measured yet.`,
    blocked: $localize`:@@quota.panel.blocked:Limit reached`,
    warned: $localize`:@@quota.panel.warned-short:Near limit`,
    warnedAt: $localize`:@@quota.panel.warned-at:Warning recorded`,
    blockedAt: $localize`:@@quota.panel.blocked-at:Block recorded`,
    project: $localize`:@@quota.panel.project:Project`,
    projects: $localize`:@@quota.panel.projects:Per project`,
    projectCaption: $localize`:@@quota.panel.project-caption:Returned project counters. Organization totals are supplied separately, not calculated from these rows.`,
    recordedState: $localize`:@@quota.panel.recorded-state:Recorded quota state`,
    noAlert: $localize`:@@quota.panel.no-alert:No alert recorded`,
    incomplete: $localize`:@@quota.panel.incomplete:More project counters exist on the service. All counters returned in this response are available below; this is not the complete organization breakdown.`,
    failed: $localize`:@@quota.panel.summary-unavailable:Usage summary unavailable. Try loading it again.`,
    retry: $localize`:@@quota.panel.retry:Reload usage`,
    storageTitle: $localize`:@@quota.panel.storage-title:Daily storage counters`,
    storageReason: $localize`:@@quota.panel.storage-reason:Daily storage and egress counters are unavailable because the service does not expose a read for them.`,
    storedBytes: $localize`:@@quota.panel.storage-bytes:Stored bytes`,
    egressBytes: $localize`:@@quota.panel.egress-bytes:Egress bytes`,
  };

  constructor() {
    this.quota.load();
  }
}
