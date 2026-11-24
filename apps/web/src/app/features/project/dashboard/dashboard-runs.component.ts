import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import type { RunSummary } from '@evidgate/sdk';
import { RowActionComponent } from '../../../shared/ui/data-table/row-action.component';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import {
  DataTableComponent,
  type DataTableCellContext,
  type DataTableColumn,
} from '../../../shared/ui/data-table';
import { DecisionChipComponent } from '../../../shared/ui/verdict/decision-chip.component';
import { RunStatePillComponent } from '../../../shared/ui/chips/run-state-pill.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';

@Component({
  selector: 'app-dashboard-runs',
  imports: [
    DataTableComponent,
    AbsoluteTimeComponent,
    RowActionComponent,
    HlmDialogImports,
    DecisionChipComponent,
    RunStatePillComponent,
    CopyableIdentifierComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <app-data-table
      [rows]="rows()"
      [columns]="columns()"
      [rowId]="rowId"
      [caption]="copy.caption"
      [fitToContainer]="true"
      [flow]="'page'"
    />
    <ng-template #identity let-row>
      <app-copyable-identifier [value]="row.run_id" [label]="copy.run" />
      <app-absolute-time class="block text-xs text-muted-foreground" [value]="row.created_at" />
    </ng-template>
    <ng-template #decision let-row
      ><app-decision-chip [decision]="row.decision ?? 'pending'" [compact]="true"
    /></ng-template>
    <ng-template #state let-row
      ><app-run-state-pill [state]="row.state" [compact]="true"
    /></ng-template>
    <ng-template #view let-row>
      <button
        appRowAction
        #trigger
        data-dashboard-run-view
        [label]="viewLabel(row.run_id)"
        [attr.aria-haspopup]="'dialog'"
        [attr.aria-label]="viewLabel(row.run_id)"
        (click)="inspected.set({ row, trigger: trigger.nativeElement })"
      ></button>
    </ng-template>
    @if (inspected(); as selection) {
      <hlm-dialog
        [state]="'open'"
        [restoreFocus]="selection.trigger"
        [autoFocus]="'first-heading'"
        (closed)="inspected.set(null)"
      >
        <hlm-dialog-content *hlmDialogPortal [size]="'md'" data-dashboard-run-dialog>
          <header hlmDialogHeader>
            <h2 hlmDialogTitle>{{ copy.details }}</h2>
            <p hlmDialogDescription>{{ copy.snapshot }}</p>
          </header>
          <dl class="hlm-field-grid">
            <div class="hlm-field-wide">
              <dt>{{ copy.run }}</dt>
              <dd class="m-0">
                <app-copyable-identifier
                  [value]="selection.row.run_id"
                  [label]="copy.run"
                  [truncate]="false"
                />
              </dd>
            </div>
            <div>
              <dt>{{ copy.created }}</dt>
              <dd class="m-0"><app-absolute-time [value]="selection.row.created_at" /></dd>
            </div>
            <div>
              <dt>{{ copy.state }}</dt>
              <dd class="m-0"><app-run-state-pill [state]="selection.row.state" /></dd>
            </div>
            @if (decisionsVisible()) {
              <div>
                <dt>{{ copy.decision }}</dt>
                <dd class="m-0">
                  <app-decision-chip [decision]="selection.row.decision ?? 'pending'" />
                </dd>
              </div>
            }
            <div>
              <dt>{{ copy.ref }}</dt>
              <dd class="m-0">
                <bdi>{{ displayRef(selection.row.ref.name ?? copy.unavailable) }}</bdi>
              </dd>
            </div>
          </dl>
        </hlm-dialog-content>
      </hlm-dialog>
    }
  `,
})
export class DashboardRunsComponent {
  readonly rows = input.required<readonly RunSummary[]>();
  readonly decisionsVisible = input(false);
  protected readonly displayRef = escapeFormatChars;
  protected readonly rowId = (row: RunSummary) => row.run_id;
  protected readonly inspected = linkedSignal<
    readonly RunSummary[],
    { row: RunSummary; trigger: HTMLElement } | null
  >({ source: this.rows, computation: () => null });
  private readonly identity =
    viewChild.required<TemplateRef<DataTableCellContext<RunSummary>>>('identity');
  private readonly decision =
    viewChild.required<TemplateRef<DataTableCellContext<RunSummary>>>('decision');
  private readonly state =
    viewChild.required<TemplateRef<DataTableCellContext<RunSummary>>>('state');
  private readonly view = viewChild.required<TemplateRef<DataTableCellContext<RunSummary>>>('view');
  protected readonly copy = {
    caption: $localize`:@@project.dashboard.runs-caption:Runs in this sample`,
    run: $localize`:@@project.dashboard.run:Run`,
    decision: $localize`:@@project.dashboard.decision:Decision`,
    state: $localize`:@@project.dashboard.state:State`,
    view: $localize`:@@project.dashboard.view:View`,
    created: $localize`:@@project.dashboard.created:Created (UTC)`,
    details: $localize`:@@project.dashboard.run-details:Run details`,
    snapshot: $localize`:@@project.dashboard.run-snapshot:Read-only facts from the loaded run sample.`,
    ref: $localize`:@@project.dashboard.ref:Reference`,
    unavailable: $localize`:@@project.dashboard.unavailable:Unavailable`,
  };
  protected readonly columns = computed<readonly DataTableColumn<RunSummary>[]>(() => [
    {
      id: 'run',
      header: this.copy.run,
      cell: this.rowId,
      role: 'identity',
      kind: 'identifier',
      cellTemplate: this.identity(),
    },
    {
      id: 'state',
      header: this.copy.state,
      cell: (row) => row.state,
      role: 'status',
      cellTemplate: this.state(),
    },
    ...(this.decisionsVisible()
      ? [
          {
            id: 'decision',
            header: this.copy.decision,
            cell: (row: RunSummary) => row.decision ?? '',
            role: 'status' as const,
            cellTemplate: this.decision(),
          },
        ]
      : []),
    {
      id: 'view',
      header: this.copy.view,
      cell: () => this.copy.view,
      role: 'actions',
      width: {
        min: 'calc(14ch + var(--eg-space-4) * 2)',
        preferred: '1%',
        max: 'calc(14ch + var(--eg-space-4) * 2)',
      },
      cellTemplate: this.view(),
    },
  ]);
  protected viewLabel(id: string): string {
    return $localize`:@@project.dashboard.view-run:View run ${id}:id:`;
  }
}
