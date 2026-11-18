import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  TemplateRef,
  viewChild,
} from '@angular/core';
import type { CaseDto } from '@evidgate/sdk';
import { StatusPillComponent } from '../../shared/ui/chips/status-pill.component';
import { DataTableComponent } from '../../shared/ui/data-table/data-table.component';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../shared/ui/data-table/data-table-column';
import { DataTableValueComponent } from '../../shared/ui/data-table/data-table-value.component';
import { CopyableIdentifierComponent } from '../../shared/ui/values/copyable-identifier.component';
import { escapeFormatChars } from '../../shared/ui/values/ref-name-display';

type CaseCell = TemplateRef<DataTableCellContext<CaseDto>>;

@Component({
  selector: 'app-case-list-table',
  imports: [
    DataTableComponent,
    DataTableValueComponent,
    CopyableIdentifierComponent,
    StatusPillComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <app-data-table
      [columns]="columns()"
      [rows]="rows()"
      [rowId]="rowId"
      [caption]="copy.caption"
      [fitToContainer]="true"
      [flow]="'page'"
    />
    <ng-template #identity let-row let-stacked="stacked">
      <div class="grid min-w-0 gap-(--eg-space-2)" data-catalog-identity>
        <bdi class="min-w-0 font-(weight:--eg-weight-semibold)">
          <app-data-table-value [value]="display(row.name)" [truncate]="!stacked" />
        </bdi>
        <app-copyable-identifier
          [value]="row.annotation_id"
          [label]="copy.stableId"
          [truncate]="!stacked"
          [copySize]="'lg'"
        />
        <app-copyable-identifier
          [value]="row.id"
          [label]="copy.id"
          [truncate]="!stacked"
          [copySize]="'lg'"
        />
      </div>
    </ng-template>
    <ng-template #framework let-row let-stacked="stacked">
      <bdi><app-data-table-value [value]="display(row.framework)" [truncate]="!stacked" /></bdi>
    </ng-template>
    <ng-template #status let-row>
      <app-status-pill
        [label]="statusLabel(row.status)"
        [tone]="row.status === 'missing' ? 'warning' : 'neutral'"
      />
    </ng-template>
    <ng-template #owner let-row let-stacked="stacked">
      @if (row.owner_membership_id) {
        <app-copyable-identifier
          [value]="row.owner_membership_id"
          [label]="copy.owner"
          [truncate]="!stacked"
          [copySize]="'lg'"
        />
      } @else {
        <span>{{ copy.unassigned }}</span>
      }
    </ng-template>
  `,
})
export class CaseListTableComponent {
  readonly rows = input.required<readonly CaseDto[]>();
  protected readonly rowId = (row: CaseDto) => row.id;
  protected readonly display = escapeFormatChars;
  private readonly identity = viewChild.required<CaseCell>('identity');
  private readonly framework = viewChild.required<CaseCell>('framework');
  private readonly status = viewChild.required<CaseCell>('status');
  private readonly owner = viewChild.required<CaseCell>('owner');
  protected readonly copy = {
    caption: $localize`:@@catalog.table-caption:Test identities in this page`,
    identity: $localize`:@@catalog.column-identity:Identity / name`,
    stableId: $localize`:@@catalog.stable-id:Stable identity`,
    id: $localize`:@@catalog.case-id:Case ID`,
    framework: $localize`:@@catalog.framework:Framework`,
    status: $localize`:@@catalog.status:Status`,
    owner: $localize`:@@catalog.owner:Owner membership`,
    unassigned: $localize`:@@catalog.unassigned:Unassigned`,
    active: $localize`:@@catalog.active:Active`,
    missing: $localize`:@@catalog.missing:Missing`,
    archived: $localize`:@@catalog.archived:Archived`,
  };
  protected readonly statusLabel = (status: CaseDto['status']) => this.copy[status];
  protected readonly columns = computed<readonly DataTableColumn<CaseDto>[]>(() => [
    {
      id: 'identity',
      header: this.copy.identity,
      cell: (row) => row.annotation_id,
      role: 'identity',
      kind: 'name',
      priority: 'primary',
      overflow: 'truncate',
      cellTemplate: this.identity(),
    },
    {
      id: 'framework',
      header: this.copy.framework,
      cell: (row) => row.framework,
      role: 'text',
      priority: 'secondary',
      overflow: 'truncate',
      cellTemplate: this.framework(),
    },
    {
      id: 'status',
      header: this.copy.status,
      cell: (row) => this.statusLabel(row.status),
      role: 'status',
      priority: 'primary',
      overflow: 'truncate',
      cellTemplate: this.status(),
    },
    {
      id: 'owner',
      header: this.copy.owner,
      cell: (row) => row.owner_membership_id ?? this.copy.unassigned,
      role: 'identity',
      kind: 'identifier',
      priority: 'secondary',
      overflow: 'truncate',
      cellTemplate: this.owner(),
    },
  ]);
}
