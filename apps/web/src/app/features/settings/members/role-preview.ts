// 09.7.1-T2 (plan 09.7.1 §2 AC2, §5 members row, §10 T2.2; COORDINATOR-DECISION S73 option B):
// the role scope preview. It answers "what does this role let me do" before an invite or a role
// change is saved, and it is DISPLAY ONLY: nothing here gates a route, a tab or a button, and the
// panel says so. The server's own authorization stays the only authority (00 §5.6 W5, S2).
//
// Two data sources are joined, both of them published rather than inferred:
//
// 1. the grant list of the selected BUILT-IN role, from `contracts/permissions.json` — the single
//    source of the role/permission vocabulary (00 §2, 02 D2);
// 2. the LIVE catalogue the server serves (`GET /v1/permissions`, 02.2.1), which adds each code's
//    `description`, `sensitive`, `reserved` and `tenant_assignable` flags.
//
// A code the role grants but the live catalogue does not serve is counted as `omitted` instead of
// being shown with an invented description, and a preview that cannot be built honestly says so:
// a custom role (its grants are the org's own rows, which the contract does not publish), a role
// code the contract does not know, or an unavailable catalogue. Nothing is guessed.

import type { PermissionView, RoleView } from '@evidgate/sdk';

/**
 * What the preview can honestly say about one role: the grouped permissions it grants, or why it
 * cannot say anything.
 */
export type RolePreview =
  | {
      readonly kind: 'unavailable';
      /**
       * `custom-role`: the role is the org's own row, so `contracts/permissions.json` has no grant
       * list for it. `unknown-role`: no role was selected, or its code is not a built-in one in the
       * contract. `catalogue-unavailable`: the live catalogue could not be read, so no description
       * or `sensitive` flag exists to show.
       */
      readonly reason: 'custom-role' | 'unknown-role' | 'catalogue-unavailable';
    }
  | {
      readonly kind: 'ready';
      /** The role the preview describes. */
      readonly role: RoleView;
      /** Granted codes the live catalogue did not serve; counted, never rendered. */
      readonly omitted: number;
      /** The grants grouped by the catalogue's `resource`, in the contract's grant order. */
      readonly groups: readonly {
        readonly resource: string;
        readonly permissions: readonly {
          readonly code: string;
          readonly description: string;
          readonly sensitive: boolean;
        }[];
      }[];
    };

/**
 * The grants of every role in `contracts/permissions.json`, keyed by role `code`.
 *
 * The contract document is the single source of these strings, but it is deliberately NOT imported
 * here: the production bundle would then carry every `description` and role `name` of the
 * catalogue, and those strings are byte-identical to the ones
 * `fixtures/web-api/permissions-list/input.json` and `fixtures/web-api/roles-list/input.json`
 * record. `scripts/check-no-fixture-code.mjs` scans `dist/browser` for the corpus's strings, so
 * that import would fail the build on a leak that is not one.
 *
 * The table is therefore a transcription, and `role-preview.spec.ts` reads the contract document
 * with `node:fs` and asserts this table equals it — the same read-only, offline approach as
 * `shell/nav/nav-registry.spec.ts`. A contract change that is not carried here is a red suite, not
 * a silent drift.
 */
export const BUILTIN_ROLE_GRANTS: ReadonlyMap<string, readonly string[]> = new Map<
  string,
  readonly string[]
