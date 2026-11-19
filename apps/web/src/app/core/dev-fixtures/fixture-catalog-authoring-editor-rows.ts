// The manual-case editor's corpus rows, kept in their own module so the shared catalog file stays
// the list of fixtures rather than the place each one's wiring grows.
import d1 from '../../../../../../fixtures/web-api/authoring-editor-case/input.json';
import d2 from '../../../../../../fixtures/web-api/authoring-editor-case-current/input.json';
import d3 from '../../../../../../fixtures/web-api/authoring-editor-case-not-found/input.json';
import d4 from '../../../../../../fixtures/web-api/authoring-editor-case-validation-target/input.json';
import d5 from '../../../../../../fixtures/web-api/authoring-editor-case-conflict/input.json';
import d6 from '../../../../../../fixtures/web-api/authoring-editor-case-validation/input.json';
import d12 from '../../../../../../fixtures/web-api/authoring-editor-case-update-not-found/input.json';
import d13 from '../../../../../../fixtures/web-api/authoring-editor-case-forbidden/input.json';
import d14 from '../../../../../../fixtures/web-api/authoring-editor-case-unavailable/input.json';
import d7 from '../../../../../../fixtures/web-api/authoring-editor-versions/input.json';
import d8 from '../../../../../../fixtures/web-api/authoring-editor-version/input.json';
import d9 from '../../../../../../fixtures/web-api/authoring-editor-version-not-found/input.json';
import d10 from '../../../../../../fixtures/web-api/authoring-editor-restore/input.json';
import d11 from '../../../../../../fixtures/web-api/authoring-editor-restore-not-found/input.json';
import type { FixtureCatalogRow } from './fixture-document';

/** The editor's wire answers; the transitions a static document cannot carry live in the scenario. */
export const FIXTURE_CATALOG_AUTHORING_EDITOR_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'authoring-editor-case', document: d1 },
  { name: 'authoring-editor-case-current', document: d2 },
  { name: 'authoring-editor-case-not-found', document: d3 },
  { name: 'authoring-editor-case-validation-target', document: d4 },
  { name: 'authoring-editor-case-conflict', document: d5 },
  { name: 'authoring-editor-case-validation', document: d6 },
  { name: 'authoring-editor-case-update-not-found', document: d12 },
  { name: 'authoring-editor-case-forbidden', document: d13 },
  { name: 'authoring-editor-case-unavailable', document: d14 },
  { name: 'authoring-editor-versions', document: d7 },
  { name: 'authoring-editor-version', document: d8 },
  { name: 'authoring-editor-version-not-found', document: d9 },
  { name: 'authoring-editor-restore', document: d10 },
  { name: 'authoring-editor-restore-not-found', document: d11 },
];
