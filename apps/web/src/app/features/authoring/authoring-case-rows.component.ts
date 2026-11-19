import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  input,
  linkedSignal,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { DecimalPipe } from '@angular/common';
import type { ManualCaseListItemDto } from '@evidgate/sdk';
import type { ListRange } from '@angular/cdk/collections';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { StatusPillComponent } from '../../shared/ui/chips/status-pill.component';
import { RevisionChipComponent } from '../../shared/ui/chips/revision-chip.component';
import { DataTableComponent } from '../../shared/ui/data-table/data-table.component';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../shared/ui/data-table/data-table-column';
import { DataTableValueComponent } from '../../shared/ui/data-table/data-table-value.component';
import { CopyableIdentifierComponent } from '../../shared/ui/values/copyable-identifier.component';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { CASE_TABLE_COPY } from './authoring-case-table.copy';

type CaseCell = TemplateRef<DataTableCellContext<ManualCaseListItemDto>>;
@Component({
  selector: 'app-authoring-case-rows',
  imports: [
    DecimalPipe,
    HlmButton,
    HlmCheckbox,
    DataTableComponent,
    DataTableValueComponent,
    CopyableIdentifierComponent,
    StatusPillComponent,
    RevisionChipComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './authoring-case-rows.component.html',
  styleUrl: './authoring-case-rows.component.css',
  host: { class: 'block min-w-0', '(focusin)': 'rememberFocus($event)' },
})
export class AuthoringCaseRowsComponent {
  private readonly viewport = inject(NarrowMutationPolicy);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly rows = input.required<readonly ManualCaseListItemDto[]>();
  protected readonly copy = CASE_TABLE_COPY;
  protected readonly desktop = this.viewport.canMutate;
  private readonly table = viewChild(DataTableComponent<ManualCaseListItemDto>);
  private readonly renderedRange = signal<ListRange | null>(null);
  private focusedSelection: HTMLElement | null = null;
  protected readonly virtual = computed(() => this.table()?.virtual() ?? false);
  private readonly selectionCell = viewChild.required<CaseCell>('selectionCell');
  private readonly codeCell = viewChild.required<CaseCell>('codeCell');
  private readonly idCell = viewChild.required<CaseCell>('idCell');
  private readonly titleCell = viewChild.required<CaseCell>('titleCell');
  private readonly parentCell = viewChild.required<CaseCell>('parentCell');
  private readonly priorityCell = viewChild.required<CaseCell>('priorityCell');
  private readonly stateCell = viewChild.required<CaseCell>('stateCell');
  private readonly coverageCell = viewChild.required<CaseCell>('coverageCell');
  private readonly versionCell = viewChild.required<CaseCell>('versionCell');
  private readonly pendingCell = viewChild.required<CaseCell>('pendingCell');
  private readonly focusId = linkedSignal(() => {
    this.rows();
    return null as string | null;
  });
  protected readonly visibleRows = computed(() => {
    const rows = this.rows();
    const table = this.table();
    if (!table) return rows.slice(0, 200);
    const range = this.renderedRange();
    const start = table.virtual() ? (range?.start ?? 0) : table.windowStart();
    const end = table.virtual() ? (range?.end ?? 0) : start + 200;
    return rows.slice(start, end);
  });
  protected readonly selected = linkedSignal(() => {
    // New result rows and a new visible window reset the selection; a viewport-only narrow or
    // widen transition must not, and narrow actions stay refused by `selectRow`/`selectVisible`.
    this.rows();
    this.visibleRows();
    return new Set<string>();
  });
  protected readonly selectedCount = computed(
    () => this.visibleRows().filter((row) => this.selected().has(row.case_id)).length,
  );
  protected readonly allSelected = computed(
    () => this.visibleRows().length > 0 && this.selectedCount() === this.visibleRows().length,
  );
  protected readonly mixed = computed(() => this.selectedCount() > 0 && !this.allSelected());
  protected readonly rowId = (row: ManualCaseListItemDto) => row.case_id;
  protected readonly selectName = (row: ManualCaseListItemDto) =>
    $localize`:@@authoring.case-table.select-case:Select case ${row.code}:code:`;
  protected readonly priorityLabel = (row: ManualCaseListItemDto) => this.copy[row.priority];
  protected readonly stateLabel = (row: ManualCaseListItemDto) => this.copy[row.state];
  protected readonly columns = computed<readonly DataTableColumn<ManualCaseListItemDto>[]>(() => [
    ...(this.desktop()
      ? [
          {
            id: 'select',
            header: this.copy.selection,
            cell: () => '',
            role: 'actions' as const,
            priority: 'primary' as const,
            mutates: true,
            virtualWidth: 'var(--eg-control-touch)',
            cellTemplate: this.selectionCell(),
          },
        ]
      : []),
    {
      id: 'code',
      header: this.copy.code,
      cell: (row) => row.code,
      role: 'identity',
      priority: 'primary',
      // Own column, so the shared primary-identity budget (flexing subject) sizes the code
      // instead of sharing one cell with a 36-character UUID.
      virtualWidth: 'calc(16ch + var(--eg-space-4) * 2)',
      cellTemplate: this.codeCell(),
    },
    {
      id: 'id',
      header: this.copy.id,
      cell: (row) => row.case_id,
      role: 'identity',
      kind: 'identifier',
      priority: 'secondary',
      virtualWidth: 'calc(28ch + var(--eg-space-4) * 2)',
      cellTemplate: this.idCell(),
    },
    {
      id: 'title',
      header: this.copy.title,
      cell: (row) => row.title,
      role: 'text',
      priority: 'primary',
      cellTemplate: this.titleCell(),
    },
    {
      id: 'item',
      header: this.copy.item,
      cell: (row) => row.checklist_item_id,
      role: 'identity',
      cellTemplate: this.parentCell(),
    },
    {
      id: 'priority',
      header: this.copy.priority,
      cell: (row) => this.copy[row.priority],
      role: 'status',
      cellTemplate: this.priorityCell(),
    },
    {
      id: 'state',
      header: this.copy.state,
      cell: (row) => this.copy[row.state],
      role: 'status',
      cellTemplate: this.stateCell(),
    },
    { id: 'source', header: this.copy.source, cell: () => this.copy.unavailable, role: 'text' },
    {
      id: 'coverage',
      header: this.copy.coverage,
      cell: (row) => (row.linked_live_count > 0 ? this.copy.covered : this.copy.uncovered),
      role: 'status',
      cellTemplate: this.coverageCell(),
    },
    { id: 'owner', header: this.copy.owner, cell: () => this.copy.unavailable, role: 'text' },
    {
      id: 'version',
      header: this.copy.version,
      cell: (row) => row.version,
      role: 'identity',
      cellTemplate: this.versionCell(),
    },
    {
      id: 'pending',
      header: this.copy.pending,
      cell: () => '—',
      role: 'number',
      cellTemplate: this.pendingCell(),
    },
  ]);

  constructor() {
    effect((cleanup) => {
      const viewport = this.table()?.viewport();
      this.renderedRange.set(null);
      if (!viewport) return;
      const subscription = viewport.renderedRangeStream.subscribe((range) =>
        this.renderedRange.set(range),
      );
      cleanup(() => subscription.unsubscribe());
    });
    afterRenderEffect(() => {
      const id = this.focusId();
      const table = this.table();
      table?.windowStart();
      table?.viewport();
      if (!id || !table) return;
      const target = [
        ...this.host.nativeElement.querySelectorAll<HTMLElement>('[data-row-id]'),
      ].find((element) => element.dataset['rowId'] === id);
      if (target) {
        target.tabIndex = -1;
        target.focus();
        this.focusId.set(null);
        return;
      }
      const index = this.rows().findIndex((row) => row.case_id === id);
      // A row that no longer exists must not hold the request: a later result carrying that id
      // would otherwise pull focus away from wherever the visitor had moved it.
      if (index < 0) {
        this.focusId.set(null);
        return;
      }
      table.revealRow(index);
    });
    afterRenderEffect(() => {
      if (this.desktop() || !this.focusedSelection) return;
      const document = this.host.nativeElement.ownerDocument;
      if (
        document.activeElement === document.body ||
        document.activeElement === this.focusedSelection
      ) {
        this.host.nativeElement.querySelector<HTMLElement>('[data-selection-notice]')?.focus();
      }
      this.focusedSelection = null;
    });
  }

  focusRow(id: string): void {
    this.focusId.set(id);
  }
  protected rememberFocus(event: FocusEvent): void {
    const target = event.target;
    // The export control disappears with the same viewport policy as row selection, so it hands
    // focus to the inspection notice too.
    this.focusedSelection =
      target instanceof HTMLElement &&
      target.closest('[data-case-select], [data-select-visible], [data-export-selection]')
        ? target
        : null;
  }
  protected selectRow(row: ManualCaseListItemDto, checked: boolean): void {
    if (
      !this.viewport.allowsMutation() ||
      !this.visibleRows().some((visible) => visible.case_id === row.case_id)
    )
      return;
    this.selected.update((selected) => {
      const next = new Set(selected);
      if (checked) next.add(row.case_id);
      else next.delete(row.case_id);
      return next;
    });
  }
  protected selectVisible(): void {
    if (!this.viewport.allowsMutation()) return;
    this.selected.set(
      this.allSelected() ? new Set() : new Set(this.visibleRows().map((row) => row.case_id)),
    );
  }
}