>([
  [
    'viewer',
    [
      'artifact:read',
      'authoring:read',
      'case:read',
      'decision:read',
      'organization:read',
      'policy:read',
      'project:read',
      'role:read',
      'run:read',
      'suite:plan_read',
      'suite:read',
    ],
  ],
  [
    'qa_engineer',
    [
      'ai_draft:request',
      'artifact:read',
      'artifact:read_sensitive',
      'artifact:write',
      'authoring:generate',
      'authoring:read',
      'authoring:write',
      'case:read',
      'decision:read',
      'draft:dry_run',
      'draft:merge_request',
      'draft:write',
      'healing:triage',
      'job:cleanup',
      'organization:read',
      'policy:read',
      'project:read',
      'role:read',
      'run:cancel',
      'run:create',
      'run:read',
      'suite:plan_read',
      'suite:read',
    ],
  ],
  [
    'test_maintainer',
    [
      'agent_pool:read',
      'ai_draft:request',
      'artifact:read',
      'artifact:read_sensitive',
      'artifact:write',
      'authoring:generate',
      'authoring:read',
      'authoring:write',
      'case:read',
      'case:write',
      'decision:read',
      'draft:dry_run',
      'draft:merge_request',
      'draft:write',
      'environment:write',
      'fixture:write',
      'healing:approve',
      'healing:revoke',
      'job:cleanup',
      'organization:read',
      'policy:read',
      'project:read',
      'quarantine:write',
      'role:read',
      'run:cancel',
      'run:create',
      'run:read',
      'script:generate',
      'suite:plan_read',
      'suite:read',
      'suite:write',
    ],
  ],
  [
    'release_approver',
    [
      'artifact:read',
      'artifact:read_sensitive',
      'authoring:read',
      'case:read',
      'decision:read',
      'organization:read',
      'policy:read',
      'policy:write',
      'project:read',
      'role:read',
      'run:read',
      'suite:plan_read',
      'suite:read',
    ],
  ],
  [
    'tenant_admin',
    [
      'agent_pool:read',
      'agent_pool:write',
      'ai_draft:request',
      'api_token:read',
      'api_token:write',
      'artifact:read',
      'artifact:write',
      'audit:read',
      'authoring:generate',
      'authoring:read',
      'authoring:write',
      'case:read',
      'case:write',
      'ci_binding:write',
      'decision:read',
      'draft:dry_run',
      'draft:merge_request',
      'draft:write',
      'environment:write',
      'fixture:write',
      'healing:configure',
      'ingest_token:read',
      'ingest_token:write',
      'membership:read',
      'membership:write',
      'organization:read',
      'organization:write',
      'policy:read',
      'project:create',
      'project:read',
      'project:write',
      'role:read',
      'role:write',
      'run:cancel',
      'run:create',
      'run:read',
      'script:generate',
      'security_event:read',
      'service_identity:read',
      'service_identity:write',
      'sso:read',
      'sso:write',
      'suite:plan_read',
      'suite:read',
      'suite:write',
      'support:approve',
      'support:read',
      'usage:read',
      'webhook:read',
      'webhook:write',
    ],
  ],
  [
    'ci_producer',
    ['artifact:write', 'decision:read', 'event:write', 'run:create', 'suite:plan_read'],
  ],
  ['agent', ['artifact:write', 'event:write', 'job:claim', 'job:execute']],
  ['platform_operator', ['support:grant', 'support:read']],
  ['billing_operator', ['billing:read', 'billing:write']],
]);

/**
 * Builds the preview of one role.
 *
 * The role's own identity is decided first, because it does not depend on the catalogue: no role →
 * `unknown-role`, a custom role → `custom-role`. Then the catalogue's availability, then whether
 * the contract knows the role's code — a built-in code that is not in {@link BUILTIN_ROLE_GRANTS}
 * is reported as `unknown-role` rather than shown empty, which would read as "grants nothing".
 *
 * The grants keep the contract's order (`contracts/permissions.json` lists them alphabetically),
 * grouped by the catalogue's server-declared `resource` on first appearance, so the panel's order
 * is stable across renders. A granted code the live catalogue does not serve is counted in
 * `omitted`; a duplicated catalogue row is shown once.
 *
 * @param role the selected role, or `undefined` when nothing is selected.
 * @param catalogue the live `GET /v1/permissions` answer, or `null` when that read failed.
 * @param builtinGrants the built-in grant table; {@link BUILTIN_ROLE_GRANTS} in the page.
 */
export function buildRolePreview(
  role: RoleView | undefined,
  catalogue: readonly PermissionView[] | null,
  builtinGrants: ReadonlyMap<string, readonly string[]>,
): RolePreview {
  if (role === undefined) {
    return { kind: 'unavailable', reason: 'unknown-role' };
  }
  if (!role.is_builtin) {
    return { kind: 'unavailable', reason: 'custom-role' };
  }
  const grants = builtinGrants.get(role.code);
  if (grants === undefined) {
    return { kind: 'unavailable', reason: 'unknown-role' };
  }
  if (catalogue === null) {
    return { kind: 'unavailable', reason: 'catalogue-unavailable' };
  }

  const permissionsByCode = new Map<string, PermissionView>();
  for (const permission of catalogue) {
    if (!permissionsByCode.has(permission.code)) {
      permissionsByCode.set(permission.code, permission);
    }
  }

  const groups = new Map<string, { code: string; description: string; sensitive: boolean }[]>();
  const seen = new Set<string>();
  let omitted = 0;
  for (const code of grants) {
    if (seen.has(code)) {
      continue;
    }
    seen.add(code);
    const permission = permissionsByCode.get(code);
    if (permission === undefined) {
      omitted += 1;
      continue;
    }
    const entry = {
      code: permission.code,
      description: permission.description,
      sensitive: permission.sensitive,
    };
    const group = groups.get(permission.resource);
    if (group === undefined) {
      groups.set(permission.resource, [entry]);
    } else {
      group.push(entry);
    }
  }

  return {
    kind: 'ready',
    role,
    omitted,
    groups: [...groups].map(([resource, permissions]) => ({ resource, permissions })),
  };
}
