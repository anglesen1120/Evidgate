-- 0040_identity_authoring_permissions.sql
--
-- Owner: 13.1.1-T2.1 (the foundation slice of the authoring CRUD API). Seeds the four catalogue strings
-- 00 §2:60 adds for the authoring feature (CR-D-2, 13 §13 CR-2) and grants each to the built-in roles that
-- line names. There is no DDL here: the authoring tables arrived in 0039, and this file only completes the
-- catalogue those tables' routes are authorized against, so `authoring:write` exists before a handler
-- declares `[Permission(Permissions.Authoring.Write)]` (the 0030 `quarantine:write` precedent).
--
--   * `authoring:read`  — every member role (`viewer`, `qa_engineer`, `test_maintainer`,
--     `release_approver`, `tenant_admin`): the tree, its items and its cases are not secret, and a viewer
--     reads the coverage report the tree produces (00 §2:50 keeps secrets out, not authored content).
--   * `authoring:write` — `qa_engineer`, `test_maintainer`, `tenant_admin`: the same triple that writes the
--     catalog's drafts and suites. `release_approver` writes nothing here, matching its read-only cells in
--     03 §4.5:121-136.
--   * `authoring:generate` — the same triple, as its own code because generation spends AI credits
--     (08.5.5): a code of its own keeps the spend separable from the write power instead of hiding it
--     inside `authoring:write`.
--   * `script:generate` — `test_maintainer`, `tenant_admin` only: it writes to a customer repository as a
--     pull request (13.5.1), which is the approve-shaped power 03 §4.5 gives `release_approver` no part of.
--     `qa_engineer` is deliberately absent: it drafts, it does not open PRs against customer code.
--
-- The mapping is 00 §2:60's `[INFERENCE]` note (it mirrors 03 §4.5); `contracts/permissions.json` carries
-- the same rows and `tests/Identity.IntegrationTests/SeedMatrixTests` diffs the seeded catalogue against
-- that file field by field, so the descriptions below must stay identical to the JSON entries. The
-- end-to-end mapping is also pinned here by `tests/Tenancy.IntegrationTests/AuthoringPermissionSeedTests`.
--
-- The migration runs as `evidgate_owner`, whose `permission_owner_backfill` /
-- `role_permission_owner_backfill` policies (0010) permit these writes under FORCE row level security, and
-- the app role's catalogue grants are 0010's (`select` on `permission`, `select`/`insert`/`update` on
-- `role_permission`): nothing here widens a grant.
--
-- The rows sit in a `do` block tagged `$seed$` (the marker 0010 and 0030 use): the catalogue suites
-- (`tests/Identity.IntegrationTests/CatalogueTestBase`) truncate the catalogue tables between tests and
-- re-apply every migration's seed block, so a permission added by a later migration survives the reset. The
-- rows are embedded literally rather than derived from another file, so the migration is self-contained
-- like 0010's catalogue block. `on conflict … do nothing` (never `do update`) makes a re-run a no-op and
-- keeps an operator's deliberate catalogue edit, which a later migration must not silently overwrite.
--
-- Lock levels: four single-row INSERTs into `public.permission` and thirteen into
-- `public.role_permission`, each taking ROW EXCLUSIVE on its own table and a key-share lock on the unique
-- index entry it probes. Both tables are tiny platform reference data, the inserts run once inside the
-- Migrator's single transaction, and no reader of them is blocked: nothing here is ACCESS EXCLUSIVE.

do $seed$
declare
  permissions jsonb := $permissions$
[
  {"code": "authoring:generate", "resource": "authoring", "action": "generate", "description": "Generate authoring content — feature nodes, checklist items and manual test cases — from an imported or AI-drafted source (13.3.1). Generation spends AI credits (08.5.5), so it is its own code.", "sensitive": false, "reserved": false, "tenant_assignable": true},
  {"code": "authoring:read", "resource": "authoring", "action": "read", "description": "Read the feature tree, its checklist items and its manual test cases (00 §2, 13.1.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
  {"code": "authoring:write", "resource": "authoring", "action": "write", "description": "Create, change, reorder, move, retire and delete feature nodes, checklist items and manual test cases (00 §2, 13.1.1).", "sensitive": false, "reserved": false, "tenant_assignable": true},
  {"code": "script:generate", "resource": "script", "action": "generate", "description": "Generate a test script from a manual test case and open it against the customer repository as a pull request (13.5.1).", "sensitive": false, "reserved": false, "tenant_assignable": true}
]
$permissions$::jsonb;
  grants jsonb := $grants$
[
  {"role_code": "viewer", "permission_code": "authoring:read"},
  {"role_code": "qa_engineer", "permission_code": "authoring:generate"},
  {"role_code": "qa_engineer", "permission_code": "authoring:read"},
  {"role_code": "qa_engineer", "permission_code": "authoring:write"},
  {"role_code": "test_maintainer", "permission_code": "authoring:generate"},
  {"role_code": "test_maintainer", "permission_code": "authoring:read"},
  {"role_code": "test_maintainer", "permission_code": "authoring:write"},
  {"role_code": "test_maintainer", "permission_code": "script:generate"},
  {"role_code": "release_approver", "permission_code": "authoring:read"},
  {"role_code": "tenant_admin", "permission_code": "authoring:generate"},
  {"role_code": "tenant_admin", "permission_code": "authoring:read"},
  {"role_code": "tenant_admin", "permission_code": "authoring:write"},
  {"role_code": "tenant_admin", "permission_code": "script:generate"}
]
$grants$::jsonb;
begin
  insert into public.permission (code, resource, action, description, sensitive, reserved, tenant_assignable)
  select p.code, p.resource, p.action, p.description, p.sensitive, p.reserved, p.tenant_assignable
    from jsonb_to_recordset(permissions)
      as p(code text, resource text, action text, description text,
           sensitive boolean, reserved boolean, tenant_assignable boolean)
  on conflict (code) do nothing;

  -- The built-in roles are 0010's rows (`org_id is null`); an org-scoped custom role with the same code
  -- never matches here, and a tenant that narrows a built-in role keeps its choice on a re-run.
  insert into public.role_permission (id, org_id, role_id, permission_code)
  select uuidv7(), null, r.id, granted.permission_code
    from jsonb_to_recordset(grants) as granted(role_code text, permission_code text)
    join public.role r on r.org_id is null and r.code = granted.role_code
  on conflict (role_id, permission_code) do nothing;
end
$seed$;
