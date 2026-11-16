/** CSS budgets shared by native Helm and ordinary DataTable columns. */
export interface DataTableColumnWidth {
  readonly min?: string;
  /** `auto` receives spare room; `1%` keeps a utility column intrinsic. */
  readonly preferred?: string;
  readonly max?: string;
}
