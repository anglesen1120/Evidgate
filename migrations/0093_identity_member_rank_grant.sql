-- 0093_identity_member_rank_grant.sql
--
-- WHY: the member commands (`RemoveMember`, `UpdateMembership`, `RevokeRole`) refuse an actor whose admin rank in
-- the acting org is below the target's. The rank has one source, `evidgate.session_admin_rank(org, user)` (0091):
-- 2 = the built-in `tenant_admin` of that org, 1 = `membership:write` through another role, 0 = otherwise, and it
-- delegates the write probe to `evidgate.session_admin_holds_write`. The commands may not restate that rule in C#
-- (a second copy of a security rule drifts from the first), and neither function is a `security definer`, so the
-- question runs under the CALLER's privileges: without this file an `evidgate_app` transaction cannot ask it and
-- every member command would fail closed instead of comparing ranks. `0091` left both owner-only because only the
-- revoke-all definer asked them; this file adds the app role as their second caller and nothing else.
--
-- SCOPE: the grant widens who may run a READ, not what it can see. Both functions are invoker-rights and declare
-- `set search_path = ''`, so an app call is evaluated under the request's `evidgate.org_id` and the policies of
-- `membership`, `membership_role`, `role` and `role_permission` apply as usual. An org the scope does not cover
-- yields 0 (or false), even when its id is passed explicitly: the rank of another org's administrator is not
-- readable from inside this one. There is no write, no definer and no bypass — a caller with a tenant scope gains
-- no row it could not already select.
--
-- GRANTS: EXECUTE to `evidgate_app` on `session_admin_rank(uuid, uuid)` and `session_admin_holds_write(uuid, uuid)`
-- only. `session_admin_may_revoke`, `session_admin_may_revoke_all` and `session_revoke_audit_append` stay
-- owner-only (they are reached through the definers), and PUBLIC, `evidgate_operator` and `evidgate_worker_sched`
-- are restated as EXECUTE-less so a re-run of this file is a no-op.
-- `tests/Tenancy.IntegrationTests/DefinerExecuteScopeTests.cs` pins both grantee lists (owner and app alone) and
-- `tests/Tenancy.IntegrationTests/MemberRankGrantTests.cs` pins the answers inside and outside the scope.
--
-- Rollback: `revoke execute on function evidgate.session_admin_rank(uuid, uuid) from evidgate_app;` and
-- `revoke execute on function evidgate.session_admin_holds_write(uuid, uuid) from evidgate_app;` — the 0091 ACL.
-- No table, column, row, function body or policy changes.
--
-- `revoke` and `grant` are idempotent, so a re-run is a no-op; the brief function lock is bounded like 0089's.
set local lock_timeout = '5s';

revoke all on function evidgate.session_admin_holds_write(uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
grant execute on function evidgate.session_admin_holds_write(uuid, uuid) to evidgate_app;

revoke all on function evidgate.session_admin_rank(uuid, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;
grant execute on function evidgate.session_admin_rank(uuid, uuid) to evidgate_app;
