-- 0079_usage_quota_counter_guard.sql
--
-- The counter's numbers only move one way inside a period, and this trigger is what makes that a property
-- of the table rather than a promise of the four statements that write it. `used` is a running total a
-- rollback or a repair can only ever have over-counted: lowering it would hand out units the period
-- already spent. `quota_limit` moves only through the audited raise, whose own `do update … where
-- excluded.quota_limit > c.quota_limit` is the rule this refuses to let a hand-written UPDATE bypass.
-- `blocked_at` is the one stamp that legitimately clears, and only as part of that raise: a counter with
-- the same limit is still blocked, so clearing it would make the gate admit work it must refuse.
--
-- The column grant already keeps the app role out of the scope columns and out of `warn_ratio`; this
-- trigger closes the half a grant cannot express, the direction a legal column may move in (D2). It is a
-- security-invoker BEFORE UPDATE trigger with an empty `search_path`, so no caller can shadow what it
-- reads, and it costs one comparison per counter write — the same statement it guards.
--
-- Idempotent like 0077: `set local lock_timeout` bounds the DDL wait, the function is replaced and the
-- trigger is dropped before it is created, so a re-run changes nothing.

set local lock_timeout = '5s';

create or replace function evidgate.usage_quota_counter_guard() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
  as $$
  begin
    if new.used < old.used then
      raise exception 'quota_counter used may not decrease (% -> %)', old.used, new.used
        using errcode = '23514';
    end if;

    if new.quota_limit < old.quota_limit then
      raise exception 'quota_counter quota_limit may only move up (% -> %)', old.quota_limit, new.quota_limit
        using errcode = '23514';
    end if;

    if old.blocked_at is not null and new.blocked_at is null and new.quota_limit <= old.quota_limit then
      raise exception 'quota_counter blocked_at may only clear on a raise'
        using errcode = '23514';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.usage_quota_counter_guard() from public;

drop trigger if exists quota_counter_monotone_guard on public.quota_counter;
create trigger quota_counter_monotone_guard
  before update on public.quota_counter
  for each row execute function evidgate.usage_quota_counter_guard();
