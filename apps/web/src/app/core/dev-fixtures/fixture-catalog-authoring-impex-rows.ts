import fixture0 from '../../../../../../fixtures/web-api/authoring-import-preview/input.json';
import fixture1 from '../../../../../../fixtures/web-api/authoring-import-confirmed/input.json';
import fixture2 from '../../../../../../fixtures/web-api/authoring-import-noop/input.json';
import fixture3 from '../../../../../../fixtures/web-api/authoring-import-mixed/input.json';
import fixture4 from '../../../../../../fixtures/web-api/authoring-import-duplicate/input.json';
import fixture5 from '../../../../../../fixtures/web-api/authoring-import-prefix/input.json';
import fixture6 from '../../../../../../fixtures/web-api/authoring-import-file/input.json';
import fixture7 from '../../../../../../fixtures/web-api/authoring-import-header/input.json';
import fixture8 from '../../../../../../fixtures/web-api/authoring-import-format/input.json';
import fixture9 from '../../../../../../fixtures/web-api/authoring-import-denied/input.json';
import fixture10 from '../../../../../../fixtures/web-api/authoring-import-conflict/input.json';
import fixture11 from '../../../../../../fixtures/web-api/authoring-import-too-large/input.json';
import fixture12 from '../../../../../../fixtures/web-api/authoring-import-rate-limited/input.json';
import fixture13 from '../../../../../../fixtures/web-api/authoring-import-unavailable/input.json';
import fixture14 from '../../../../../../fixtures/web-api/authoring-export-json/input.json';
import fixture15 from '../../../../../../fixtures/web-api/authoring-export-too-large/input.json';
import fixture16 from '../../../../../../fixtures/web-api/authoring-export-denied/input.json';
import fixture17 from '../../../../../../fixtures/web-api/authoring-export-unavailable/input.json';
import fixture18 from '../../../../../../fixtures/web-api/authoring-export-rate-limited/input.json';
import fixture19 from '../../../../../../fixtures/web-api/authoring-import-history/input.json';
import fixture20 from '../../../../../../fixtures/web-api/authoring-import-xlsx/input.json';
import fixture21 from '../../../../../../fixtures/web-api/authoring-import-xlsx-confirmed/input.json';
import fixture22 from '../../../../../../fixtures/web-api/authoring-import-xlsx-too-large/input.json';
import fixture23 from '../../../../../../fixtures/web-api/authoring-import-xlsx-invalid/input.json';
import fixture24 from '../../../../../../fixtures/web-api/authoring-export-xlsx/input.json';
import fixture25 from '../../../../../../fixtures/web-api/authoring-export-xlsx-too-large/input.json';
import fixture26 from '../../../../../../fixtures/web-api/authoring-export-xlsx-invalid/input.json';
import type { FixtureCatalogRow } from './fixture-document';

export const FIXTURE_CATALOG_AUTHORING_IMPEX_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'authoring-import-preview', document: fixture0 },
  { name: 'authoring-import-confirmed', document: fixture1 },
  { name: 'authoring-import-noop', document: fixture2 },
  { name: 'authoring-import-mixed', document: fixture3 },
  { name: 'authoring-import-duplicate', document: fixture4 },
  { name: 'authoring-import-prefix', document: fixture5 },
  { name: 'authoring-import-file', document: fixture6 },
  { name: 'authoring-import-header', document: fixture7 },
  { name: 'authoring-import-format', document: fixture8 },
  { name: 'authoring-import-denied', document: fixture9 },
  { name: 'authoring-import-conflict', document: fixture10 },
  { name: 'authoring-import-too-large', document: fixture11 },
  { name: 'authoring-import-rate-limited', document: fixture12 },
  { name: 'authoring-import-unavailable', document: fixture13 },
  { name: 'authoring-export-json', document: fixture14 },
  { name: 'authoring-export-too-large', document: fixture15 },
  { name: 'authoring-export-denied', document: fixture16 },
  { name: 'authoring-export-unavailable', document: fixture17 },
  { name: 'authoring-export-rate-limited', document: fixture18 },
  { name: 'authoring-import-history', document: fixture19 },
  { name: 'authoring-import-xlsx', document: fixture20 },
  { name: 'authoring-import-xlsx-confirmed', document: fixture21 },
  { name: 'authoring-import-xlsx-too-large', document: fixture22 },
  { name: 'authoring-import-xlsx-invalid', document: fixture23 },
  { name: 'authoring-export-xlsx', document: fixture24 },
  { name: 'authoring-export-xlsx-too-large', document: fixture25 },
  { name: 'authoring-export-xlsx-invalid', document: fixture26 },
];

// The JSON corpus validates the logical document; this tiny workbook encodes its same single
// node and thirteen columns. Only the fixture backend emits these archive bytes.
export const AUTHORING_XLSX_BASE64 =
  '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';
