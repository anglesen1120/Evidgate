-- Late usage metered after its month was billed is re-dated into the next open period instead of refused: a source
-- that meters after month-end is billed, never dropped, never dead-lettered, and never written into an issued month.
-- The period the row was metered for is kept in `original_period`, so every read of the ledger shows the move.
--
-- The decision lives in the 0087 insert guard, which already holds the requested period's shared advisory lock. The
-- walk takes that same lock for each candidate month before probing `billing_statement`, and an issue takes the
-- matching exclusive lock (0087), so a month this walk found open cannot be issued before this transaction commits.
-- Statements are never deleted (0092), so a month the probe found issued cannot reopen either.
--
-- The walk is bounded by the calendar: `9999-12` is the last period the `YYYY-MM` form can name, and a row with no
-- open month after it is refused under a named constraint rather than written into a billed month. The trigger runs
-- before the insert's `ON CONFLICT (org_id, billing_key) DO NOTHING`, so a replay of a key that is already on file
-- answers `Duplicates` only while the walk can name a month; a replay that also sits at the end of an exhausted chain
-- gets that same named refusal (nothing is written either way).
--
-- 0092's UPDATE/DELETE guard is untouched: a re-dated row is as immutable as any other ledger row.
--
-- Rollback: drop the two check constraints and the column, then re-create the 0087 body of
-- `evidgate.usage_ledger_entry_period_issued_guard()`. Deploy: any order, one transaction; the column takes the
-- table's ACCESS EXCLUSIVE lock, bounded by lock_timeout, and every existing row keeps `original_period` null.

set local lock_timeout = '5s';

alter table public.usage_ledger_entry
  add column if not exists original_period text;

-- The same shape the period column enforces, so a period read from `original_period` parses wherever `period` does.
alter table public.usage_ledger_entry
  drop constraint if exists usage_ledger_entry_original_period_check;
alter table public.usage_ledger_entry
  add constraint usage_ledger_entry_original_period_check
  check (original_period is null or original_period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$');

-- A re-date only ever moves forward, and the two columns compare as text exactly as their `YYYY-MM` form orders.
alter table public.usage_ledger_entry
  drop constraint if exists usage_ledger_entry_original_period_order_check;
alter table public.usage_ledger_entry
  add constraint usage_ledger_entry_original_period_order_check
  check (original_period is null or original_period < period);

create or replace function evidgate.usage_ledger_entry_period_issued_guard() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
  as $$
  declare
    candidate text := new.period;
  begin
    if pg_catalog.current_setting('transaction_isolation') <> 'read committed' then
      raise exception 'ledger rows are written under READ COMMITTED only, not %',
        pg_catalog.current_setting('transaction_isolation')
        using errcode = '25000';
    end if;

    perform pg_catalog.pg_advisory_xact_lock_shared(
      pg_catalog.hashtextextended(new.org_id::text || ':' || new.period, 0));

    if not exists (
      select 1 from public.billing_statement s
      where s.org_id = new.org_id and s.period = new.period
    ) then
      return new;
    end if;

    -- The move is the guard's decision, and only it can take the lock of the month the row ends up in: a writer that
    -- already named a period would be stating something it cannot observe, so it is refused instead of trusted.
    if new.original_period is not null then
      raise exception 'a ledger row is re-dated by its insert, so it cannot name original period % itself',
        new.original_period
        using errcode = '23514', constraint = 'usage_ledger_entry_original_period_supplied';
    end if;

    new.original_period := new.period;

    loop
      if candidate >= '9999-12' then
        raise exception 'usage period % has no open month after it', new.original_period
          using errcode = '23514', constraint = 'usage_ledger_entry_no_open_period';
      end if;

      candidate := pg_catalog.to_char(
        pg_catalog.to_date(candidate || '-01', 'YYYY-MM-DD') + interval '1 month', 'YYYY-MM');

      perform pg_catalog.pg_advisory_xact_lock_shared(
        pg_catalog.hashtextextended(new.org_id::text || ':' || candidate, 0));

      exit when not exists (
        select 1 from public.billing_statement s
        where s.org_id = new.org_id and s.period = candidate);
    end loop;

    new.period := candidate;

    return new;
  end;
  $$;

revoke all on function evidgate.usage_ledger_entry_period_issued_guard()
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;

drop trigger if exists usage_ledger_entry_period_issued on public.usage_ledger_entry;
create trigger usage_ledger_entry_period_issued
  before insert on public.usage_ledger_entry
  for each row execute function evidgate.usage_ledger_entry_period_issued_guard();
