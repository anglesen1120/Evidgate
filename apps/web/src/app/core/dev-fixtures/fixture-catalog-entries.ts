// Keep row-group order stable: first-match fixture selection depends on it.

import { FIXTURE_CATALOG_RUN_ROWS } from './fixture-catalog-run-rows';
import { FIXTURE_CATALOG_SESSION_ROWS } from './fixture-catalog-session-rows';
import { FIXTURE_CATALOG_RELEASE_ROWS } from './fixture-catalog-release-rows';
import { FIXTURE_CATALOG_SECURITY_ROWS } from './fixture-catalog-security-rows';
import { FIXTURE_CATALOG_CASE_ROWS } from './fixture-catalog-case-rows';
import { FIXTURE_CATALOG_AUTHORING_ROWS } from './fixture-catalog-authoring-rows';
import { FIXTURE_CATALOG_AUTHORING_IMPEX_ROWS } from './fixture-catalog-authoring-impex-rows';
import { FIXTURE_CATALOG_AI_SETTINGS_ROWS } from './fixture-catalog-ai-settings-rows';
import { FIXTURE_CATALOG_AUTHORING_PENDING_ROWS } from './fixture-catalog-authoring-pending-rows';
import { FIXTURE_CATALOG_AUTHORING_EDITOR_ROWS } from './fixture-catalog-authoring-editor-rows';
import type { FixtureCatalogRow } from './fixture-document';

export const FIXTURE_CATALOG: readonly FixtureCatalogRow[] = [
  ...FIXTURE_CATALOG_SESSION_ROWS,
  ...FIXTURE_CATALOG_RUN_ROWS,
  ...FIXTURE_CATALOG_RELEASE_ROWS,
  ...FIXTURE_CATALOG_SECURITY_ROWS,
  ...FIXTURE_CATALOG_CASE_ROWS,
  ...FIXTURE_CATALOG_AUTHORING_ROWS,
  ...FIXTURE_CATALOG_AUTHORING_IMPEX_ROWS,
  ...FIXTURE_CATALOG_AI_SETTINGS_ROWS,
  ...FIXTURE_CATALOG_AUTHORING_PENDING_ROWS,
  ...FIXTURE_CATALOG_AUTHORING_EDITOR_ROWS,
];

/** One catalog entry: the corpus directory name, the document it holds and the cursor it answers. */
export type FixtureEntry = (typeof FIXTURE_CATALOG)[number];
