-- 0055_tenant_policy_roles.sql
--
-- WHY: `tenant_isolation` on public.security_event (0053) and on public.healing_llm_call /
-- public.healing_llm_budget (0054) was declared without a `TO` clause, so it applied to PUBLIC —
-- and `evidgate_operator` holds SELECT on all three tables (0001's default privileges, plus the
-- explicit grants at the end of 0054) while being free to `set evidgate.org_id` itself. "PUBLIC"
-- therefore let the audited-read role reach every tenant's security events, LLM attempts and budget
-- counters. Nothing reads these three tables as the operator: the tenant routes and the recorder
-- run as `evidgate_app`, so the operator grant never bought a read path, only an exposure.
--
-- WHAT: each policy is dropped and re-created naming `evidgate_app` alone, keeping the predicate and
-- the USING/WITH CHECK semantics of the file that introduced it (0053 declared USING only, and a
-- policy's WITH CHECK defaults to its USING, so writing both out below changes nothing for the app
-- role or the owner). The operator's SELECT is revoked per table afterwards. `force row level
-- security` and the `_owner_backfill` policies stay as 0053/0054 left them: the migration role still
-- writes fixtures and privileged backfills, and the owner policy is the reason FORCE does not lock
-- it out.
--
-- The policy keeps the name `tenant_isolation` rather than the house policy naming rule's
-- `<table>_app_tenant`: this file corrects the role clause of one existing policy, so the same name
-- keeps the history readable as one policy and keeps the lint's exact `(file, policy, table)`
-- exemption keys pointing at it. A table created from now on follows the house policy naming rule
-- and names its own policy.
--
-- ORDER: migrate before app. The old policy admitted `evidgate_app` through PUBLIC and the new one
-- names it explicitly with the same predicate, so neither order refuses an application statement;
-- the file is applied first so no window exists in which a policy names a role the database lacks.
--
-- LOCKS: dropping and re-creating a policy is an ALTER TABLE-class change that needs ACCESS EXCLUSIVE
-- on a table live traffic writes (`security_event` inserts, the ledger writes, the budget update), so
-- every statement below is bounded by the `lock_timeout` that follows. Exit 3 (timeout, file rolled
-- back and unjournalled) means "apply again while the table is quiet", not a defect.
--
-- NOT FIXED HERE: 0001's `alter default privileges for role evidgate_owner in schema public grant
-- select on tables to evidgate_operator` still hands every FUTURE table to the operator. A future
-- table is safe only because its tenant policy names the app role and its operator decision is made
-- in the creating file; changing the default instead would silently strip tables whose operator read
-- is deliberate (`schema_migration`, the probe table's operator read policy). The lint in
-- scripts/check-rls.py (`policy_no_role`, `operator_select_no_policy`) refuses the next table that
-- repeats either mistake.
--
-- Forward-only: `drop policy if exists` + `create policy` and a repeatable `revoke` are the statements
-- this file runs; they state the role clause and the operator grant, and there is no down migration.

set local lock_timeout = '5s';

drop policy if exists tenant_isolation on public.security_event;
create policy tenant_isolation on public.security_event
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists tenant_isolation on public.healing_llm_call;
create policy tenant_isolation on public.healing_llm_call
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists tenant_isolation on public.healing_llm_budget;
create policy tenant_isolation on public.healing_llm_budget
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- The policy above already confines these rows to the app role; the revoke removes the grant that
-- made the missing role clause dangerous in the first place, so a future edit that widens a policy
-- back to PUBLIC still cannot read a tenant row as the operator. No column-level grant exists on
-- these tables, so the table-level revoke is complete.
revoke all on public.security_event from evidgate_operator;
revoke all on public.healing_llm_call from evidgate_operator;
revoke all on public.healing_llm_budget from evidgate_operator;
