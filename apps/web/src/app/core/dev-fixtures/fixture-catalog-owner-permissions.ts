// The catalog owner review's session, in TypeScript rather than the `fixtures/web-api` corpus:
// the default `ok` grant set plus `membership:read`, which the owner picker needs to list the
// assignable members. Editing the corpus default would hand that grant to every review that uses
// it, so the extra row is merged here from the corpus page and the permission catalogue itself.
//
// A review selects it with `?fixture.variant.listPermissions=catalog-owner`.

import permissionsCatalogue from '../../../../../../contracts/permissions.json';
import type { FixtureDocument } from './fixture-document';
import { findFixtureEntry } from './fixture-catalog';
import type { FixtureEntry } from './fixture-catalog-entries';

/** The toggle value that selects this session; no corpus directory answers it. */
export const CATALOG_OWNER_PERMISSIONS_VARIANT = 'catalog-owner';

interface PermissionRow {
  readonly code: string;
  readonly [member: string]: unknown;
}

/** The catalog owner review's extra grant, read from the permission catalogue so it cannot drift. */
function membershipReadRow(): PermissionRow {
  const row = permissionsCatalogue.permissions.find((entry) => entry.code === 'membership:read');
  if (!row) throw new Error('The permission catalogue lost membership:read');
  return row;
}

/**
 * The fixture entry answering `variant`, or `undefined` so the corpus answers as usual: a typo in
 * the toggle must surface as `fixture_not_found` or the corpus page, never as a merged grant set.
 */
export function findCatalogOwnerPermissionsFixture(variant: string): FixtureEntry | undefined {
  if (variant !== CATALOG_OWNER_PERMISSIONS_VARIANT) return undefined;
  const document = findFixtureEntry('listPermissions', 'ok')?.document;
  const body = document?.body as { items?: unknown; next_cursor?: unknown } | undefined;
  if (document?.status !== 200 || !Array.isArray(body?.['items'])) {
    throw new Error('the listPermissions corpus lost its ok page');
  }
  const items = [...(body['items'] as readonly PermissionRow[]), membershipReadRow()].sort(
    (left, right) => (left.code < right.code ? -1 : left.code > right.code ? 1 : 0),
  );
  const merged: FixtureDocument = {
    operation_id: 'listPermissions',
    variant,
    status: 200,
    headers: {},
    latency_ms: 0,
    body: { items, next_cursor: body['next_cursor'] },
  };
  return { name: `permissions-list-${variant}`, document: merged };
}
