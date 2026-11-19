import type { ApiError } from '../../../core/http/api-error';
import { IMPORT_BYTE_CAP, IMPORT_COLUMNS, type AuthoringFormat } from './local-file-preview';

export const AUTHORING_READ = 'authoring:read';
export const AUTHORING_WRITE = 'authoring:write';
export const IMPEX_COPY = {
  title: $localize`:@@authoring.import-export.title:Import and export`,
  description: $localize`:@@authoring.import-export.description:Preview your file before confirming an import. Export only the exact scope shown.`,
  back: $localize`:@@authoring.import-export.back:Back to authoring`,
  context: $localize`:@@authoring.import-export.context:Project context`,
  project: $localize`:@@authoring.import-export.project:Project`,
  prefix: $localize`:@@authoring.import-export.prefix:Project code prefix`,
  importTab: $localize`:@@authoring.import-export.import-tab:Import`,
  exportTab: $localize`:@@authoring.import-export.export-tab:Export`,
  intake: $localize`:@@authoring.import-export.intake:File and target`,
  file: $localize`:@@authoring.import-export.file:Source file`,
  limits: $localize`:@@authoring.import-export.file-limits:CSV, JSON or XLSX. The server accepts at most 5 MiB and 5,000 data rows. File selection is not validation.`,
  format: $localize`:@@authoring.import-export.format:Format`,
  preview: $localize`:@@authoring.import-export.preview:Preview without importing`,
  previewTitle: $localize`:@@authoring.import-export.preview-title:Preview rows`,
  fromFile: $localize`:@@authoring.import-export.from-file:From your file, not validated. Row identity, status and totals come only from the server.`,
  noFile: $localize`:@@authoring.import-export.no-file:No file has been selected for import.`,
  xlsxPreview: $localize`:@@authoring.import-export.xlsx-preview:Workbook cells are not read in this browser. Preview sends the original XLSX file for a server dry run; only the server report is shown.`,
  xlsxTooLarge: $localize`:@@authoring.import-export.xlsx-too-large:The XLSX file exceeds 5 MiB. Choose a smaller workbook and preview it again. No import request was sent.`,
  formatMismatch: $localize`:@@authoring.import-export.format-mismatch:An .xlsx file must use the XLSX format. Choose XLSX and preview it again. No import request was sent.`,
  localUnreadable: $localize`:@@authoring.import-export.local-unreadable:Local preview could not read this file. The original file can still be sent for server validation.`,
  invalidUtf8: $localize`:@@authoring.import-export.invalid-utf8:CSV files must use valid UTF-8 encoding. Save this file as UTF-8 and preview it again. No import request was sent.`,
  displayCap: $localize`:@@authoring.import-export.display-cap:Local display stops at the server's 5,000-row cap. No rows are removed from the upload; the server validates the original file.`,
  busy: $localize`:@@authoring.import-export.busy:Previewing rows; no version has been created.`,
  cancelPreview: $localize`:@@authoring.import-export.cancel-preview:Cancel preview`,
  row: $localize`:@@authoring.import-export.row:Source row`,
  kind: $localize`:@@authoring.import-export.kind:Kind`,
  identity: $localize`:@@authoring.import-export.identity:Server identity`,
  content: $localize`:@@authoring.import-export.content:File content`,
  status: $localize`:@@authoring.import-export.status:Server outcome or reported problem`,
  unverified: $localize`:@@authoring.import-export.unverified:Unverified`,
  withheld: $localize`:@@authoring.import-export.withheld:Refused cell text is not displayed.`,
  errors: $localize`:@@authoring.import-export.errors:Reported problems`,
  invalid: $localize`:@@authoring.import-export.invalid:The file was refused. No rows were written. Remaining rows are unverified; at most 100 problems are reported. Fix the original file and preview it again.`,
  confirmTitle: $localize`:@@authoring.import-export.confirm-title:Confirm the validated import`,
  confirm: $localize`:@@authoring.import-export.confirm:Confirm import`,
  confirmHint: $localize`:@@authoring.import-export.confirm-hint:Existing live codes update cases; deleted codes restore them; empty codes allocate a new number. No partial import is possible.`,
  blocked: $localize`:@@authoring.import-export.blocked:Preview a valid file before confirming. Nothing is written before explicit confirmation.`,
  denied: $localize`:@@authoring.import-export.denied:You do not have permission to import into this project. The file stays readable; no new version was created.`,
  success: $localize`:@@authoring.import-export.success:Import confirmed. The server returned this batch and these outcome totals.`,
  noOp: $localize`:@@authoring.import-export.no-op:Nothing to import. No changes; no new version was created.`,
  batch: $localize`:@@authoring.import-export.batch:Import batch`,
  created: $localize`:@@authoring.import-export.created:Created`,
  updated: $localize`:@@authoring.import-export.updated:Updated`,
  restored: $localize`:@@authoring.import-export.restored:Restored`,
  unchanged: $localize`:@@authoring.import-export.unchanged:Unchanged`,
  wouldCreate: $localize`:@@authoring.import-export.would-create:Would create`,
  wouldUpdate: $localize`:@@authoring.import-export.would-update:Would update`,
  wouldRestore: $localize`:@@authoring.import-export.would-restore:Would restore`,
  wouldUnchanged: $localize`:@@authoring.import-export.would-unchanged:Would remain unchanged`,
  previewComplete: $localize`:@@authoring.import-export.preview-complete:Preview complete — not applied. Review the validated scope before confirming.`,
  inspected: $localize`:@@authoring.import-export.inspected:Inspected rows`,
  snapshot: $localize`:@@authoring.import-export.snapshot:Selected file and format`,
  headerGuide: $localize`:@@authoring.import-export.header-guide:Correct the CSV header to these thirteen contract columns, in this order:`,
  limit: $localize`:@@authoring.import-export.limit:Server limit`,
  actual: $localize`:@@authoring.import-export.actual:Measured value`,
  bytes: $localize`:@@authoring.import-export.bytes:bytes`,
  dataRows: $localize`:@@authoring.import-export.data-rows:data rows`,
  history: $localize`:@@authoring.import-export.history:Authoring versions`,
  historyHint: $localize`:@@authoring.import-export.history-hint:Latest import versions in this project, up to 50 per page. Version numbers belong to each entity. Only this authorized history read confirms a version.`,
  historyFailed: $localize`:@@authoring.import-export.history-failed:Version history could not be loaded. The file and preview remain available.`,
  historyStale: $localize`:@@authoring.import-export.history-stale:Version history is stale because its refresh failed. The file and preview remain available.`,
  historyEmpty: $localize`:@@authoring.import-export.history-empty:No import versions were returned for this project.`,
  historyRefresh: $localize`:@@authoring.import-export.history-refresh:Refresh version history`,
  historyOlder: $localize`:@@authoring.import-export.history-older:Older import versions`,
  historyLatest: $localize`:@@authoring.import-export.history-latest:Latest import versions`,
  version: $localize`:@@authoring.import-export.version:Entity version`,
  entity: $localize`:@@authoring.import-export.entity:Entity`,
  source: $localize`:@@authoring.import-export.source:Source`,
  author: $localize`:@@authoring.import-export.author:Author`,
  reason: $localize`:@@authoring.import-export.reason:Reason`,
  notSupplied: $localize`:@@authoring.import-export.not-supplied:Not supplied`,
  changedAt: $localize`:@@authoring.import-export.changed-at:Created at`,
  change: $localize`:@@authoring.import-export.change:Change`,
  retry: $localize`:@@authoring.import-export.retry:Retry the same intent`,
  pending: $localize`:@@authoring.import-export.pending:Import is awaiting a server response.`,
  exportScope: $localize`:@@authoring.import-export.export-scope:Export scope`,
  projectScope: $localize`:@@authoring.import-export.project-scope:Whole project`,
  nodeScope: $localize`:@@authoring.import-export.node-scope:Selected node subtree`,
  selected: $localize`:@@authoring.import-export.selected:Export selected — unavailable`,
  filtered: $localize`:@@authoring.import-export.filtered:Current filtered set — unavailable`,
  selectionUnavailable: $localize`:@@authoring.import-export.selection-unavailable:Selection and filtered-set export are unavailable: this contract has no ids or filter parameter. Choose a node or the project explicitly; selection is never expanded automatically.`,
  node: $localize`:@@authoring.import-export.node:Selected node UUID`,
  noNode: $localize`:@@authoring.import-export.no-node:No node is selected. An empty scope is never expanded to the whole project.`,
  nodeInvalid: $localize`:@@authoring.import-export.node-invalid:Enter the exact node UUID, or choose the whole project explicitly.`,
  include: $localize`:@@authoring.import-export.include:Included kinds`,
  nodes: $localize`:@@authoring.import-export.nodes:Nodes`,
  items: $localize`:@@authoring.import-export.items:Checklist items`,
  cases: $localize`:@@authoring.import-export.cases:Test cases`,
  noKinds: $localize`:@@authoring.import-export.no-kinds:Choose at least one kind. An empty list would mean all kinds to the server.`,
  countUnavailable: $localize`:@@authoring.import-export.count-unavailable:Exact count unavailable: the export response supplies a file, not a count.`,
  fields: $localize`:@@authoring.import-export.fields:Export field preview`,
  fieldsHint: $localize`:@@authoring.import-export.fields-hint:Contract columns for the round trip, not an example schema. No source version is claimed.`,
  download: $localize`:@@authoring.import-export.download:Download export`,
  confirmDownload: $localize`:@@authoring.import-export.confirm-download:Download this exact scope?`,
  cancel: $localize`:@@authoring.import-export.cancel:Cancel`,
  downloaded: $localize`:@@authoring.import-export.downloaded:The server file was downloaded for the confirmed scope.`,
  readOnlyExport: $localize`:@@authoring.import-export.read-only-export:Read-only export remains available at this width. Changing scope requires a desktop view.`,
  exportCap: $localize`:@@authoring.import-export.export-cap:This scope exceeds the export file cap. Choose a smaller node scope. No truncated file was created.`,
  exportDenied: $localize`:@@authoring.import-export.export-denied:You do not have permission to export from this project. No file was created.`,
  scopeMismatch: $localize`:@@authoring.import-export.scope-mismatch:This credential is scoped to another project. Use a credential permitted for this exact project; no rows were imported and no file was exported.`,
  emptyExport: $localize`:@@authoring.import-export.empty-export:This export scope is empty. No file was downloaded; choose another scope explicitly.`,
};

