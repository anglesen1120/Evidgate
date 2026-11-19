// Focus preservation across a table re-layout: a narrow/viewport switch re-renders rows, and the
// visitor's place is held as identifiers (row id, column id, control key) rather than an element
// reference. Identifiers are compared as data and never interpolated into a selector.

export interface DataTableLayoutFocus {
  readonly source: HTMLElement;
  readonly rowId: string;
  readonly columnId: string | null;
  readonly control: string;
  readonly index: number;
  readonly rowIndex: number;
}

const ROW_CONTROL_SELECTOR = 'a[href],button,input,select,textarea,summary,[tabindex]';

function controlKey(control: HTMLElement): string {
  const kind =
    control.getAttribute('role') ??
    (control.tagName === 'A' ? 'link' : control.tagName.toLowerCase());
  const action =
    kind === 'link'
      ? control.getAttribute('href')
      : (control.getAttribute('aria-label') ?? control.textContent?.trim());
  return `${kind}:${action ?? ''}`;
}

/** The focus anchor the visitor held before a re-layout, or `undefined` for no anchor. */
export function captureTableLayoutFocus<T>(
  document: Document,
  scroll: HTMLElement,
  rows: readonly T[],
  rowIdOf: (row: T) => string | number,
): DataTableLayoutFocus | undefined {
  const source = document.activeElement;
  if (!(source instanceof HTMLElement) || !scroll.contains(source)) {
    return undefined;
  }
  const cell = source.closest<HTMLElement>('app-data-table-cell[data-column-id]');
  const row = source.closest<HTMLElement>('[data-row-id]');
  const rowId = row?.dataset['rowId'];
  const columnId = cell?.dataset['columnId'];
  if (rowId === undefined || (!cell && source !== row)) return undefined;
  const rowIndex = rows.findIndex((candidate) => String(rowIdOf(candidate)) === rowId);
  if (rowIndex < 0) return undefined;
  if (source === row) return { source, rowId, columnId: null, control: '', index: 0, rowIndex };
  if (!cell || columnId === undefined) return undefined;
  const control = controlKey(source);
  let index = 0;
  for (const candidate of cell.querySelectorAll<HTMLElement>(ROW_CONTROL_SELECTOR)) {
    if (candidate === source) return { source, rowId, columnId, control, index, rowIndex };
    if (controlKey(candidate) === control) index++;
  }
  return undefined;
}

export interface RestoreLayoutFocusOptions {
  readonly virtual: boolean;
  readonly rowExists: (rowId: string) => boolean;
  /** Releases the anchor at exactly the points the original place holder gave it up. */
  readonly clear: () => void;
}

/**
 * Returns focus to the anchored control once its row is rendered. The anchor is released when the
 * visitor has moved focus elsewhere or when the row no longer exists.
 */
export function restoreTableLayoutFocus(
  document: Document,
  scroll: HTMLElement,
  anchor: DataTableLayoutFocus,
  options: RestoreLayoutFocusOptions,
): void {
  const active = document.activeElement;
  if (
    anchor.source.isConnected ||
    (active instanceof HTMLElement &&
      active !== document.body &&
      active !== anchor.source &&
      active.isConnected)
  ) {
    options.clear();
    return;
  }
  for (const cell of scroll.querySelectorAll<HTMLElement>(
    anchor.columnId === null ? '[data-row-id]' : 'app-data-table-cell[data-column-id]',
  )) {
    if (
      (anchor.columnId !== null && cell.dataset['columnId'] !== anchor.columnId) ||
      cell.closest<HTMLElement>('[data-row-id]')?.dataset['rowId'] !== anchor.rowId
    )
      continue;
    if (anchor.columnId === null) {
      options.clear();
      cell.tabIndex = -1;
      cell.focus();
      return;
    }
    let index = anchor.index;
    for (const control of cell.querySelectorAll<HTMLElement>(ROW_CONTROL_SELECTOR)) {
      if (controlKey(control) !== anchor.control || index-- !== 0) continue;
      options.clear();
      if (control.tabIndex >= 0 && !control.matches(':disabled,[aria-disabled="true"]')) {
        control.focus();
      }
      return;
    }
    options.clear();
    return;
  }
  if (!options.virtual || !options.rowExists(anchor.rowId)) {
    options.clear();
  }
}
