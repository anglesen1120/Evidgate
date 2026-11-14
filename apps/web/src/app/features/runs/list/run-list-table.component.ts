import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type { RunSummary } from '@evidgate/sdk';
import { HlmBadge } from '@evidgate/ui/badge';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { RunStatePillComponent } from '../../../shared/ui/chips/run-state-pill.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../shared/ui/data-table/data-table-column';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { CopyValueButtonComponent } from '../../../shared/ui/values/copy-value-button.component';
import { middleTruncate } from '../../../shared/ui/values/middle-truncate';
import { DecisionChipComponent } from '../../../shared/ui/verdict/decision-chip.component';
import { escapeFormatChars, escapeFormatLabel } from './ref-name-display';

type RunCell = TemplateRef<DataTableCellContext<RunSummary>>;

@Component({
  selector: 'app-run-list-table',
  imports: [
    RouterLink,
    HlmBadge,
    AbsoluteTimeComponent,
    RunStatePillComponent,
    StatusPillComponent,
    DataTableComponent,
    DataTableValueComponent,
    CopyValueButtonComponent,
    DecisionChipComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-list-table.component.html',
  host: { class: 'block min-w-0' },
})
export class RunListTableComponent {
  readonly rows = input.required<readonly RunSummary[]>();
  protected readonly rowId = (row: RunSummary) => row.run_id;
  protected readonly displayRef = escapeFormatChars;
  protected readonly displayLabel = escapeFormatLabel;
  protected readonly preview = middleTruncate;
  private readonly identityCell = viewChild.required<RunCell>('identityCell');
  private readonly stateCell = viewChild.required<RunCell>('stateCell');
  private readonly evidenceCell = viewChild.required<RunCell>('evidenceCell');
  private readonly decisionCell = viewChild.required<RunCell>('decisionCell');
  private readonly refCell = viewChild.required<RunCell>('refCell');
  private readonly scopeCell = viewChild.required<RunCell>('scopeCell');
  private readonly createdCell = viewChild.required<RunCell>('createdCell');
  protected readonly copy = {
    caption: $localize`:@@runs.list.table-caption:Project runs`,
    id: $localize`:@@runs.list.column-id:Run id`,
    state: $localize`:@@runs.list.column-state:State`,
    evidence: $localize`:@@runs.list.column-evidence:Evidence`,
    evidenceUnavailable: $localize`:@@runs.list.evidence-unavailable:Unavailable`,
    evidenceHint: $localize`:@@runs.list.evidence-hint:Open the run for evidence completeness.`,
    decision: $localize`:@@runs.list.column-decision:Decision`,
    ref: $localize`:@@runs.list.column-ref:Ref`,
    scope: $localize`:@@runs.list.column-scope:Run scope`,
    created: $localize`:@@runs.list.column-created:Created (UTC)`,
    unverified: $localize`:@@runs.list.unverified-ref:Unverified ref`,
    unavailableRef: $localize`:@@runs.list.ref-unavailable:Ref unavailable`,
    original: $localize`:@@runs.list.original:Original run`,
    rerun: $localize`:@@runs.list.rerun:Rerun`,
    reprocess: $localize`:@@runs.list.reprocess:Reprocessed`,
    child: $localize`:@@runs.list.child:Derived run`,
  };
  // Rich evidence/scope cells grow with text size; the shared table bounds loaded pages at 200.
  protected readonly columns = computed<readonly DataTableColumn<RunSummary>[]>(() => [
    {
      id: 'id',
      header: this.copy.id,
      cell: this.rowId,
      role: 'identity',
      kind: 'identifier',
      priority: 'primary',
      overflow: 'truncate',
      cellTemplate: this.identityCell(),
    },
    {
      id: 'state',
      header: this.copy.state,
      cell: (row) => row.state,
      role: 'status',
      priority: 'primary',
      overflow: 'truncate',
      cellTemplate: this.stateCell(),
    },
    {
      id: 'evidence',
      header: this.copy.evidence,
      cell: () => this.copy.evidenceUnavailable,
      role: 'status',
      priority: 'primary',
      overflow: 'wrap',
      cellTemplate: this.evidenceCell(),
    },
    {
      id: 'decision',
      header: this.copy.decision,
      cell: (row) => row.decision ?? '',
      role: 'status',
      priority: 'primary',
      overflow: 'wrap',
      cellTemplate: this.decisionCell(),
    },
    {
      id: 'ref',
      header: this.copy.ref,
      cell: (row) => this.displayRef(row.ref.name ?? this.copy.unavailableRef),
      role: 'identity',
      kind: 'branch',
      priority: 'secondary',
      overflow: 'truncate',
      cellTemplate: this.refCell(),
    },
    {
      id: 'scope',
      header: this.copy.scope,
      cell: (row) => this.displayLabel(row.rerun_label ?? row.reprocess_label ?? ''),
      role: 'text',
      priority: 'secondary',
      overflow: 'truncate',
      cellTemplate: this.scopeCell(),
    },
    {
      id: 'created',
      header: this.copy.created,
      cell: (row) => row.created_at,
      role: 'date',
      priority: 'secondary',
      overflow: 'wrap',
      cellTemplate: this.createdCell(),
    },
  ]);
}
