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
import { RouterLink } from '@angular/router';
import type { ManualCaseListItemDto } from '@evidgate/sdk';
import type { ListRange } from '@angular/cdk/collections';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { StatusPillComponent } from '../../shared/ui/chips/status-pill.component';
import { RevisionChipComponent } from '../../shared/ui/chips/revision-chip.component';
import { DataTableComponent } from '../../shared/ui/data-table/data-table.component';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../shared/ui/data-table/data-table-column';
import { DataTableValueComponent } from '../../shared/ui/data-table/data-table-value.component';
import { RowActionComponent } from '../../shared/ui/data-table/row-action.component';
import { CopyValueButtonComponent } from '../../shared/ui/values/copy-value-button.component';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { escapeFormatChars } from '../../shared/ui/values/ref-name-display';
import { CASE_TABLE_COPY } from './authoring-case-table.copy';

type CaseCell = TemplateRef<DataTableCellContext<ManualCaseListItemDto>>;
const selectionTrack = 'calc(var(--eg-control-touch) + var(--eg-space-2) * 2)';
@Component({
  selector: 'app-authoring-case-rows',
  imports: [
    DecimalPipe,
    RouterLink,
    HlmButton,
    HlmCheckbox,
    HlmDialogImports,
    DataTableComponent,
    DataTableValueComponent,
    RowActionComponent,
    CopyValueButtonComponent,
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
  readonly editorScope = input<readonly string[] | null>(null);
  protected readonly copy = CASE_TABLE_COPY;
  protected readonly identifiers = $localize`:@@authoring.case-table.identifiers:Identifiers`;
  protected readonly inspected = linkedSignal<ManualCaseListItemDto | null>(() => {
    this.rows();
    return null;
  });
  protected readonly identifiersForRow = computed(() => {
    const row = this.inspected();
    return row
      ? [
          { label: this.copy.id, value: row.case_id },
          { label: this.copy.item, value: row.checklist_item_id },
          ...row.linked_case_ids.map((value) => ({ label: this.copy.links, value })),
        ]
      : [];
  });
  protected readonly desktop = this.viewport.canMutate;
  private readonly table = viewChild(DataTableComponent<ManualCaseListItemDto>);
  private readonly renderedRange = signal<ListRange | null>(null);
  private focusedSelection: HTMLElement | null = null;
  protected readonly virtual = computed(() => this.table()?.virtual() ?? false);
  protected readonly virtualThreshold = 200;
  protected readonly roomForFixedTracks = signal(false);
  private readonly bulk = computed(() => this.rows().length > this.virtualThreshold);
  private readonly selectionCell = viewChild.required<CaseCell>('selectionCell');
  private readonly codeCell = viewChild.required<CaseCell>('codeCell');
  private readonly priorityCell = viewChild.required<CaseCell>('priorityCell');
  private readonly coverageCell = viewChild.required<CaseCell>('coverageCell');
  private readonly viewCell = viewChild.required<CaseCell>('viewCell');
  private readonly focusId = linkedSignal(() => {
    this.rows();
    return null as string | null;
  });
  protected readonly visibleRows = computed(() => {
    const rows = this.rows();
    const table = this.table();
    if (!table) return rows.slice(0, this.virtualThreshold);
    const range = this.renderedRange();
    const start = table.virtual() ? (range?.start ?? 0) : table.windowStart();
    const end = table.virtual() ? (range?.end ?? 0) : start + this.virtualThreshold;
    return rows.slice(start, end);
  });
  protected readonly selected = linkedSignal<readonly ManualCaseListItemDto[], ReadonlySet<string>>(
    {
      source: this.rows,
      // A new result replaces the selection, but an appended page keeps it: the rows it selected
      // are still in the list, and selection is keyed by case id. Ids that left the result are
      // dropped, so a filter that removes a case never leaves an invisible pick behind.
      computation: (rows, previous) => {
        if (!previous) return new Set<string>();
        const present = new Set(rows.map((row) => row.case_id));
        const kept = [...previous.value].filter((id) => present.has(id));
        return kept.length === previous.value.size ? previous.value : new Set(kept);
      },
    },
  );
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
  protected readonly viewName = (row: ManualCaseListItemDto) =>
    $localize`:@@authoring.case-table.view-name:View ${escapeFormatChars(row.title)}:name:`;
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
            width: { min: selectionTrack, preferred: selectionTrack, max: selectionTrack },
            virtualWidth: selectionTrack,
            cellTemplate: this.selectionCell(),
          },
        ]
      : []),
    {
      id: 'code',
      header: this.copy.manualCase,
      cell: (row) => row.code,
      role: 'identity',
      priority: 'primary',
      virtualWidth: 'calc(40ch + var(--eg-space-4) * 2)',
      cellTemplate: this.codeCell(),
    },
    {
      id: 'priority',
      header: this.copy.priorityState,
      cell: (row) => this.copy[row.priority],
      role: 'status',
      width: { min: 'max-content' },
      cellTemplate: this.priorityCell(),
    },
    {
      id: 'coverage',
      header: this.copy.coverage,
      cell: (row) => (row.linked_live_count > 0 ? this.copy.covered : this.copy.uncovered),
      role: 'status',
      width: { min: 'max-content', max: 'none' },
      virtualWidth: 'calc(32ch + var(--eg-space-4) * 2)',
      cellTemplate: this.coverageCell(),
    },
    {
      id: 'actions',
      header: this.copy.actions,
      cell: () => this.copy.view,
      role: 'actions',
      priority: 'primary',
      width: { min: 'max-content' },
      cellTemplate: this.viewCell(),
    },
  ]);

  constructor() {
    afterRenderEffect((cleanup) => {
      if (!this.bulk() || typeof ResizeObserver === 'undefined') return;
      const host = this.host.nativeElement;
      const measure = () =>
        this.roomForFixedTracks.set(
          getComputedStyle(host).getPropertyValue('--case-virtual').trim() === '1',
        );
      measure();
      const observer = new ResizeObserver(measure);
      observer.observe(host);
      cleanup(() => observer.disconnect());
    });
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
    this.focusedSelection =
      target instanceof HTMLElement && target.closest('[data-case-select], [data-select-visible]')
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
