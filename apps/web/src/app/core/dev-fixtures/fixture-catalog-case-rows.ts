import cases from '../../../../../../fixtures/web-api/catalog-cases-list/input.json';
import page2 from '../../../../../../fixtures/web-api/catalog-cases-page-2/input.json';
import archived from '../../../../../../fixtures/web-api/catalog-cases-archived/input.json';
import empty from '../../../../../../fixtures/web-api/catalog-cases-empty/input.json';
import forbidden from '../../../../../../fixtures/web-api/catalog-cases-forbidden/input.json';
import unavailable from '../../../../../../fixtures/web-api/catalog-cases-unavailable/input.json';
import owner from '../../../../../../fixtures/web-api/catalog-case-owner/input.json';
import ownerForbidden from '../../../../../../fixtures/web-api/catalog-case-owner-forbidden/input.json';
import ownerConflict from '../../../../../../fixtures/web-api/catalog-case-owner-conflict/input.json';
import ownerStale from '../../../../../../fixtures/web-api/catalog-case-owner-stale/input.json';
import type { FixtureCatalogRow } from './fixture-document';

export const FIXTURE_CATALOG_CASE_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'catalog-cases-list', document: cases },
  {
    name: 'catalog-cases-page-2',
    document: page2,
    // The continuation `catalog-cases-list` names, so Load more walks both pages.
    cursor: 'catalog-more-opaque',
  },
  { name: 'catalog-cases-archived', document: archived },
  { name: 'catalog-cases-empty', document: empty },
  { name: 'catalog-cases-forbidden', document: forbidden },
  { name: 'catalog-cases-unavailable', document: unavailable },
  { name: 'catalog-case-owner', document: owner },
  { name: 'catalog-case-owner-forbidden', document: ownerForbidden },
  { name: 'catalog-case-owner-conflict', document: ownerConflict },
  { name: 'catalog-case-owner-stale', document: ownerStale },
];
