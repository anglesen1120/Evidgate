// The learn-and-repair review screens' session: the default `ok` grant set plus `healing:triage`,
// the grant their review routes are gated on. Editing the corpus default would hand that grant to
// every review that uses it, so the extra row is merged here from the permission catalogue.
//
// A review selects it with `?fixture.variant.listPermissions=healing-triage`.

import permissionsCatalogue from '../../../../../../contracts/permissions.json';
import type { FixtureDocument } from './fixture-document';
import { findFixtureEntry } from './fixture-catalog';
import type { FixtureEntry } from './fixture-catalog-entries';

/** The toggle value that selects this session; no corpus directory answers it. */
export const HEALING_TRIAGE_PERMISSIONS_VARIANT = 'healing-triage';

interface PermissionRow {
  readonly code: string;
  readonly [member: string]: unknown;
}

function healingTriageRow(): PermissionRow {
  const row = permissionsCatalogue.permissions.find((entry) => entry.code === 'healing:triage');
  if (!row) throw new Error('The permission catalogue lost healing:triage');
  return row;
}

/** The fixture entry answering `variant`, or `undefined` so the corpus answers as usual. */
export function findHealingTriagePermissionsFixture(variant: string): FixtureEntry | undefined {
  if (variant !== HEALING_TRIAGE_PERMISSIONS_VARIANT) return undefined;
  const document = findFixtureEntry('listPermissions', 'ok')?.document;
  const body = document?.body as { items?: unknown; next_cursor?: unknown } | undefined;
  if (document?.status !== 200 || !Array.isArray(body?.['items'])) {
    throw new Error('the listPermissions corpus lost its ok page');
  }
  const rows = body['items'] as readonly PermissionRow[];
  const items = [...rows.filter((row) => row.code !== 'healing:triage'), healingTriageRow()].sort(
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
