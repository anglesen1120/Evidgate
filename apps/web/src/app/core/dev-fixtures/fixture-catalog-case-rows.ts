import cases from '../../../../../../fixtures/web-api/catalog-cases-list/input.json';
import empty from '../../../../../../fixtures/web-api/catalog-cases-empty/input.json';
import forbidden from '../../../../../../fixtures/web-api/catalog-cases-forbidden/input.json';
import unavailable from '../../../../../../fixtures/web-api/catalog-cases-unavailable/input.json';
import type { FixtureCatalogRow } from './fixture-document';

export const FIXTURE_CATALOG_CASE_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'catalog-cases-list', document: cases },
  { name: 'catalog-cases-empty', document: empty },
  { name: 'catalog-cases-forbidden', document: forbidden },
  { name: 'catalog-cases-unavailable', document: unavailable },
];
