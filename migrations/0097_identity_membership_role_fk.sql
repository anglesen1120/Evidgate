-- 0097_identity_membership_role_fk.sql
--
-- WHY: `0011` left `membership_role.role_id` without a foreign key and left the "member role of this org" rule to
-- the command layer, so a row written around `GrantRole`/`InviteMember` could point at a role that does not exist,
-- at another org's custom role, or at a `staff`/`service` role. The evaluator ignores such a row, but the rank
-- helpers (`0093`, `0096`) read `role_id` directly, so the DB itself must refuse it.
--
-- SHAPE: the foreign key is the single-column, non-deferrable `role_id -> role (id)`. The composite tenant key
-- `(org_id, role_id) -> role (org_id, id)` that `0011` considered cannot hold for a built-in role (`org_id IS NULL`
-- never matches a composite key), and `0011` kept the column key-less for that reason alone. What a foreign key
-- cannot say, the trigger says: `role.kind = 'member'` and (`role.org_id` is null or equals the grant's `org_id`).
-- The key is `ON DELETE NO ACTION`: the app role holds no DELETE on `role` and no command deletes one, so a role
-- that is still granted cannot disappear (the owner's delete fails at the statement). The two schema scans carry a
-- documented allowance for this one key, as they do for `role_permission`.
--
-- The role side: a second trigger refuses to change `kind` or `org_id` of a role that a `membership_role` row still
-- references, so a violating pair cannot be created by editing the role afterwards.
--
-- SERIALISATION: both guards take a two-key advisory lock (`hashtext('evidgate.membership_role'), hashtext(role id)`;
-- shared for a grant insert, exclusive for a role change) before they read the other table, so a grant insert and a kind/org change of that role run in one order and the
-- second one's check sees the first one's committed row.
--
-- DATA: the file never rewrites or deletes rows. A pre-check counts the existing violations and raises with the
-- count; the operator fixes the rows and re-runs. The small membership_role table gets a directly validated key.
--
-- ERRORS: SQLSTATE `23514` (constraint name `membership_role_role_guard`) from the trigger, `23503` (`membership_role_role_fk`) from the key. The commands
-- resolve the role under RLS first, so these only fire on a race or a write around the commands; they are mapped to
-- the existing 409 `unknown_role`.
--
-- Deploy: any order, one transaction; the application never issues a row the trigger refuses. Rollback: drop the
-- two triggers, the two functions and the key; no data is transformed.
set local lock_timeout = '5s';

do $$
declare
  v_bad bigint;
begin
  select count(*) into v_bad
    from public.membership_role mr
    left join public.role r on r.id = mr.role_id
   where r.id is null
      or r.kind <> 'member'
      or (r.org_id is not null and r.org_id <> mr.org_id);
  if v_bad > 0 then
    raise exception 'membership_role: % existing row(s) reference a missing, non-member or foreign-org role; fix them before 0097', v_bad
      using errcode = '23514';
  end if;
end
$$;

-- membership_role is small and the pre-check proves validity, so add the validated key directly;
-- a separate validation migration would add deployment work without a useful lock saving here.
do $$
begin
  if not exists (
      select 1
        from pg_catalog.pg_constraint
       where conrelid = 'public.membership_role'::regclass
         and conname = 'membership_role_role_fk') then
    alter table public.membership_role
      add constraint membership_role_role_fk
      foreign key (role_id) references public.role (id);
  end if;
end
$$;

create or replace function evidgate.membership_role_guard_role() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    -- Shared: grants never block each other (a founder inserts several grants per transaction, and
    -- concurrent orgs would deadlock on an exclusive lock), but they exclude a role kind/org change.
    perform pg_catalog.pg_advisory_xact_lock_shared(
      pg_catalog.hashtext('evidgate.membership_role'), pg_catalog.hashtext(new.role_id::text));

    if not exists (
        select 1
          from public.role r
         where r.id = new.role_id
           and r.kind = 'member'
           and (r.org_id is null or r.org_id = new.org_id)) then
      raise exception 'membership_role_role_invalid: role % is not a member role of org %', new.role_id, new.org_id
        using errcode = '23514', constraint = 'membership_role_role_guard';
    end if;
    return new;
  end;
  $$;

revoke all on function evidgate.membership_role_guard_role() from public;
alter function evidgate.membership_role_guard_role() owner to evidgate_owner;

drop trigger if exists membership_role_guard_role_trg on public.membership_role;
create trigger membership_role_guard_role_trg
  before insert or update of role_id, org_id on public.membership_role
  for each row
  execute function evidgate.membership_role_guard_role();

create or replace function evidgate.role_guard_granted_scope() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    -- Exclusive against the shared lock of grant inserts; the WHEN clause limits it to kind/org changes.
    perform pg_catalog.pg_advisory_xact_lock(
      pg_catalog.hashtext('evidgate.membership_role'), pg_catalog.hashtext(new.id::text));

    if exists (
        select 1
          from public.membership_role mr
         where mr.role_id = new.id) then
      raise exception 'role_granted_scope_change: role % is granted to a membership', new.id
        using errcode = '23514', constraint = 'role_granted_scope_guard';
    end if;
    return new;
  end;
  $$;

revoke all on function evidgate.role_guard_granted_scope() from public;
alter function evidgate.role_guard_granted_scope() owner to evidgate_owner;

drop trigger if exists role_guard_granted_scope_trg on public.role;
create trigger role_guard_granted_scope_trg
  before update of kind, org_id on public.role
  for each row
  when (old.kind is distinct from new.kind or old.org_id is distinct from new.org_id)
  execute function evidgate.role_guard_granted_scope();
