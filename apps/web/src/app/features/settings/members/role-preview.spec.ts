// 09.7.1-T2 (plan 09.7.1 §2 AC2, §10 T2.1/T2.2; COORDINATOR-DECISION S73 option B): the role
// scope preview and the built-in grant table it joins.
//
// Two kinds of case live here. The preview cases pin what the panel may claim: the grants of a
// built-in role grouped by resource with their `sensitive` flag, an omitted count for a code the
// live catalogue does not serve, and an honest "unavailable" for every state the panel cannot
// describe (a custom role, an unknown code, an unreadable catalogue). The last block ties the
// transcribed {@link BUILTIN_ROLE_GRANTS} table to `contracts/permissions.json`, read here with
// `node:fs` — the same read-only, offline approach as `shell/nav/nav-registry.spec.ts` — so a
// contract change that is not carried into the table is a red suite instead of a silent drift.
//
// The contract document is deliberately NOT imported by `role-preview.ts`: the production bundle
// would then carry the catalogue's `description` and role `name` strings, which are byte-identical
// to the ones in `fixtures/web-api/permissions-list/input.json` and `roles-list/input.json`, and
// `scripts/check-no-fixture-code.mjs` would fail the build on a leak that is not one.

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

import type { PermissionView, RoleView } from '@evidgate/sdk';

import { BUILTIN_ROLE_GRANTS, buildRolePreview } from './role-preview';

/* eslint-disable evidgate/no-hand-written-wire-dto -- `contracts/permissions.json` is a contract
   document with snake_case keys (`tenant_assignable`), not an API payload; there is no SDK type for
   a catalogue *file*, and this spec deliberately declares what it reads instead of importing a
   generated shape. Same narrow exemption as `shell/nav/nav-registry.spec.ts`. */
/** One permission row of `contracts/permissions.json`, structurally declared for this spec. */
interface CataloguePermission {
  readonly code: string;
  readonly resource: string;
  readonly action: string;
  readonly description: string;
  readonly sensitive: boolean;
  readonly reserved: boolean;
  readonly tenant_assignable: boolean;
}

/** One built-in role of `contracts/permissions.json`. */
interface CatalogueRole {
  readonly code: string;
  readonly name: string;
  readonly permissions: readonly string[];
}

interface CatalogueDocument {
  readonly roles: readonly CatalogueRole[];
  readonly permissions: readonly CataloguePermission[];
}
/* eslint-enable evidgate/no-hand-written-wire-dto */

/**
 * The repository root: `ng test` runs with `apps/web` as its working directory, but a native Vitest
 * run may start anywhere, so it is the nearest ancestor that holds `contracts/openapi.yaml`.
 */
function findRepositoryRoot(): string {
  let directory = process.cwd();
  for (;;) {
    if (existsSync(join(directory, 'contracts', 'openapi.yaml'))) {
      return directory;
    }
    const parent = dirname(directory);
    if (parent === directory) {
      throw new Error(
        `cannot locate the repository root (the directory holding contracts/openapi.yaml) from ${process.cwd()}`,
      );
    }
    directory = parent;
  }
}

const REPOSITORY_ROOT = findRepositoryRoot();
const CATALOGUE_DOCUMENT: CatalogueDocument = JSON.parse(
  readFileSync(join(REPOSITORY_ROOT, 'contracts', 'permissions.json'), 'utf8'),
) as CatalogueDocument;

function role(code: string, isBuiltin = true): RoleView {
  return {
    id: `role-${code}`,
    org_id: isBuiltin ? null : 'o-1',
    code,
    name: `${code} (test)`,
    is_builtin: isBuiltin,
    created_at: '2026-10-02T09:00:00Z',
    version: 1,
  };
}

function permission(code: string, sensitive = false): PermissionView {
  const [resource = code, action = 'read'] = code.split(':');
  return {
    code,
    resource,
    action,
    description: `${code} (test)`,
    sensitive,
    reserved: false,
    tenant_assignable: true,
  };
}

/** The contract's own rows, as the live catalogue would serve them. */
function contractCatalogue(): readonly PermissionView[] {
  return CATALOGUE_DOCUMENT.permissions.map((entry) => ({
    code: entry.code,
    resource: entry.resource,
    action: entry.action,
    description: entry.description,
    sensitive: entry.sensitive,
    reserved: entry.reserved,
    tenant_assignable: entry.tenant_assignable,
  }));
}

