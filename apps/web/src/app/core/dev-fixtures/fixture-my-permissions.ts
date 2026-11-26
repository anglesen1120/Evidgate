// The caller's own grants (`getMyPermissions`), derived from the `listPermissions` session the
// review toggled. Every fixture session already names its grant set through
// `?fixture.variant.listPermissions=<variant>`; answering the new read from that same page keeps one
// source of truth per session instead of a second copy of each grant list. A `getMyPermissions`
// toggle other than `ok` (`forbidden`, `unavailable`) is answered by its corpus row.

import permissionsCatalogue from '../../../../../../contracts/permissions.json';
import type { FixtureDocument } from './fixture-document';
import type { FixtureEntry } from './fixture-catalog-entries';

/** Codes only an operator holds: the member endpoint never returns them. */
const NOT_MEMBER_GRANTABLE: ReadonlySet<string> = new Set(
  permissionsCatalogue.permissions.filter((row) => !row.tenant_assignable).map((row) => row.code),
);

/** The grant page of `variant`, as `listPermissions` would serve it; `undefined` when it has none. */
export type GrantPageLookup = (variant: string) => FixtureEntry | undefined;

/**
 * The entry answering `GET /me/permissions` for one session, or `undefined` when the session's
 * `listPermissions` variant has no 200 page (the caller then answers `fixture_not_found`).
 */
export function findMyPermissionsFixture(
  grantVariant: string,
  orgId: string | null,
  projectId: string | null,
  lookup: GrantPageLookup,
): FixtureEntry | undefined {
  const page = lookup(grantVariant)?.document;
  const body = page?.body as { items?: unknown } | undefined;
  if (page?.status !== 200 || !Array.isArray(body?.items)) return undefined;
  const permissions = (body.items as ReadonlyArray<{ readonly code: string }>)
    .map((row) => row.code)
    .filter((code) => !NOT_MEMBER_GRANTABLE.has(code))
    .sort((left, right) => (left < right ? -1 : left > right ? 1 : 0));
  const document: FixtureDocument = {
    operation_id: 'getMyPermissions',
    variant: grantVariant,
    status: 200,
    headers: {},
    latency_ms: 0,
    body: {
      org_id: orgId ?? '018f0000-0000-7000-8000-000000000001',
      ...(projectId === null ? {} : { project_id: projectId }),
      roles: [],
      permissions,
    },
  };
  return { name: `me-permissions-${grantVariant}`, document };
}
