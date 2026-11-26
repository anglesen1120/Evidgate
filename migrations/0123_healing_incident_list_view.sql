set local lock_timeout = '5s';

-- security_invoker keeps the caller's row-level security on incident and incident_attempt, so no org predicate has
-- to be repeated here. The first failing attempt is stored on the incident; incident_attempt holds only the retries.
create or replace view public.incident_list with (security_invoker = true) as
select i.org_id, i.id, i.project_id, i.run_id, i.attempt_id, i.case_id, i.case_revision_id,
       i.execution_key, i.engine, i.state, i.context_state, i.created_at, i.updated_at, i.version,
       (1 + (select count(*) from public.incident_attempt a
             where a.org_id = i.org_id and a.incident_id = i.id))::integer as attempt_count
  from public.incident i;

-- Default privileges also hand new relations to the other roles; only the app role reads incidents.
revoke all on public.incident_list from public, evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched;
grant select on public.incident_list to evidgate_app;

-- A state-filtered page ordered by created_at desc, id would otherwise sort the rows incident_project_state returns,
-- because that index stops at the state. Carrying the page order after the state lets the page stream and stop at the limit.
create index if not exists incident_project_state_list
  on public.incident (org_id, project_id, state, created_at desc, id);
