// The learn-and-repair review screens' and the healing kill switch's sessions: the default `ok`
// grant set plus the healing grants their routes are gated on. Editing the corpus default would hand
// those grants to every review that uses it, so the extra rows are merged here from the permission
// catalogue.
//
// A session selects one with `?fixture.variant.listPermissions=<variant>`.

import permissionsCatalogue from '../../../../../../contracts/permissions.json';
import type { FixtureDocument } from './fixture-document';
import { findFixtureEntry } from './fixture-catalog';
import type { FixtureEntry } from './fixture-catalog-entries';

/** The toggle value of the review sessions, which hold `healing:triage` only. */
export const HEALING_TRIAGE_PERMISSIONS_VARIANT = 'healing-triage';
/** The toggle value of the kill-switch session, which also holds `healing:configure`. */
export const HEALING_CONFIGURE_PERMISSIONS_VARIANT = 'healing-configure';

const GRANTS_BY_VARIANT: ReadonlyMap<string, readonly string[]> = new Map([
  [HEALING_TRIAGE_PERMISSIONS_VARIANT, ['healing:triage']],
  [HEALING_CONFIGURE_PERMISSIONS_VARIANT, ['healing:triage', 'healing:configure']],
]);

interface PermissionRow {
  readonly code: string;
  readonly [member: string]: unknown;
}

function catalogueRow(code: string): PermissionRow {
  const row = permissionsCatalogue.permissions.find((entry) => entry.code === code);
  if (!row) throw new Error(`The permission catalogue lost ${code}`);
  return row;
}

/** The fixture entry answering `variant`, or `undefined` so the corpus answers as usual. */
export function findHealingPermissionsFixture(variant: string): FixtureEntry | undefined {
  const grants = GRANTS_BY_VARIANT.get(variant);
  if (grants === undefined) return undefined;
  const document = findFixtureEntry('listPermissions', 'ok')?.document;
  const body = document?.body as { items?: unknown; next_cursor?: unknown } | undefined;
  if (document?.status !== 200 || !Array.isArray(body?.['items'])) {
    throw new Error('the listPermissions corpus lost its ok page');
  }
  const rows = body['items'] as readonly PermissionRow[];
  const items = [
    ...rows.filter((row) => !grants.includes(row.code)),
    ...grants.map(catalogueRow),
  ].sort((left, right) => (left.code < right.code ? -1 : left.code > right.code ? 1 : 0));
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
