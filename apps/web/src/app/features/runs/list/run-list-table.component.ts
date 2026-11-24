import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  linkedSignal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type { RunSummary } from '@evidgate/sdk';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
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
import { escapeFormatChars, escapeFormatLabel } from '../../../shared/ui/values/ref-name-display';

type RunCell = TemplateRef<DataTableCellContext<RunSummary>>;

@Component({
  selector: 'app-run-list-table',
  imports: [
    NgTemplateOutlet,
    RouterLink,
    HlmBadge,
    HlmButton,
    HlmDialogImports,
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
  host: { class: 'block min-w-0', tabindex: '-1' },
})
export class RunListTableComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly rows = input.required<readonly RunSummary[]>();
  readonly projectId = input.required<string | null>();
  protected readonly inspected = linkedSignal<
    { rows: readonly RunSummary[]; projectId: string | null },
    string | null
  >({
    source: () => ({ rows: this.rows(), projectId: this.projectId() }),
    computation: ({ rows, projectId }, previous) =>
      previous?.value &&
      previous.source.projectId === projectId &&
      rows.some((row) => row.run_id === previous.value)
        ? previous.value
        : null,
  });
  protected readonly inspectedRow = computed(() => {
    const id = this.inspected();
    return id ? this.rows().find((row) => row.run_id === id) : undefined;
  });
  protected readonly rowId = (row: RunSummary) => row.run_id;
  protected readonly displayRef = escapeFormatChars;
  protected readonly displayLabel = escapeFormatLabel;
  protected readonly preview = middleTruncate;
  protected readonly identifierPreviewLength = 17;
  private readonly identityCell = viewChild.required<RunCell>('identityCell');
  private readonly stateCell = viewChild.required<RunCell>('stateCell');
  private readonly evidenceCell = viewChild.required<RunCell>('evidenceCell');
  private readonly decisionCell = viewChild.required<RunCell>('decisionCell');
  private readonly refCell = viewChild.required<RunCell>('refCell');
  private readonly scopeCell = viewChild.required<RunCell>('scopeCell');
  private readonly viewCell = viewChild.required<RunCell>('viewCell');
  protected viewLabel(id: string): string {
    return $localize`:@@runs.list.view-run:View ${id}:runId:`;
  }
  protected closeInspection(id: string): void {
    this.inspected.set(null);
    const host = this.host.nativeElement;
    if (!host.isConnected) return;
    const view = host.ownerDocument.getElementById(`run-view-${id}`);
    (view && host.contains(view) ? view : host).focus();
  }
  protected readonly copy = {
    caption: $localize`:@@runs.list.table-caption:Project runs`,
    id: $localize`:@@runs.list.column-id:Run id`,
    actions: $localize`:@@runs.list.column-actions:Actions`,
    view: $localize`:@@runs.list.view:View`,
    details: $localize`:@@runs.detail.title:Run detail`,
    close: $localize`:@@ui.close:Close`,
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
  // Rich evidence and scope cells grow with text size inside the current server page.
  protected readonly columns = computed<readonly DataTableColumn<RunSummary>[]>(() => [
    {
      id: 'id',
      header: this.copy.id,
      cell: this.rowId,
      role: 'identity',
      kind: 'identifier',
      priority: 'primary',
      overflow: 'truncate',
      width: {
        min: `calc(${this.identifierPreviewLength}ch + var(--eg-control-touch) + var(--eg-space-4) * 2 + var(--eg-space-2))`,
        preferred: '1%',
      },
      cellTemplate: this.identityCell(),
    },
    {
      id: 'state',
      header: this.copy.state,
      cell: (row) => row.state,
      role: 'status',
      priority: 'primary',
      overflow: 'truncate',
      width: { min: 'calc(12ch + var(--eg-space-4) * 2)' },
      cellTemplate: this.stateCell(),
    },
    {
      id: 'evidence',
      header: this.copy.evidence,
      cell: () => this.copy.evidenceUnavailable,
      role: 'status',
      priority: 'primary',
      overflow: 'wrap',
      // The badge label never wraps: keep the fit floor, but ask for its natural width when the row has room.
      width: { min: 'calc(14ch + var(--eg-space-4) * 2)', preferred: 'max-content' },
      cellTemplate: this.evidenceCell(),
    },
    {
      id: 'decision',
      header: this.copy.decision,
      cell: (row) => row.decision ?? '',
      role: 'status',
      priority: 'primary',
      overflow: 'wrap',
      width: { min: 'calc(15ch + var(--eg-space-4) * 2)' },
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
      width: { preferred: 'auto' },
      cellTemplate: this.refCell(),
    },
    {
      id: 'scope',
      header: this.copy.scope,
      cell: (row) => this.displayLabel(row.rerun_label ?? row.reprocess_label ?? ''),
      role: 'text',
      priority: 'secondary',
      overflow: 'truncate',
      width: { min: 'calc(13ch + var(--eg-space-4) * 2)' },
      cellTemplate: this.scopeCell(),
    },
    {
      id: 'actions',
      header: this.copy.actions,
      cell: () => this.copy.view,
      role: 'actions',
      width: { min: 'max-content', preferred: '1%' },
      cellTemplate: this.viewCell(),
    },
  ]);
}
