// The Settings retention review's writer session, in TypeScript rather than the
// `fixtures/web-api` corpus: `settings-admin` plus `project:write`, a grant no corpus variant
// holds yet. Editing the corpus would change a document the .NET fixture loader also walks, so the
// extra row is merged here from the corpus page itself; nothing else is restated.
//
// A review selects it with `?fixture.variant.listPermissions=settings-retention-write`.

import type { FixtureDocument } from './fixture-document';
import { findFixtureEntry } from './fixture-catalog';
import type { FixtureEntry } from './fixture-catalog-entries';

/** The toggle value that selects this session; no corpus directory answers it. */
export const SETTINGS_RETENTION_WRITE_VARIANT = 'settings-retention-write';

const PROJECT_WRITE_ROW: PermissionRow = {
  code: 'project:write',
  resource: 'project',
  action: 'write',
  description:
    'Change project settings such as retention and the artifact kind policy (T21: tenant_admin).',
  sensitive: false,
  reserved: false,
  tenant_assignable: true,
};

/** One row of a `listPermissions` page, exactly as the corpus stores it. */
interface PermissionRow {
  readonly code: string;
  readonly [member: string]: unknown;
}

/** The `settings-admin` corpus page, narrowed to the rows this specimen extends. */
function adminPage(): { items: readonly PermissionRow[]; next_cursor: unknown } {
  const document = findFixtureEntry('listPermissions', 'settings-admin')?.document;
  const body = document?.body as { items?: unknown; next_cursor?: unknown } | undefined;
  if (document?.status !== 200 || !Array.isArray(body?.['items'])) {
    throw new Error('the listPermissions corpus lost its settings-admin page');
  }
  return { items: body['items'] as readonly PermissionRow[], next_cursor: body['next_cursor'] };
}

/**
 * The fixture entry answering `variant`, or `undefined` so the corpus answers as usual — a typo in
 * the toggle must surface as `fixture_not_found` or the corpus page, never as a merged grant set.
 */
export function findSettingsRetentionWriteFixture(variant: string): FixtureEntry | undefined {
  if (variant !== SETTINGS_RETENTION_WRITE_VARIANT) return undefined;
  const admin = adminPage();
  const items = [...admin.items, PROJECT_WRITE_ROW].sort((left, right) =>
    left['code'] < right['code'] ? -1 : left['code'] > right['code'] ? 1 : 0,
  );
  const document: FixtureDocument = {
    operation_id: 'listPermissions',
    variant,
    status: 200,
    headers: {},
    latency_ms: 0,
    body: { items, next_cursor: admin.next_cursor },
  };
  return { name: `permissions-list-${variant}`, document };
}
