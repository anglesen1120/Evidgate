/**
 * Copy this panel owns beyond the shared import/export map: the unavailable XLSX choice, the
 * scope label that never claims project scope, and the narrow-view notice that keeps the read-only
 * download visible.
 */
export const EXPORT_COPY = {
  noScope: $localize`:@@authoring.import-export.no-scope:No scope`,
  xlsxOption: $localize`:@@authoring.import-export.xlsx-option:XLSX — unavailable`,
  xlsxUnavailable: $localize`:@@authoring.import-export.xlsx-unavailable:XLSX export is unavailable: the current transport carries text only, so no workbook is requested. Choose CSV or JSON.`,
  narrowNotice: $localize`:@@authoring.import-export.narrow-notice:Desktop required to change the export scope. Downloading the scope shown is still available.`,
};
