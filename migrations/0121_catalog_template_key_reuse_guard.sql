-- 0121_catalog_template_key_reuse_guard.sql
--
-- WHY: a template is addressed by `(kind, name, version)` inside the list a tenant sees, which is its own org rows plus
-- every built-in. 0118's keys cannot relate the two sides (`org_id` NULL makes the composite key blind to a built-in, and
-- the partial unique index covers built-ins only), so an org row could carry a built-in's key and one org's list would
-- show two rows under one key. No tenant writer exists yet; this rule is in place before one does.
--
-- SHAPE: a row trigger on `public.template`, before insert and before an update of the four key columns. An org row
-- (`org_id` not null) is refused when a built-in holds its key; a built-in row is refused when any org row holds its
-- key. The refusal is SQLSTATE 23505 with constraint name `template_key_reuse`, the code a unique key would raise.
--
-- RACE: two transactions inserting the two sides at once would each miss the other's uncommitted row, so the check
-- takes a transaction-scoped advisory lock on the key's hash first; a loser then sees the winner's committed row.
-- The lock is held until commit and covers only that key.
--
-- ROLE: the function is invoker-rights with an empty `search_path`; only the owner role writes this table (the app
-- role holds SELECT only), and the owner policy lets the check see every org's rows. No grant or policy changes.
--
-- LOCKS: creating a trigger takes a SHARE ROW EXCLUSIVE lock on the table; `lock_timeout` bounds its wait. Existing
-- rows are not rescanned: no writer could have produced a reused key. Rollback: drop the trigger and the function.
--
-- Idempotent in the shape the README's file contract allows: `create or replace function` and
-- `drop trigger if exists` before `create trigger`.

set local lock_timeout = '5s';

create or replace function public.template_key_reuse_guard() returns trigger
  language plpgsql
  set search_path = ''
as $$
begin
  perform pg_advisory_xact_lock(
    hashtextextended('template_key:' || new.kind || '/' || new.name || '/' || new.version::text, 0));

  if new.org_id is not null and exists (
       select 1 from public.template t
        where t.org_id is null and t.kind = new.kind and t.name = new.name and t.version = new.version) then
    raise exception 'template key (%, %, %) belongs to a built-in template', new.kind, new.name, new.version
      using errcode = '23505', constraint = 'template_key_reuse';
  end if;

  if new.org_id is null and exists (
       select 1 from public.template t
        where t.org_id is not null and t.kind = new.kind and t.name = new.name and t.version = new.version) then
    raise exception 'template key (%, %, %) is already used by an org template', new.kind, new.name, new.version
      using errcode = '23505', constraint = 'template_key_reuse';
  end if;

  return new;
end
$$;

revoke all on function public.template_key_reuse_guard() from public;

drop trigger if exists template_key_reuse_guard on public.template;
create trigger template_key_reuse_guard
  before insert or update of org_id, kind, name, version on public.template
  for each row
  execute function public.template_key_reuse_guard();
