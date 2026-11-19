import fixture0 from '../../../../../../fixtures/web-api/authoring-pending-list/input.json';
import fixture1 from '../../../../../../fixtures/web-api/authoring-pending-selection/input.json';
import fixture2 from '../../../../../../fixtures/web-api/authoring-pending-queue/input.json';
import fixture3 from '../../../../../../fixtures/web-api/authoring-pending-apply/input.json';
import fixture4 from '../../../../../../fixtures/web-api/authoring-pending-apply-comment-only/input.json';
import fixture5 from '../../../../../../fixtures/web-api/authoring-pending-apply-conflict/input.json';
import fixture6 from '../../../../../../fixtures/web-api/authoring-pending-apply-rate-limited/input.json';
import fixture7 from '../../../../../../fixtures/web-api/authoring-pending-discard/input.json';
import fixture8 from '../../../../../../fixtures/web-api/authoring-pending-queue-conflict/input.json';
import fixture9 from '../../../../../../fixtures/web-api/authoring-pending-queue-quota/input.json';
import fixture10 from '../../../../../../fixtures/web-api/authoring-pending-forbidden/input.json';
import fixture11 from '../../../../../../fixtures/web-api/authoring-pending-not-found/input.json';
import type { FixtureCatalogRow } from './fixture-document';

export const FIXTURE_CATALOG_AUTHORING_PENDING_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'authoring-pending-list', document: fixture0 },
  { name: 'authoring-pending-selection', document: fixture1 },
  { name: 'authoring-pending-queue', document: fixture2 },
  { name: 'authoring-pending-apply', document: fixture3 },
  { name: 'authoring-pending-apply-comment-only', document: fixture4 },
  { name: 'authoring-pending-apply-conflict', document: fixture5 },
  { name: 'authoring-pending-apply-rate-limited', document: fixture6 },
  { name: 'authoring-pending-discard', document: fixture7 },
  { name: 'authoring-pending-queue-conflict', document: fixture8 },
  { name: 'authoring-pending-queue-quota', document: fixture9 },
  { name: 'authoring-pending-forbidden', document: fixture10 },
  { name: 'authoring-pending-not-found', document: fixture11 },
];
