-- 0002_foundation_evidgate_schema.sql
--
-- The `evidgate` helper schema (NULL-safe RLS helpers, TL T2) and the empty
-- `evidgate_resolve` schema for pre-tenant SECURITY DEFINER resolvers (TL T1).
-- Owner: 01.4.1-T3.1/T3.2. Runs as evidgate_owner; idempotent.
--
-- Schema `evidgate` — owned by evidgate_owner, USAGE granted to the three
-- application roles, CREATE withheld from all of them. It holds the helpers the
-- RLS policies call (TL T2: never raw current_setting in a policy) and, later,
-- owner SECURITY DEFINER functions.
--
-- Schema `evidgate_resolve` — pre-tenant resolution (00 §5.4.8): the app role is
-- present before any tenant context exists, so it must EXECUTE the resolvers
-- (login callback, `POST /v1/ci/token`, ingest-token auth, GitHub webhooks,
-- invite activation) and nothing else. The grant pattern below is the whole
-- contract: USAGE + EXECUTE for evidgate_app, no CREATE, no table privileges.
-- The resolver functions themselves arrive with 02.6.1-T3 and 03.4.1; every one
-- of them must be created by evidgate_owner, SECURITY DEFINER, and return only
-- (org_id, project_id, binding_id, status).

create schema if not exists evidgate authorization evidgate_owner;
revoke all on schema evidgate from public;
revoke create on schema evidgate from evidgate_app, evidgate_operator, evidgate_worker_sched;
grant usage on schema evidgate to evidgate_app, evidgate_operator, evidgate_worker_sched;

-- TL T2: NULL-safe tenant helpers. `current_setting(..., true)` returns NULL
-- instead of raising when the setting was never set, and NULLIF maps the empty
-- string to NULL, so a request without a tenant context sees zero rows — never
-- an error and never every row.
create or replace function evidgate.current_org_id() returns uuid
  language sql
  stable
  as $$ select nullif(current_setting('evidgate.org_id', true), '')::uuid $$;

create or replace function evidgate.current_project_id() returns uuid
  language sql
  stable
  as $$ select nullif(current_setting('evidgate.project_id', true), '')::uuid $$;

-- New functions are EXECUTE to PUBLIC by default; close that. The two helpers
-- are granted explicitly, per function, to the roles that evaluate policies.
revoke all on function evidgate.current_org_id() from public;
revoke all on function evidgate.current_project_id() from public;
grant execute on function evidgate.current_org_id() to evidgate_app, evidgate_operator;
grant execute on function evidgate.current_project_id() to evidgate_app, evidgate_operator;

-- Default EXECUTE to PUBLIC (PostgreSQL's built-in function default) is revoked
-- globally in 0001: ALTER DEFAULT PRIVILEGES entries are additive per schema, so
-- a per-schema REVOKE cannot subtract from the built-in default. Consequence:
-- every function created by evidgate_owner starts owner-only, which is exactly
-- the T80 pattern — the migration that creates a function grants EXECUTE to
-- whoever may call it (evidgate_app here, evidgate_worker_sched for sweeps).

create schema if not exists evidgate_resolve authorization evidgate_owner;
revoke all on schema evidgate_resolve from public;
revoke create on schema evidgate_resolve from evidgate_app, evidgate_operator, evidgate_worker_sched;
grant usage on schema evidgate_resolve to evidgate_app;

-- The grant pattern for T1: every future resolve function is EXECUTE-able by
-- evidgate_app and by nobody else (PUBLIC is closed globally in 0001).
alter default privileges for role evidgate_owner in schema evidgate_resolve
  grant execute on functions to evidgate_app;
