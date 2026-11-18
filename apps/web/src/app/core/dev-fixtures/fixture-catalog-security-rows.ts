import list from '../../../../../../fixtures/web-api/security-events-list/input.json';
import empty from '../../../../../../fixtures/web-api/security-events-list-empty/input.json';
import forbidden from '../../../../../../fixtures/web-api/security-events-list-forbidden/input.json';
import unavailable from '../../../../../../fixtures/web-api/security-events-list-unavailable/input.json';
import second from '../../../../../../fixtures/web-api/security-events-list-page-2/input.json';
import type { FixtureCatalogRow } from './fixture-document';

export const FIXTURE_CATALOG_SECURITY_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'security-events-list', document: list },
  {
    name: 'security-events-list-slow',
    document: { ...list, variant: 'slow', latency_ms: 1000 },
    derived: true,
  },
  { name: 'security-events-list-empty', document: empty },
  { name: 'security-events-list-forbidden', document: forbidden },
  { name: 'security-events-list-unavailable', document: unavailable },
  { name: 'security-events-list-page-2', document: second, cursor: 'security-events-next' },
];
