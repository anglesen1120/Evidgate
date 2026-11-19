-- 0111_identity_suite_plan_read_permission.sql
--
-- WHY: `evidgate manifest` builds a run manifest from a suite revision's members, and the only credential a CI job holds
-- is the short-lived `ci_producer` token. The existing revision read needs `suite:read`, which that role must not hold
-- (it would also expose the whole revision and every suite of the project). So the plan read gets its own permission,
-- `suite:plan_read`, granted to `ci_producer` and to every built-in role that already reads suites, so a human can debug
-- the same page.
--
-- WHAT: one catalogue row, its grants, the widened ingest-token scope menu and the read index of the new route.
--
--   * `permission` gains `suite:plan_read`; `SeedMatrixTests` diffs the grants against `contracts/permissions.json`.
--   * `ci_producer` gains the grant; every built-in member role that holds `suite:read` gains it too (viewer,
--     qa_engineer, test_maintainer, release_approver, tenant_admin today), read from the rows rather than listed here, so
--     a catalogue that moves `suite:read` between roles does not need this file edited.
--   * `ingest_token_scopes_check` widens from four codes to five, because a token's menu is the `ci_producer` set: a
--     token that could not carry `suite:plan_read` would mint a producer token without it. Existing rows already satisfy
--     the wider check, so it is replaced without a backfill. A token minted before this file holds no plan scope, so it
--     answers no plan read until it is re-minted.
--   * `suite_member_plan_idx (org_id, suite_revision_id, ordinal, id)` serves the plan page's keyset order. The older
--     `suite_member_order_idx (suite_revision_id, ordinal)` stays: it leads with the revision id alone, which the
--     revision-detail aggregate still reads.
--
-- ORDER: additive for every existing reader — no handler loses a permission — so migrate before deploying the CLI that
-- walks the plan. Every API and Worker pod must run the new code before the CI exchange can mint `suite:plan_read`: a pod
-- still on the previous build does not know the permission and refuses the token, so a CI job fails for one token TTL
-- until the rollout completes.
--
-- ROLLBACK: the CLI first, then drop the index and re-add the four-code constraint. The five-code constraint must stay
-- once any token carries `suite:plan_read`, because narrowing it back would leave those rows invalid. The grant and the
-- catalogue row may stay, since a permission no route reads grants nothing.

set local lock_timeout = '5s';

do $seed$
declare
  permissions jsonb := '[
    {"code": "suite:plan_read", "resource": "suite", "action": "plan_read",
     "description": "Read a suite revision''s execution plan — each member''s case id, variant hash and pinned case revision — for manifest generation (00 §9.3, 04.2.2-T3). It never substitutes for `suite:read`.",
     "sensitive": false, "reserved": false, "tenant_assignable": true}
  ]'::jsonb;
begin
  insert into public.permission (code, resource, action, description, sensitive, reserved, tenant_assignable)
  select p.code, p.resource, p.action, p.description, p.sensitive, p.reserved, p.tenant_assignable
    from jsonb_to_recordset(permissions)
      as p(code text, resource text, action text, description text,
           sensitive boolean, reserved boolean, tenant_assignable boolean)
  on conflict (code) do nothing;

  -- The built-in service role the CLI's credential is minted from.
  insert into public.role_permission (id, org_id, role_id, permission_code)
  select uuidv7(), null, r.id, 'suite:plan_read'
    from public.role r
   where r.org_id is null and r.code = 'ci_producer'
  on conflict (role_id, permission_code) do nothing;

  -- Every built-in member role that may already read a suite revision may read its plan too.
  insert into public.role_permission (id, org_id, role_id, permission_code)
  select uuidv7(), null, granted.role_id, 'suite:plan_read'
    from public.role_permission granted
    join public.role r on r.id = granted.role_id and r.org_id is null
   where granted.org_id is null and granted.permission_code = 'suite:read'
  on conflict (role_id, permission_code) do nothing;
end
$seed$;

-- The token's scope menu is the ci_producer set, so a fifth code joins the closed check. Only the menu and the
-- cardinality widen: a token minted before this migration already satisfies the new check.
alter table public.ingest_token drop constraint if exists ingest_token_scopes_check;
alter table public.ingest_token
  add constraint ingest_token_scopes_check check (
    cardinality(scopes) between 1 and 5
    and scopes <@ array['event:write', 'artifact:write', 'run:create', 'decision:read', 'suite:plan_read']::text[]);

-- The plan page's keyset order (ordinal, id) inside one tenant and revision. org_id leads because the policy and the
-- statement both filter it, so the page is an index range rather than a filter over the tenant's members.
create index if not exists suite_member_plan_idx
    on public.suite_member (org_id, suite_revision_id, ordinal, id);
