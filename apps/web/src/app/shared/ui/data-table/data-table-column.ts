import type { TemplateRef } from '@angular/core';
import type {
  HlmTableColumnKind,
  HlmTableColumnOverflow,
  HlmTableColumnPriority,
  HlmTableColumnRole,
} from '@evidgate/ui/table';

export interface DataTableCellContext<T> {
  readonly $implicit: T;
  readonly value: string | number;
  readonly stacked: boolean;
}

export interface DataTableColumn<T> {
  readonly id: string;
  /** Header and values are localized by the caller; identifiers are never credentials. */
  readonly header: string;
  readonly cell: (row: T) => string | number;
  readonly role?: HlmTableColumnRole;
  readonly kind?: HlmTableColumnKind;
  readonly priority?: HlmTableColumnPriority;
  readonly overflow?: HlmTableColumnOverflow;
  readonly cellTemplate?: TemplateRef<DataTableCellContext<T>>;
  readonly align?: 'start' | 'end';
  readonly truncate?: boolean;
  readonly mutates?: boolean;
}

export interface ResolvedDataTableColumn<T> extends DataTableColumn<T> {
  readonly role: HlmTableColumnRole;
  readonly kind: HlmTableColumnKind;
  readonly priority: HlmTableColumnPriority;
  readonly overflow: HlmTableColumnOverflow;
  readonly virtualWidth: string | null;
}

const COLUMN_WIDTH: Readonly<Record<HlmTableColumnRole, number>> = {
  identity: 28,
  text: 32,
  status: 12,
  number: 8,
  date: 18,
  actions: 6,
};

export function resolveTableColumns<T>(
  columns: readonly DataTableColumn<T>[],
  rows: readonly T[] = [],
): readonly ResolvedDataTableColumn<T>[] {
  const subjectIndex = Math.max(
    0,
    columns.findIndex((column) => column.role === 'identity'),
  );
  return columns.map((column, index) => {
    let role = column.role;
    if (role === undefined) {
      if (column.align === 'end') role = 'number';
      else if (column.mutates) role = 'actions';
      else role = index === 0 ? 'identity' : 'text';
    }
    const priority =
      column.priority ??
      (index === subjectIndex || role === 'status' || role === 'actions' ? 'primary' : 'secondary');
    const kind = column.kind ?? 'name';
    const defaultOverflow = role === 'identity' || role === 'status' ? 'truncate' : 'wrap';
    const legacyOverflow = column.truncate ? 'truncate' : 'wrap';
    const overflow =
      column.overflow ?? (column.truncate === undefined ? defaultOverflow : legacyOverflow);
    let width = kind === 'email' || kind === 'url' ? 36 : COLUMN_WIDTH[role];
    if (role === 'number' || role === 'status' || role === 'actions') {
      width = Math.max(width, column.header.length);
      for (const row of rows) width = Math.max(width, String(column.cell(row)).length);
      if (role === 'status') width = Math.min(width, 24);
    }
    return {
      ...column,
      role,
      kind,
      priority,
      overflow,
      virtualWidth:
        priority === 'primary' && (role === 'identity' || role === 'text')
          ? null
          : `calc(${width}ch + var(--eg-space-4) * 2)`,
    };
  });
}
