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
];
