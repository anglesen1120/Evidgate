import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import { NarrowMutationPolicy } from '../narrow/narrow-mutation-policy';
import { DataTableCellComponent } from './data-table-cell.component';
import { resolveTableColumns } from './data-table-column';
import type { DataTableColumn } from './data-table-column';

/** Container stacking preserves facts; only the viewport policy can hide mutation controls. */
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
  private readonly viewport = inject(NarrowMutationPolicy);
  protected readonly inspectionColumns = computed(() => {
    const mutationLocked = this.viewport.narrow();
    const columns = resolveTableColumns(this.columns()).filter(
      (column) => !mutationLocked || !column.mutates,
    );
    return [
      ...columns.filter((column) => column.priority === 'primary'),
      ...columns.filter((column) => column.priority !== 'primary'),
    ];
  });
}
