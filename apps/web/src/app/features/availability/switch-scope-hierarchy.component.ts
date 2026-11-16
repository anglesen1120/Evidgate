import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import type { HealingSwitchResolutionView, HealingSwitchView } from '@evidgate/sdk';

import { SessionStore } from '../../core/session/session.store';
import { AbsoluteTimeComponent } from '../../shared/time/absolute-time.component';
import { DataTableComponent, type DataTableColumn } from '../../shared/ui/data-table';
import type { DataTableCellContext } from '../../shared/ui/data-table/data-table-column';
import { NumberValueComponent } from '../../shared/ui/values/number-value.component';
import { HEALING_SWITCH_COPY, modeLabel, scopeLabel } from './healing-switch.copy';
import type { SwitchScope } from './healing-switch.store';

interface ScopeFact {
  readonly scope: SwitchScope;
  readonly name: string;
  readonly row: HealingSwitchView | null;
}

@Component({
  selector: 'app-switch-scope-hierarchy',
  imports: [DataTableComponent, AbsoluteTimeComponent, NumberValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <ng-template #scopeCell let-item>
      <div class="grid min-w-0 gap-(--eg-space-3)" [attr.data-healing-scope]="item.scope">
        <span>{{ item.name }}</span>
        @if (item.scope === 'system') {
          <p class="m-0 text-sm text-muted-foreground" data-healing-system-note>
            {{ copy.systemReadOnly }}
          </p>
        }
        @if (item.row === null) {
          <p class="m-0 text-sm text-muted-foreground" data-healing-no-row>{{ copy.noRow }}</p>
        }
      </div>
    </ng-template>
    <ng-template #timeCell let-item>
      @if (item.row; as stored) {
        <app-absolute-time [value]="stored.at" />
      } @else {
        {{ copy.notProvided }}
      }
    </ng-template>
    <ng-template #versionCell let-item>
      @if (item.row; as stored) {
        <app-number-value [value]="stored.version" />
      } @else {
        {{ copy.notProvided }}
      }
    </ng-template>
    <app-data-table
      [columns]="columns()"
      [rows]="rows()"
      [rowId]="rowId"
      [caption]="copy.hierarchyDescription"
    />
  `,
})
export class SwitchScopeHierarchy {
  readonly resolution = input.required<HealingSwitchResolutionView>();
  protected readonly copy = HEALING_SWITCH_COPY;
  private readonly session = inject(SessionStore);
  private readonly scopeCell = viewChild<TemplateRef<DataTableCellContext<ScopeFact>>>('scopeCell');
  private readonly timeCell = viewChild<TemplateRef<DataTableCellContext<ScopeFact>>>('timeCell');
  private readonly versionCell =
    viewChild<TemplateRef<DataTableCellContext<ScopeFact>>>('versionCell');
  protected readonly rowId = (item: ScopeFact): string => item.scope;
  protected readonly rows = computed<readonly ScopeFact[]>(() => {
    const resolution = this.resolution();
    const projectName = this.session.currentProject()?.name ?? '';
    return (['system', 'tenant', 'project'] as const).map((scope) => ({
      scope,
      name: scopeLabel(scope, projectName),
      row: resolution[scope],
    }));
  });
  protected readonly columns = computed<readonly DataTableColumn<ScopeFact>[]>(() => {
    const scopeCell = this.scopeCell();
    const timeCell = this.timeCell();
    const versionCell = this.versionCell();
    return [
      {
        id: 'scope',
        header: this.copy.scope,
        role: 'identity',
        priority: 'primary',
        overflow: 'wrap',
        cell: (item) => item.name,
        ...(scopeCell === undefined ? {} : { cellTemplate: scopeCell }),
      },
      {
        id: 'mode',
        header: this.copy.mode,
        role: 'status',
        overflow: 'wrap',
        cell: (item) => (item.row === null ? this.copy.notProvided : modeLabel(item.row.mode)),
      },
      {
        id: 'set-by',
        header: this.copy.setBy,
        role: 'text',
        overflow: 'wrap',
        cell: (item) => this.fieldValue(item, 'set_by'),
      },
      {
        id: 'reason',
        header: this.copy.reason,
        role: 'text',
        overflow: 'wrap',
        cell: (item) => this.fieldValue(item, 'reason'),
      },
      {
        id: 'changed-at',
        header: this.copy.changedAt,
        role: 'date',
        overflow: 'wrap',
        cell: (item) => item.row?.at ?? this.copy.notProvided,
        ...(timeCell === undefined ? {} : { cellTemplate: timeCell }),
      },
      {
        id: 'version',
        header: this.copy.version,
        role: 'number',
        cell: (item) => item.row?.version ?? this.copy.notProvided,
        ...(versionCell === undefined ? {} : { cellTemplate: versionCell }),
      },
    ];
  });

  private fieldValue(item: ScopeFact, key: 'set_by' | 'reason'): string {
    if (item.row === null) return this.copy.notProvided;
    return item.row[key] ?? (item.scope === 'system' ? this.copy.redacted : this.copy.notProvided);
  }
}
