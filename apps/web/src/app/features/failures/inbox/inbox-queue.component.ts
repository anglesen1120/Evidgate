import { ChangeDetectionStrategy, Component, computed, inject, output, TemplateRef, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { StatusPillComponent } from '../../../shared/ui/chips';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import type { DataTableCellContext, DataTableColumn } from '../../../shared/ui/data-table/data-table-column';
import { DesktopMutationDirective } from '../../../shared/ui/narrow';
import { EmptyPatternComponent } from '../../../shared/ui/states';
import { INBOX_COPY, viewIncident } from './failure-inbox.copy';
import { FILTERS, type IncidentId, type IncidentViewModel } from './failure-inbox.fixture';
import { FailureInboxStore } from './failure-inbox.store';
import { InboxChoiceComponent } from './inbox-choice.component';

type IncidentCell = TemplateRef<DataTableCellContext<IncidentViewModel>>;

@Component({
  selector: 'app-inbox-queue',
  imports: [
    DataTableComponent,
    DesktopMutationDirective,
    EmptyPatternComponent,
    HlmBadge,
    HlmButton,
    HlmCard,
    HlmCheckbox,
    HlmIcon,
    HlmInput,
    HlmLabel,
    InboxChoiceComponent,
    RouterLink,
    StatusPillComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './inbox-queue.component.html',
})
export class InboxQueueComponent {
  protected readonly store = inject(FailureInboxStore);
  readonly review = output<readonly IncidentId[]>();
  protected readonly rowId = (row: IncidentViewModel) => row.id;
  protected readonly viewLabel = viewIncident;
  private readonly selectCell = viewChild.required<IncidentCell>('selectCell');
  private readonly subjectCell = viewChild.required<IncidentCell>('subjectCell');
  private readonly statusCell = viewChild.required<IncidentCell>('statusCell');
  private readonly severityCell = viewChild.required<IncidentCell>('severityCell');
  private readonly labelCell = viewChild.required<IncidentCell>('labelCell');
  private readonly actionCell = viewChild.required<IncidentCell>('actionCell');
  protected readonly columns = computed<readonly DataTableColumn<IncidentViewModel>[]>(() => [
    { id: 'select', header: this.c.select, role: 'actions', cell: this.rowId, mutates: true, cellTemplate: this.selectCell() },
    { id: 'incident', header: this.c.incident, role: 'identity', overflow: 'wrap', cell: (row) => `${this.c[row.owner]} · ${row.revision} · ${this.c[row.context]}`, cellTemplate: this.subjectCell() },
    { id: 'status', header: this.c.status, role: 'status', cell: (row) => this.c[row.status], cellTemplate: this.statusCell() },
    { id: 'severity', header: this.c.severity, role: 'status', cell: (row) => this.c[row.severity], cellTemplate: this.severityCell() },
    { id: 'label', header: this.c.failureLabel, role: 'status', cell: (row) => this.c[row.label], cellTemplate: this.labelCell() },
    { id: 'view', header: this.c.view, role: 'actions', cell: () => this.c.view, cellTemplate: this.actionCell() },
  ]);
  protected readonly c = INBOX_COPY;
  protected readonly filters = FILTERS.map((f) => ({
    ...f,
    options: ['all' as const, ...f.choices],
  }));
  protected readonly statuses = [
    { key: 'all', count: 6 },
    { key: 'open', count: 4 },
    { key: 'triaged', count: 2 },
  ] as const;
}
