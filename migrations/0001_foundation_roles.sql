-- 0001_foundation_roles.sql
--
-- The four Evidgate database roles and their schema-level privileges
-- (00 §5.4.3, ADR-0003, TL T80). Owner: 01.4.1-T2.1/T2.2. Runs as
-- evidgate_owner and is idempotent (safe against a database whose roles already
-- exist).
--
-- LOGIN and passwords are deliberately NOT part of this migration:
--   * a credential is environment state, never a committed SQL file (00 §5.7 S3,
--     01 §6) — a password in a migration would leak into git history forever;
--   * the dev bootstrap (`scripts/dev/db-init.sh`) and the deploy gate (10.2.2)
--     create/grant LOGIN + a password as superuser, outside version control;
--   * so this file creates the roles without LOGIN and never touches existing
--     LOGIN state.
--
-- Least privilege (00 §5.4.3) and who enforces it:
--   evidgate_owner        owns every object and runs migrations. Needs CREATEROLE
--                         (this file, and every later migration that creates or
--                         alters the three application roles). Never SUPERUSER,
--                         never BYPASSRLS, no CREATEDB.
--   evidgate_app          application traffic. Not an object owner, no BYPASSRLS,
--                         no DDL (no CREATE on any schema), NOINHERIT so a
--                         membership granted for convenience never silently
--                         escalates. Gets DML on business tables through the
--                         default privileges below plus EXECUTE on the evidgate
--                         helpers (0002).
--   evidgate_operator     audited read-only support path (TL T76): SELECT only —
--                         no INSERT/UPDATE/DELETE, no DDL, no BYPASSRLS.
--   evidgate_worker_sched dedicated EXECUTE-only target for evidgate_owner
--                         SECURITY DEFINER sweeps (TL T80, e.g. 01.5.1's
--                         evidgate.purge_expired_idempotency). No table and no
--                         schema-write privilege of its own; EXECUTE is granted
--                         per function by the migration that creates it.
--
-- The attributes are set where PostgreSQL allows it: at CREATE ROLE (SUPERUSER
-- and BYPASSRLS default to "no" and only a SUPERUSER may change them), and by
-- the superuser bootstrap for a role it creates. A migration running as
-- evidgate_owner can therefore not "correct" an existing role's SUPERUSER or
-- BYPASSRLS flag, which is why the block below refuses to continue when one of
-- the four roles carries an attribute that would break tenant isolation.

do $$
begin
  if not exists (select 1 from pg_catalog.pg_roles where rolname = 'evidgate_owner') then
    create role evidgate_owner nologin createrole;
  end if;
  if not exists (select 1 from pg_catalog.pg_roles where rolname = 'evidgate_app') then
    create role evidgate_app nologin noinherit;
  end if;
  if not exists (select 1 from pg_catalog.pg_roles where rolname = 'evidgate_operator') then
    create role evidgate_operator nologin noinherit;
  end if;
  if not exists (select 1 from pg_catalog.pg_roles where rolname = 'evidgate_worker_sched') then
    create role evidgate_worker_sched nologin noinherit;
  end if;
end
$$;

-- Fail the migration loudly instead of running the platform on a role that
-- bypasses RLS (DECISIONS F5) or could create databases.
do $$
declare
  offenders text;
begin
  select string_agg(rolname || ' (' || concat_ws(', ',
           case when rolsuper then 'SUPERUSER' end,
           case when rolbypassrls then 'BYPASSRLS' end,
           case when rolcreatedb then 'CREATEDB' end,
           case when rolcreaterole and rolname <> 'evidgate_owner' then 'CREATEROLE' end) || ')', '; ')
    into offenders
    from pg_catalog.pg_roles
   where rolname in ('evidgate_owner', 'evidgate_app', 'evidgate_operator', 'evidgate_worker_sched')
     and (rolsuper or rolbypassrls or rolcreatedb or (rolcreaterole and rolname <> 'evidgate_owner'));

  if offenders is not null then
    raise exception 'evidgate roles carry unsafe attributes (00 §5.4.3): %', offenders;
  end if;
end
$$;

-- Schema `public` (PostgreSQL 15+ owns it by pg_database_owner, so the database
-- owner keeps CREATE while everyone else loses it).
revoke create on schema public from public;
revoke all on schema public from evidgate_app, evidgate_operator, evidgate_worker_sched;
grant usage on schema public to evidgate_app, evidgate_operator;

-- Default privileges for business tables created by the migration role in
-- `public`: app gets DML, operator gets SELECT. A module creating its own schema
-- repeats these statements for that schema.
alter default privileges for role evidgate_owner in schema public
  grant select, insert, update, delete on tables to evidgate_app;
alter default privileges for role evidgate_owner in schema public
  grant usage, select on sequences to evidgate_app;
alter default privileges for role evidgate_owner in schema public
  grant select on tables to evidgate_operator;

-- Functions are EXECUTE to PUBLIC by default, and ALTER DEFAULT PRIVILEGES
-- entries are additive: a per-schema REVOKE cannot subtract from that built-in
-- global default. Only the schema-less form can, so functions created by
-- evidgate_owner start owner-only in every schema. Each migration that creates a
-- function grants EXECUTE explicitly to the role that may call it — the app for
-- the RLS helpers and resolvers, evidgate_worker_sched for the owner
-- SECURITY DEFINER sweeps (TL T80).
alter default privileges for role evidgate_owner revoke execute on functions from public;

-- The migration journal is read-only for the application roles: the readiness
-- check (01.1.2-T1.3) reads the head as evidgate_app. The journal table is
-- created before this migration runs, so it carries no default privileges; the
-- explicit revoke keeps it write-free even if the table is recreated once the
-- default privileges above are in place.
grant select on public.schema_migration to evidgate_app, evidgate_operator;
revoke insert, update, delete, truncate, references, trigger
  on public.schema_migration from evidgate_app, evidgate_operator, evidgate_worker_sched;