export const IMPORT_REASONS: Readonly<Record<string, string>> = {
  invalid_utf8: IMPEX_COPY.invalidUtf8,
  unknown_parent: $localize`:@@authoring.import-export.reason.unknown-parent:The parent must name an earlier node of the required kind.`,
  orphan_row: $localize`:@@authoring.import-export.reason.orphan-row:The row has no valid owning item or node.`,
  duplicate_key: $localize`:@@authoring.import-export.reason.duplicate-key:This file-local key is duplicated. All rows are retained; nothing was imported.`,
  code_conflict: $localize`:@@authoring.import-export.reason.code-conflict:The case code conflicts with the project's prefix or allowed number. Nothing was written.`,
  value_invalid: $localize`:@@authoring.import-export.reason.value-invalid:This field does not meet the import contract.`,
  steps_invalid: $localize`:@@authoring.import-export.reason.steps-invalid:The paired action and expected-result steps do not meet the steps contract.`,
  invalid_text: $localize`:@@authoring.import-export.reason.invalid-text:This text does not meet the server's text requirements.`,
  unsupported_format: $localize`:@@authoring.import-export.reason.unsupported-format:The file structure, CSV header or chosen format is not supported.`,
};
export const UNKNOWN_IMPORT_REASON = $localize`:@@authoring.import-export.reason.unknown:This file could not be processed.`;
export function importReason(reason: string | undefined): string {
  return reason && Object.hasOwn(IMPORT_REASONS, reason)
    ? `${IMPORT_REASONS[reason]} (${reason})`
    : UNKNOWN_IMPORT_REASON;
}
export function importFailure(error: ApiError, exporting = false): string {
  if (error.code === 'xlsx_too_large') return IMPEX_COPY.xlsxTooLarge;
  if (error.code === 'format_mismatch') return IMPEX_COPY.formatMismatch;
  if (error.code === 'invalid_utf8') return IMPEX_COPY.invalidUtf8;
  switch (error.kind) {
    case 'payload_too_large':
      return exporting
        ? IMPEX_COPY.exportCap
        : $localize`:@@authoring.import-export.failure.size:The file exceeds the allowed limit of 5 MiB or 5,000 data rows. Nothing was imported.`;
    case 'validation':
      return exporting
        ? $localize`:@@authoring.import-export.failure.export-filters:Export filters were refused. Check the format, node UUID and included kinds. No file was downloaded.`
        : IMPEX_COPY.invalid;
    case 'forbidden':
      return error.code === 'project_scope_mismatch'
        ? IMPEX_COPY.scopeMismatch
        : exporting
          ? IMPEX_COPY.exportDenied
          : IMPEX_COPY.denied;
    case 'idempotency_conflict':
      return $localize`:@@authoring.import-export.failure.conflict:This intent key belongs to a different request. Select the original file and format or start a new import intent. Nothing was retried automatically.`;
    case 'rate_limited':
    case 'unavailable':
      return exporting
        ? $localize`:@@authoring.import-export.failure.export-wait:The server is busy. Wait as directed by Retry-After, then retry this exact scope explicitly. Nothing is downloaded automatically.`
        : $localize`:@@authoring.import-export.failure.wait:The server is busy. Wait as directed by Retry-After, then retry explicitly. Your file is retained.`;
    case 'unauthenticated':
      return $localize`:@@authoring.import-export.failure.sign-in:Sign in again before retrying. No request is sent automatically after sign-in.`;
    case 'not_found':
      return $localize`:@@authoring.import-export.failure.not-found:This project or node was not found. No file is available.`;
    default:
      return exporting
        ? $localize`:@@authoring.import-export.failure.export-unknown:The export could not be completed or its server file was unavailable. No file was downloaded. Retry the exact scope explicitly.`
        : $localize`:@@authoring.import-export.failure.unknown:The request could not be completed. Its outcome is unconfirmed; retry only the same intent.`;
  }
}

