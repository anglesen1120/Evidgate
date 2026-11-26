-- 0134_identity_github_removal.sql
--
-- Retires the GitHub Actions OIDC exchange store: the pre-tenant binding resolver, the expired-issue purger, the
-- `ci_token_issue` replay cache with its run index, and the `ci_binding` table. Project ingest tokens
-- (`ingest_token`, `evidgate_resolve.resolve_ingest_token`) are not touched.
--
-- Permissions: `ci_binding:write` and `ingest_token:write` stay in the catalogue and in every role that holds them.
-- `ci_binding:write` is still the shared gate of other integration operations, so no `permission` or
-- `role_permission` row changes here. `audit_log` is append-only and has no reference to these tables, so earlier
-- `ci_binding.*` audit rows keep their text.
--
-- Order: dedicated functions, grants, the run index, then `ci_token_issue` before `ci_binding` (the issue table
-- holds the foreign key). No CASCADE: a dependent object this file does not know about fails the migration
-- instead of being dropped with the table.
--
-- LOCKS: ACCESS EXCLUSIVE on `ci_token_issue` and `ci_binding` until commit; both are written only by the removed
-- exchange, so stop the API and Worker of the previous release before applying. 5 s lock_timeout; a blocked file
-- rolls back whole and re-runs. Re-running a journalled file is a no-op, and every statement is guarded.
set local lock_timeout = '5s';

-- Grants are withdrawn first, each guarded so a replay on a partly cleaned database still completes. The two
-- definers are dropped before the tables: no caller keeps a routine whose body names a dropped relation.
do $$
begin
  if to_regprocedure('evidgate_resolve.resolve_ci_binding(text, bigint, bytea[])') is not null then
    revoke all on function evidgate_resolve.resolve_ci_binding(text, bigint, bytea[]) from evidgate_app;
  end if;
  if to_regprocedure('evidgate.purge_expired_ci_token_issue(integer)') is not null then
    revoke all on function evidgate.purge_expired_ci_token_issue(integer) from evidgate_worker_sched;
  end if;
  if to_regclass('public.ci_token_issue') is not null then
    revoke all on public.ci_token_issue from evidgate_app, evidgate_operator;
  end if;
  if to_regclass('public.ci_binding') is not null then
    revoke all on public.ci_binding from evidgate_app, evidgate_operator;
  end if;
end
$$;

drop function if exists evidgate_resolve.resolve_ci_binding(text, bigint, bytea[]);
drop function if exists evidgate.purge_expired_ci_token_issue(integer);
drop index if exists public.ci_token_issue_run_idx;
drop table if exists public.ci_token_issue;
drop table if exists public.ci_binding;
