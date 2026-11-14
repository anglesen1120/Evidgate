import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { CopyableIdentifierComponent } from '../values/copyable-identifier.component';
import type { ResolvedDataTableColumn } from './data-table-column';
import { DataTableValueComponent } from './data-table-value.component';

@Component({
  selector: 'app-data-table-cell',
  imports: [NgTemplateOutlet, CopyableIdentifierComponent, DataTableValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'eg-table-value block min-w-0' },
  template: `
    @if (column().cellTemplate; as template) {
      <ng-container [ngTemplateOutlet]="template" [ngTemplateOutletContext]="context()" />
    } @else if (column().kind === 'identifier') {
      <app-copyable-identifier
        [value]="identifier()"
        [label]="column().header"
        [truncate]="!stacked()"
      />
    } @else {
      <app-data-table-value
        [value]="value()"
        [truncate]="!stacked() && (virtual() || column().overflow === 'truncate')"
        [fixedRow]="virtual()"
        [nowrap]="!stacked() && (column().role === 'number' || column().role === 'actions')"
      />
    }
  `,
})
export class DataTableCellComponent<T> {
  readonly column = input.required<ResolvedDataTableColumn<T>>();
  readonly row = input.required<T>();
  readonly stacked = input(false);
  readonly virtual = input(false);
  protected readonly value = computed(() => this.column().cell(this.row()));
  protected readonly identifier = computed(() => String(this.value()));
  protected readonly context = computed(() => ({
    $implicit: this.row(),
    value: this.value(),
    stacked: this.stacked(),
  }));
}