describe('buildRolePreview (09.7.1-T2)', () => {
  const GRANTS = new Map<string, readonly string[]>([
    ['viewer', ['artifact:read', 'artifact:read_sensitive', 'audit:read', 'ghost:read']],
  ]);

  it('groups a built-in role’s grants by resource and flags the sensitive ones', () => {
    const preview = buildRolePreview(
      role('viewer'),
      [
        permission('artifact:read'),
        permission('artifact:read_sensitive', true),
        permission('audit:read'),
        permission('case:read'),
      ],
      GRANTS,
    );

    expect(preview.kind).toBe('ready');
    if (preview.kind !== 'ready') {
      return;
    }
    expect(preview.role.code).toBe('viewer');
    // Group order follows the first appearance of each resource in the grant list, and a code the
    // role does not grant never appears even when the catalogue holds it.
    expect(preview.groups.map((group) => group.resource)).toEqual(['artifact', 'audit']);
    expect(preview.groups[0]?.permissions).toEqual([
      { code: 'artifact:read', description: 'artifact:read (test)', sensitive: false },
      {
        code: 'artifact:read_sensitive',
        description: 'artifact:read_sensitive (test)',
        sensitive: true,
      },
    ]);
    expect(preview.groups[1]?.permissions).toEqual([
      { code: 'audit:read', description: 'audit:read (test)', sensitive: false },
    ]);
  });

  it('counts a granted code the live catalogue does not serve instead of inventing it', () => {
    const preview = buildRolePreview(
      role('viewer'),
      [
        permission('artifact:read'),
        permission('artifact:read_sensitive', true),
        permission('audit:read'),
      ],
      GRANTS,
    );

    expect(preview.kind).toBe('ready');
    if (preview.kind !== 'ready') {
      return;
    }
    // `ghost:read` is granted by the role but absent from the catalogue: one omission, not a row.
    expect(preview.omitted).toBe(1);
    expect(
      preview.groups.flatMap((group) => group.permissions.map((entry) => entry.code)),
    ).not.toContain('ghost:read');
  });

  it('reports the whole grant list as omitted when the catalogue serves none of it', () => {
    const preview = buildRolePreview(role('viewer'), [permission('case:read')], GRANTS);

    expect(preview.kind).toBe('ready');
    if (preview.kind !== 'ready') {
      return;
    }
    expect(preview.omitted).toBe(4);
    expect(preview.groups).toEqual([]);
  });

  it('shows a duplicated catalogue row once', () => {
    const preview = buildRolePreview(
      role('viewer'),
      [permission('artifact:read'), permission('artifact:read')],
      GRANTS,
    );

    expect(preview.kind).toBe('ready');
    if (preview.kind !== 'ready') {
      return;
    }
    expect(preview.groups).toHaveLength(1);
    expect(preview.groups[0]?.permissions).toHaveLength(1);
  });

  it('joins the real built-in table with the contract catalogue for tenant_admin', () => {
    const preview = buildRolePreview(
      role('tenant_admin'),
      contractCatalogue(),
      BUILTIN_ROLE_GRANTS,
    );

    expect(preview.kind).toBe('ready');
    if (preview.kind !== 'ready') {
      return;
    }
    // Every code the contract grants for this role exists in the contract's catalogue, so the
    // preview of a built-in role against the real catalogue omits nothing.
    expect(preview.omitted).toBe(0);
    const membership = preview.groups.find((group) => group.resource === 'membership');
    expect(membership?.permissions.map((entry) => entry.code)).toEqual([
      'membership:read',
      'membership:write',
    ]);
  });

  it('answers custom-role for the org’s own role, catalogue or not', () => {
    const custom = role('auditor', false);

    expect(buildRolePreview(custom, [permission('artifact:read')], BUILTIN_ROLE_GRANTS)).toEqual({
      kind: 'unavailable',
      reason: 'custom-role',
    });
    // The role's own kind decides before the catalogue's availability: a custom role has no
    // published grant list either way.
    expect(buildRolePreview(custom, null, BUILTIN_ROLE_GRANTS)).toEqual({
      kind: 'unavailable',
      reason: 'custom-role',
    });
  });

  it('answers unknown-role when no role is selected', () => {
    expect(buildRolePreview(undefined, [permission('artifact:read')], BUILTIN_ROLE_GRANTS)).toEqual(
      {
        kind: 'unavailable',
        reason: 'unknown-role',
      },
    );
    expect(buildRolePreview(undefined, null, BUILTIN_ROLE_GRANTS)).toEqual({
      kind: 'unavailable',
      reason: 'unknown-role',
    });
  });

  it('answers unknown-role for a built-in code the contract does not publish', () => {
    const unknown = role('future_admin');

    expect(buildRolePreview(unknown, contractCatalogue(), BUILTIN_ROLE_GRANTS)).toEqual({
      kind: 'unavailable',
      reason: 'unknown-role',
    });
  });

  it('answers catalogue-unavailable when the live catalogue could not be read', () => {
    expect(buildRolePreview(role('viewer'), null, BUILTIN_ROLE_GRANTS)).toEqual({
      kind: 'unavailable',
      reason: 'catalogue-unavailable',
    });
  });

  it('takes the grant table as given, so a caller can preview a role code the table lacks', () => {
    const grants = new Map<string, readonly string[]>([['viewer', ['case:read']]]);

    expect(buildRolePreview(role('tenant_admin'), [permission('case:read')], grants)).toEqual({
      kind: 'unavailable',
      reason: 'unknown-role',
    });
    expect(buildRolePreview(role('viewer'), [permission('case:read')], grants)).toEqual({
      kind: 'ready',
      role: role('viewer'),
      omitted: 0,
      groups: [
        {
          resource: 'case',
          permissions: [{ code: 'case:read', description: 'case:read (test)', sensitive: false }],
        },
      ],
    });
  });
});

describe('BUILTIN_ROLE_GRANTS (09.7.1-T2)', () => {
  it('transcribes every role of contracts/permissions.json, in the contract’s order', () => {
    expect([...BUILTIN_ROLE_GRANTS.keys()]).toEqual(
      CATALOGUE_DOCUMENT.roles.map((entry) => entry.code),
    );
    expect(BUILTIN_ROLE_GRANTS.size).toBe(CATALOGUE_DOCUMENT.roles.length);
  });

  it('matches every role’s grant list exactly', () => {
    for (const contractRole of CATALOGUE_DOCUMENT.roles) {
      expect(BUILTIN_ROLE_GRANTS.get(contractRole.code)).toEqual(contractRole.permissions);
    }
  });

  it('names only codes the contract catalogue defines', () => {
    const defined = new Set(CATALOGUE_DOCUMENT.permissions.map((entry) => entry.code));
    const missingGrants = [...BUILTIN_ROLE_GRANTS].flatMap(([code, grants]) =>
      grants.filter((grant) => !defined.has(grant)).map((grant) => `${code}: ${grant}`),
    );

    expect(missingGrants).toEqual([]);
  });
});
