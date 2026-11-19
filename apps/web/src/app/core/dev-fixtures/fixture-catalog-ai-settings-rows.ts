import d0 from '../../../../../../fixtures/web-api/ai-settings-head/input.json';
import d1 from '../../../../../../fixtures/web-api/ai-settings-default/input.json';
import d2 from '../../../../../../fixtures/web-api/ai-settings-head-forbidden/input.json';
import d3 from '../../../../../../fixtures/web-api/ai-settings-head-unavailable/input.json';
import d4 from '../../../../../../fixtures/web-api/ai-settings-save/input.json';
import d5 from '../../../../../../fixtures/web-api/ai-settings-save-conflict/input.json';
import d6 from '../../../../../../fixtures/web-api/ai-settings-save-validation/input.json';
import d7 from '../../../../../../fixtures/web-api/ai-settings-save-unavailable/input.json';
import d8 from '../../../../../../fixtures/web-api/ai-settings-versions/input.json';
import d9 from '../../../../../../fixtures/web-api/ai-settings-versions-page-2/input.json';
import d10 from '../../../../../../fixtures/web-api/ai-settings-versions-empty/input.json';
import d11 from '../../../../../../fixtures/web-api/ai-settings-version-1/input.json';
import d12 from '../../../../../../fixtures/web-api/ai-settings-version-2/input.json';
import d13 from '../../../../../../fixtures/web-api/ai-settings-templates/input.json';
import versionNotFound from '../../../../../../fixtures/web-api/ai-settings-version-not-found/input.json';
import type { FixtureCatalogRow } from './fixture-document';
export const FIXTURE_CATALOG_AI_SETTINGS_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'ai-settings-head', document: d0 },
  { name: 'ai-settings-default', document: d1 },
  { name: 'ai-settings-head-forbidden', document: d2 },
  { name: 'ai-settings-head-unavailable', document: d3 },
  { name: 'ai-settings-save', document: d4 },
  { name: 'ai-settings-save-conflict', document: d5 },
  { name: 'ai-settings-save-validation', document: d6 },
  { name: 'ai-settings-save-unavailable', document: d7 },
  { name: 'ai-settings-versions', document: d8 },
  { name: 'ai-settings-versions-page-2', document: d9, cursor: 'ai-settings-page-2' },
  { name: 'ai-settings-versions-empty', document: d10 },
  { name: 'ai-settings-version-1', document: d11 },
  { name: 'ai-settings-version-2', document: d12 },
  { name: 'ai-settings-templates', document: d13 },
  { name: 'ai-settings-version-not-found', document: versionNotFound },
];
