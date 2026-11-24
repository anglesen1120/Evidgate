import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  Injector,
  input,
  linkedSignal,
  signal,
  TemplateRef,
  viewChild,
  viewChildren,
} from '@angular/core';
import type { ErrorEnvelopeDetail, ImportResponse, ImportRowDto } from '@evidgate/sdk';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import { DataTablePagerComponent } from '../../../shared/ui/data-table/data-table-pager.component';
import type {
  DataTableColumn,
  DataTableCellContext,
} from '../../../shared/ui/data-table/data-table-column';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { IMPORT_COLUMNS, type LocalPreview } from './local-file-preview';
import {
  IMPEX_COPY,
  IMPORT_FIELD_LABELS,
  importReason,
  importProblemTarget,
  safeImportPath,
} from './import-export.copy';

type ImportColumn = (typeof IMPORT_COLUMNS)[number];
interface PreviewRow {
  readonly index: number;
  readonly server?: ImportRowDto;
  readonly cells: Readonly<Record<ImportColumn, string>>;
  readonly errors: ReadonlyMap<string, string>;
  readonly status: string;
}

@Component({
  selector: 'app-import-preview',
  imports: [DataTableComponent, DataTableValueComponent, DataTablePagerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    @for (field of fields; track field) {
      <ng-template #fieldCell let-row let-stacked="stacked">
        <div
          [id]="'import-row-' + row.index + '-' + field"
          tabindex="-1"
          [attr.aria-label]="labels[field]"
          [attr.aria-describedby]="
            row.errors.has(field) ? 'import-row-' + row.index + '-' + field + '-error' : null
          "
          class="grid min-w-0 gap-(--eg-space-2) focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
        >
          @if (fixedRows() && !stacked) {
            <app-data-table-value
              [value]="
                row.cells[field] + (row.errors.has(field) ? ' — ' + row.errors.get(field) : '')
              "
              [fixedRow]="true"
            />
            @if (row.errors.get(field); as error) {
              <span [id]="'import-row-' + row.index + '-' + field + '-error'" class="sr-only">{{
                error
              }}</span>
            }
          } @else {
            <app-data-table-value [value]="row.cells[field]" />
            @if (row.errors.get(field); as error) {
              <p
                [id]="'import-row-' + row.index + '-' + field + '-error'"
                class="m-0 text-sm font-medium whitespace-pre-wrap text-(--eg-danger-fg) wrap-anywhere"
              >
                {{ error }}
              </p>
            }
          }
        </div>
      </ng-template>
    }
    <ng-template #statusCell let-row let-stacked="stacked">
      <span
        [id]="'import-row-' + row.index + '-status'"
        tabindex="-1"
        class="block min-w-0 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
      >
        <app-data-table-value [value]="row.status" [fixedRow]="fixedRows() && !stacked" />
      </span>
    </ng-template>
    <app-data-table
      [busy]="fetching()"
      [rows]="visibleRows()"
      [columns]="columns()"
      [rowId]="rowId"
      [caption]="copy.previewTitle"
      [fixedHeightCells]="true"
    />
    <app-data-table-pager
      [page]="page()"
      [pageSize]="pageSize()"
      [total]="rows().length"
      [count]="visibleRows().length"
      [hasNext]="page() * pageSize() < rows().length"
      (previous)="move(-1)"
      (next)="move(1)"
      (pageSizeChange)="setPageSize($event)"
    />
  `,
})
export class ImportPreviewComponent {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  readonly fetching = input(false);
  readonly local = input<LocalPreview | null>(null);
  readonly report = input<ImportResponse | null>(null);
  readonly details = input<readonly ErrorEnvelopeDetail[]>([]);
  readonly applied = input(false);
  protected readonly copy = IMPEX_COPY;
  protected readonly fields = IMPORT_COLUMNS;
  protected readonly labels = IMPORT_FIELD_LABELS;
  private readonly table = viewChild.required(DataTableComponent<PreviewRow>);
  private readonly fieldCells =
    viewChildren<TemplateRef<DataTableCellContext<PreviewRow>>>('fieldCell');
  private readonly statusCell =
    viewChild.required<TemplateRef<DataTableCellContext<PreviewRow>>>('statusCell');
  protected readonly rowId = (row: PreviewRow): number => row.index;
  protected readonly pageSize = signal(20);
  protected readonly page = linkedSignal({
    source: () => ({ rows: this.rows(), size: this.pageSize() }),
    computation: () => 1,
  });
  protected readonly visibleRows = computed(() => {
    const offset = (this.page() - 1) * this.pageSize();
    return this.rows().slice(offset, offset + this.pageSize());
  });
  protected readonly fixedRows = computed(() => this.visibleRows().length > 200);
  protected readonly rows = computed<readonly PreviewRow[]>(() => {
    const reports = new Map(this.report()?.rows.map((row) => [row.index, row]));
    const locals = new Map(this.local()?.rows.map((row) => [row.index, row]));
    const problems = new Map<number, ErrorEnvelopeDetail[]>();
    for (const detail of this.details()) {
      if (typeof detail.index !== 'number') continue;
      const existing = problems.get(detail.index) ?? [];
      existing.push(detail);
      problems.set(detail.index, existing);
    }
    const indices = new Set([...locals.keys(), ...reports.keys(), ...problems.keys()]);
    return [...indices]
      .sort((a, b) => a - b)
      .map((index) => {
        const local = locals.get(index);
        const server = reports.get(index);
        const errors = new Map<string, string>();
        for (const issue of problems.get(index) ?? []) {
          const field = safeImportPath(issue.path).slice(1);
          errors.set(
            field,
            [errors.get(field), importReason(issue.reason)].filter(Boolean).join('; '),
          );
        }
        const cells = Object.fromEntries(
          IMPORT_COLUMNS.map((column) => [
            column,
            errors.has(column)
              ? IMPEX_COPY.withheld
              : local?.cells[column] || IMPEX_COPY.notSupplied,
          ]),
        ) as Record<ImportColumn, string>;
        const outcomes = this.applied()
          ? {
              create: IMPEX_COPY.created,
              update: IMPEX_COPY.updated,
              restore: IMPEX_COPY.restored,
              unchanged: IMPEX_COPY.unchanged,
            }
          : {
              create: IMPEX_COPY.wouldCreate,
              update: IMPEX_COPY.wouldUpdate,
              restore: IMPEX_COPY.wouldRestore,
              unchanged: IMPEX_COPY.wouldUnchanged,
            };
        return {
          index,
          ...(server ? { server } : {}),
          cells,
          errors,
          status: errors.size
            ? IMPEX_COPY.errors
            : server
              ? outcomes[server.outcome]
              : IMPEX_COPY.unverified,
        };
      });
  });
  protected readonly columns = computed<readonly DataTableColumn<PreviewRow>[]>(() => [
    { id: 'index', header: this.copy.row, cell: (row) => row.index, role: 'number', align: 'end' },
    {
      id: 'identity',
      header: this.copy.identity,
      cell: (row) =>
        row.server
          ? [row.server.kind, row.server.key, row.server.code].filter(Boolean).join(' · ')
          : this.copy.unverified,
      role: 'identity',
    },
    ...IMPORT_COLUMNS.map((column, index): DataTableColumn<PreviewRow> => {
      const template = this.fieldCells()[index];
      return {
        id: column,
        header: `${IMPORT_FIELD_LABELS[column]} (${column})`,
        cell: (row) => row.cells[column],
        role: 'text',
        ...(template ? { cellTemplate: template } : {}),
      };
    }),
    {
      id: 'status',
      header: this.copy.status,
      cell: (row) => row.status,
      role: 'text',
      cellTemplate: this.statusCell(),
    },
  ]);

  protected move(delta: number): void {
    const next = this.page() + delta;
    if (next >= 1 && next <= Math.ceil(this.rows().length / this.pageSize())) this.page.set(next);
  }

  protected setPageSize(size: number): void {
    if ([20, 50, 100].includes(size)) this.pageSize.set(size);
  }

  reveal(index: number, path?: string): void {
    const position = this.rows().findIndex((row) => row.index === index);
    if (position < 0) return;
    this.page.set(Math.floor(position / this.pageSize()) + 1);
    afterNextRender(
      () => {
        this.table().revealRow(position % this.pageSize());
        this.document.getElementById(importProblemTarget(index, path))?.focus();
      },
      { injector: this.injector },
    );
  }
}
