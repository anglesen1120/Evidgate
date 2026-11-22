import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  output,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';
import { StatusPillComponent } from '../../../shared/ui/chips';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../shared/ui/data-table/data-table-column';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { mappingCopy } from './mapping-resolution.copy';
import type { MappingRowViewModel } from './mapping-resolution.view-model';

type Cell = TemplateRef<DataTableCellContext<MappingRowViewModel>>;

@Component({
  selector: 'app-mapping-queue-table',
  imports: [DataTableComponent, StatusPillComponent, HlmButton, HlmIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'block min-w-0 [&_th]:wrap-normal [&_th]:break-normal [&_tr[data-row-id]]:h-(--eg-space-11) [&_[data-row-id]:has([aria-current=true])]:bg-(--eg-accent-soft)',
  },
  template: `
    <app-data-table
      [caption]="copy.records"
      [rows]="rows()"
      [rowId]="rowId"
      [columns]="columns()"
      [flow]="'page'"
      [fitToContainer]="true"
    />
    <ng-template #identity let-row>
      <div
        data-mapping-identity
        class="flex min-w-0 items-start gap-2 border-s-2 border-transparent ps-2 wrap-anywhere aria-[current=true]:border-(--eg-accent)"
        [attr.aria-current]="selectedId() === row.id ? 'true' : null"
      >
        <hlm-icon name="lucideActivity" class="mt-1 size-4 text-muted-foreground" />
        <div class="grid min-w-0 gap-1">
          <strong class="text-sm font-semibold"
            ><bdi>{{ display(row.name) }}</bdi></strong
          >
          <bdi class="font-mono text-xs text-muted-foreground">{{ display(row.id) }}</bdi>
        </div>
      </div>
    </ng-template>
    <ng-template #status let-row>
      <app-status-pill [tone]="'neutral'" [label]="stateLabel(row)" />
    </ng-template>
    <ng-template #actions let-row>
      <button
        hlmBtn
        #opener
        type="button"
        class="shrink-0 whitespace-nowrap"
        [variant]="'outline'"
        [size]="'sm'"
        [attr.aria-haspopup]="'dialog'"
        [attr.data-row]="row.id"
        (click)="view.emit({ row, opener })"
      >
        {{ copy.view
        }}<span class="sr-only"
          >&ngsp;<bdi>{{ display(row.name) }}</bdi></span
        >
      </button>
    </ng-template>
  `,
})
export class MappingQueueTableComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly rows = input.required<readonly MappingRowViewModel[]>();
  readonly selectedId = input<string>();
  readonly view = output<{ row: MappingRowViewModel; opener: HTMLButtonElement }>();
  protected readonly copy = mappingCopy;
  protected readonly display = escapeFormatChars;
  protected readonly stateLabel = (row: MappingRowViewModel) => this.copy[row.state];
  protected readonly rowId = (row: MappingRowViewModel) => row.id;
  private readonly identity = viewChild.required<Cell>('identity');
  private readonly status = viewChild.required<Cell>('status');
  private readonly actions = viewChild.required<Cell>('actions');
  protected readonly columns = computed<readonly DataTableColumn<MappingRowViewModel>[]>(() => [
    {
      id: 'identity',
      header: this.copy.identity,
      role: 'identity',
      priority: 'primary',
      cell: (row) => row.name,
      cellTemplate: this.identity(),
    },
    {
      id: 'status',
      header: this.copy.state,
      role: 'status',
      cell: this.stateLabel,
      cellTemplate: this.status(),
    },
    {
      id: 'candidates',
      header: this.copy.candidateCases,
      role: 'number',
      width: { min: '12ch', preferred: '12ch' },
      cell: (row) => row.candidates.length,
    },
    {
      id: 'actions',
      header: this.copy.actions,
      role: 'actions',
      width: { min: '9ch', preferred: '9ch' },
      cell: () => this.copy.view,
      cellTemplate: this.actions(),
    },
  ]);

  /** The shared table can replace the original button when the container reflows. */
  focusView(id: string): boolean {
    const buttons = this.host.nativeElement.querySelectorAll<HTMLButtonElement>('button[data-row]');
    const button = Array.from(buttons).find((item) => item.dataset['row'] === id) ?? buttons[0];
    button?.focus();
    return !!button;
  }
}
