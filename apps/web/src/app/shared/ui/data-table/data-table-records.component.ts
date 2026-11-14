import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { DataTableCellComponent } from './data-table-cell.component';
import { resolveTableColumns } from './data-table-column';
import type { DataTableColumn } from './data-table-column';

/** Read-only facts retain their labels and full values, rather than hiding low-priority columns. */
@Component({
  selector: 'app-data-table-records',
  imports: [DataTableCellComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <ol class="eg-table-records" [attr.aria-label]="caption()">
      @for (row of rows(); track rowId()(row)) {
        <li class="eg-table-record" [attr.data-row-id]="rowId()(row)">
          <dl>
            @for (column of inspectionColumns(); track column.id) {
              <div class="eg-table-fact" [attr.data-column-priority]="column.priority">
                <dt>{{ column.header }}</dt>
                <dd>
                  <app-data-table-cell [column]="column" [row]="row" [stacked]="true" />
                </dd>
              </div>
            }
          </dl>
        </li>
      } @empty {
        <li class="eg-table-record" i18n="@@shared.data-table.no-rows">No rows</li>
      }
    </ol>
  `,
})
export class DataTableRecordsComponent<T> {
  readonly columns = input.required<readonly DataTableColumn<T>[]>();
  readonly rows = input.required<readonly T[]>();
  readonly rowId = input.required<(row: T) => string | number>();
  readonly caption = input.required<string>();
  protected readonly inspectionColumns = computed(() => {
    const columns = resolveTableColumns(this.columns()).filter((column) => !column.mutates);
    return [
      ...columns.filter((column) => column.priority === 'primary'),
      ...columns.filter((column) => column.priority !== 'primary'),
    ];
  });
}
