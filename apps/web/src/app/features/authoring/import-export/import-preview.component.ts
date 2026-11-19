import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  Injector,
  input,
  TemplateRef,
  viewChild,
} from '@angular/core';
import type { ErrorEnvelopeDetail, ImportResponse, ImportRowDto } from '@evidgate/sdk';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import type {
  DataTableColumn,
  DataTableCellContext,
} from '../../../shared/ui/data-table/data-table-column';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { IMPORT_COLUMNS, type LocalPreview, type LocalPreviewRow } from './local-file-preview';
import { IMPEX_COPY, importReason, safeImportPath } from './import-export.copy';

interface PreviewRow {
  readonly index: number;
  readonly local?: LocalPreviewRow;
  readonly server?: ImportRowDto;
  readonly problems: readonly ErrorEnvelopeDetail[];
  readonly content: string;
  readonly status: string;
}

@Component({
  selector: 'app-import-preview',
  imports: [DataTableComponent, DataTableValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <ng-template #contentCell let-row>
      <span [attr.aria-describedby]="row.problems.length ? 'import-row-' + row.index : null">
        <app-data-table-value [value]="row.content" [fixedRow]="true" />
      </span>
    </ng-template>
    <ng-template #statusCell let-row>
      <span
        [id]="'import-row-' + row.index"
        tabindex="-1"
        class="block min-w-0 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
      >
        <app-data-table-value [value]="row.status" [fixedRow]="true" />
      </span>
    </ng-template>
    <app-data-table
      [rows]="rows()"
      [columns]="columns()"
      [rowId]="rowId"
      [caption]="copy.previewTitle"
      [fitToContainer]="true"
      [fixedHeightCells]="true"
    />
  `,
})
export class ImportPreviewComponent {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  readonly local = input<LocalPreview | null>(null);
  readonly report = input<ImportResponse | null>(null);
  readonly details = input<readonly ErrorEnvelopeDetail[]>([]);
  protected readonly copy = IMPEX_COPY;
  private readonly table = viewChild.required(DataTableComponent<PreviewRow>);
  private readonly contentCell =
    viewChild.required<TemplateRef<DataTableCellContext<PreviewRow>>>('contentCell');
  private readonly statusCell =
    viewChild.required<TemplateRef<DataTableCellContext<PreviewRow>>>('statusCell');
  protected readonly rowId = (row: PreviewRow): number => row.index;
  protected readonly rows = computed<readonly PreviewRow[]>(() => {
    const localRows = this.local()?.rows ?? [];
    const reports = new Map(this.report()?.rows.map((row) => [row.index, row]));
    const locals = new Map(localRows.map((row) => [row.index, row]));
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
        const local = locals.get(index),
          server = reports.get(index),
          issues = problems.get(index) ?? [];
        const content = local
          ? IMPORT_COLUMNS.map((column) => {
              const value = issues.some((issue) => issue.path === '/' + column)
                ? IMPEX_COPY.withheld
                : local.cells[column];
              return value ? `${column}: ${value}` : '';
            })
              .filter(Boolean)
              .join('\n')
          : IMPEX_COPY.xlsx;
        const outcomes = {
          create: IMPEX_COPY.created,
          update: IMPEX_COPY.updated,
          restore: IMPEX_COPY.restored,
          unchanged: IMPEX_COPY.unchanged,
        };
        const status = issues.length
          ? issues
              .map((issue) => `${safeImportPath(issue.path)}: ${importReason(issue.reason)}`)
              .join('\n')
          : server
            ? outcomes[server.outcome]
            : IMPEX_COPY.unverified;
        return {
          index,
          ...(local ? { local } : {}),
          ...(server ? { server } : {}),
          problems: issues,
          content,
          status,
        };
      });
  });
  protected readonly columns = computed<readonly DataTableColumn<PreviewRow>[]>(() => [
    { id: 'index', header: this.copy.row, cell: (row) => row.index, role: 'number', align: 'end' },
    {
      id: 'kind',
      header: this.copy.kind,
      cell: (row) => row.server?.kind ?? this.copy.unverified,
      role: 'status',
    },
    {
      id: 'identity',
      header: this.copy.identity,
      cell: (row) =>
        row.server
          ? [row.server.key, row.server.code].filter(Boolean).join(' · ')
          : this.copy.unverified,
      role: 'identity',
    },
    {
      id: 'content',
      header: this.copy.content,
      cell: (row) => row.content,
      role: 'text',
      cellTemplate: this.contentCell(),
    },
    {
      id: 'status',
      header: this.copy.status,
      cell: (row) => row.status,
      role: 'text',
      cellTemplate: this.statusCell(),
    },
  ]);

  reveal(index: number): void {
    const position = this.rows().findIndex((row) => row.index === index);
    this.table().revealRow(position);
    afterNextRender(() => this.document.getElementById('import-row-' + index)?.focus(), {
      injector: this.injector,
    });
  }
}