export function importSizeError(file: File, format: AuthoringFormat): ApiError {
  const workbook = format === 'xlsx';
  const error: ApiError = {
    kind: workbook ? 'validation' : 'payload_too_large',
    status: workbook ? 422 : 413,
    code: workbook ? 'xlsx_too_large' : 'payload_too_large',
    message: '',
    requestId: null,
    retryable: false,
    retryAfterSeconds: null,
    details: [
      { path: '/file', reason: 'body_too_large', limit: IMPORT_BYTE_CAP, actual: file.size },
    ],
  };
  return { ...error, message: importFailure(error) };
}

const IMPORT_PATHS = new Set([
  ...IMPORT_COLUMNS.map((column) => '/' + column),
  '/file',
  '/header',
  '/format',
]);
export function safeImportPath(path: string | undefined): string {
  return path && IMPORT_PATHS.has(path) ? path : '/file';
}

export const IMPORT_FIELD_LABELS: Readonly<Record<(typeof IMPORT_COLUMNS)[number], string>> = {
  kind: IMPEX_COPY.kind,
  key: $localize`:@@authoring.import-export.field-key:File-local key`,
  parent_key: $localize`:@@authoring.import-export.field-parent-key:Parent key`,
  item_key: $localize`:@@authoring.import-export.field-item-key:Owning item key`,
  node_kind: $localize`:@@authoring.import-export.field-node-kind:Node kind`,
  title: $localize`:@@authoring.import-export.field-title:Title`,
  description: $localize`:@@authoring.import-export.field-description:Description`,
  precondition: $localize`:@@authoring.import-export.field-precondition:Precondition`,
  steps: $localize`:@@authoring.import-export.field-steps:Action and expected-result steps`,
  priority: $localize`:@@authoring.import-export.field-priority:Priority`,
  state: $localize`:@@authoring.import-export.field-state:State`,
  code: $localize`:@@authoring.import-export.field-code:Case code`,
  position: $localize`:@@authoring.import-export.field-position:Position`,
};

export function importFieldLabel(path: string | undefined): string {
  const column = IMPORT_COLUMNS.find((name) => '/' + name === safeImportPath(path));
  return column ? IMPORT_FIELD_LABELS[column] : IMPEX_COPY.file;
}

export function importProblemTarget(index: number, path: string | undefined): string {
  const column = IMPORT_COLUMNS.find((name) => '/' + name === safeImportPath(path));
  return `import-row-${index}-${column ?? 'status'}`;
}
