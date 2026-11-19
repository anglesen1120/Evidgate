import roots from '../../../../../../fixtures/web-api/authoring-tree-roots/input.json';
import rootsPage2 from '../../../../../../fixtures/web-api/authoring-tree-roots-page-2/input.json';
import childrenPage2 from '../../../../../../fixtures/web-api/authoring-tree-children-page-2/input.json';
import level1 from '../../../../../../fixtures/web-api/authoring-tree-level-1/input.json';
import level2 from '../../../../../../fixtures/web-api/authoring-tree-level-2/input.json';
import level3 from '../../../../../../fixtures/web-api/authoring-tree-level-3/input.json';
import level4 from '../../../../../../fixtures/web-api/authoring-tree-level-4/input.json';
import level5 from '../../../../../../fixtures/web-api/authoring-tree-level-5/input.json';
import level6 from '../../../../../../fixtures/web-api/authoring-tree-level-6/input.json';
import level7 from '../../../../../../fixtures/web-api/authoring-tree-level-7/input.json';
import empty from '../../../../../../fixtures/web-api/authoring-tree-empty/input.json';
import loading from '../../../../../../fixtures/web-api/authoring-tree-loading/input.json';
import error from '../../../../../../fixtures/web-api/authoring-tree-error/input.json';
import forbidden from '../../../../../../fixtures/web-api/authoring-tree-forbidden/input.json';
import notFound from '../../../../../../fixtures/web-api/authoring-tree-not-found/input.json';
import rateLimited from '../../../../../../fixtures/web-api/authoring-tree-rate-limited/input.json';
import type { FixtureCatalogRow } from './fixture-document';

export const FIXTURE_CATALOG_AUTHORING_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'authoring-tree-roots', document: roots },
  { name: 'authoring-tree-roots-page-2', document: rootsPage2 },
  { name: 'authoring-tree-children-page-2', document: childrenPage2 },
  { name: 'authoring-tree-level-1', document: level1 },
  { name: 'authoring-tree-level-2', document: level2 },
  { name: 'authoring-tree-level-3', document: level3 },
  { name: 'authoring-tree-level-4', document: level4 },
  { name: 'authoring-tree-level-5', document: level5 },
  { name: 'authoring-tree-level-6', document: level6 },
  { name: 'authoring-tree-level-7', document: level7 },
  { name: 'authoring-tree-empty', document: empty },
  { name: 'authoring-tree-loading', document: loading },
  { name: 'authoring-tree-error', document: error },
  { name: 'authoring-tree-forbidden', document: forbidden },
  { name: 'authoring-tree-not-found', document: notFound },
  { name: 'authoring-tree-rate-limited', document: rateLimited },
];
