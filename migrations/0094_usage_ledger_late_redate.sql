-- Late usage metered after its month was billed is re-dated into the next open period instead of refused: a source
-- that meters after month-end is billed, never dropped, never dead-lettered, and never written into an issued month.
-- The period the row was metered for is kept in `original_period`, so every read of the ledger shows the move.
--
-- The decision lives in the 0087 insert guard, which already holds the requested period's shared advisory lock. The
-- walk takes that same lock for each candidate month before probing `billing_statement`, and an issue takes the
-- matching exclusive lock (0087), so a month this walk found open cannot be issued before this transaction commits.
-- Statements are never deleted (0092), so a month the probe found issued cannot reopen either.
--
-- One row pays one shared lock and one probe per issued month it walks past, and a batch pays that per row: the walk
-- is bounded by the calendar, not by a step count, so a long issued chain is slow by design.
--
-- The walk stops at the calendar's last period: `9999-12` is the last `YYYY-MM` a row can name, and a row with no
-- open month after it is refused under a named constraint rather than written into a billed month. The trigger runs
-- before the insert's `ON CONFLICT (org_id, billing_key) DO NOTHING`, so a replay of a key that is already on file
-- answers `Duplicates` only while the walk can name a month; a replay that also sits at the end of an exhausted chain
-- gets that same named refusal (nothing is written either way).
--
-- A writer never supplies `original_period` itself, whether the month it names is issued or not: the marker is the
-- guard's decision, and the guard is the only side holding the candidate month's lock.
--
-- 0092's UPDATE/DELETE guard is untouched: a re-dated row is as immutable as any other ledger row.
--
-- Rollback: drop the two check constraints and the column, then re-create the 0087 body of
-- `evidgate.usage_ledger_entry_period_issued_guard()`. Deploy: any order, one transaction. `lock_timeout` bounds only
-- the wait for each lock. The runner applies one file in one transaction (`WithTransactionPerScript`), so every lock
-- this file takes is held until it commits: the column add's ACCESS EXCLUSIVE lock stays for the whole file, and both
-- validation scans therefore run under it and block reads and writes like the add does. `not valid` is kept for the
-- two things it does buy: a data failure surfaces in the separate, named `validate` instead of inside an `add`, and an
-- operator applying the same statements to a large live table outside the runner can add and commit, then run
-- `validate constraint` alone under SHARE UPDATE EXCLUSIVE, which blocks neither reads nor writes. Honest cost of the
-- one-shot form: one scan of the ledger per check while the table is locked, and every row's `original_period` is
-- null at that point, so each scan reads the table once and rejects nothing.

set local lock_timeout = '5s';

alter table public.usage_ledger_entry
  add column if not exists original_period text;

-- The same shape the period column enforces, so a period read from `original_period` parses wherever `period` does.
-- Added `not valid` and validated in the next statement (the 0031/0044 way): the add writes the catalogue only, so a
-- data failure surfaces in the named `validate` instead of inside the add, and the two steps are what an operator
-- re-runs on a large table outside the runner (see the deploy note in the header).
alter table public.usage_ledger_entry
  drop constraint if exists usage_ledger_entry_original_period_check;
alter table public.usage_ledger_entry
  add constraint usage_ledger_entry_original_period_check
  check (original_period is null or original_period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$') not valid;
alter table public.usage_ledger_entry
  validate constraint usage_ledger_entry_original_period_check;

-- A re-date only ever moves forward, and the two columns compare as text exactly as their `YYYY-MM` form orders.
-- Same two steps for the same reason as the check above: the add is catalogue-only and the scan is a named statement.
alter table public.usage_ledger_entry
  drop constraint if exists usage_ledger_entry_original_period_order_check;
alter table public.usage_ledger_entry
  add constraint usage_ledger_entry_original_period_order_check
  check (original_period is null or original_period < period) not valid;
alter table public.usage_ledger_entry
  validate constraint usage_ledger_entry_original_period_order_check;

create or replace function evidgate.usage_ledger_entry_period_issued_guard() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
  as $$
  declare
    candidate text := new.period;
    candidate_year int;
    candidate_month int;
  begin
    if pg_catalog.current_setting('transaction_isolation') <> 'read committed' then
      raise exception 'ledger rows are written under READ COMMITTED only, not %',
        pg_catalog.current_setting('transaction_isolation')
        using errcode = '25000';
    end if;

    -- The move is the guard's decision, and only it holds the lock of the month the row ends up in: a writer that
    -- already named a provenance would be stating something it cannot observe, so it is refused for every month, and
    -- the message never repeats the value it supplied.
    if new.original_period is not null then
      raise exception 'a ledger row is re-dated by its insert, so it cannot name an original period itself'
        using errcode = '23514', constraint = 'usage_ledger_entry_original_period_supplied';
    end if;

    perform pg_catalog.pg_advisory_xact_lock_shared(
      pg_catalog.hashtextextended(new.org_id::text || ':' || new.period, 0));

    if not exists (
      select 1 from public.billing_statement s
      where s.org_id = new.org_id and s.period = new.period
    ) then
      return new;
    end if;

    new.original_period := new.period;

    loop
      if candidate >= '9999-12' then
        raise exception 'usage period % has no open month after it', new.original_period
          using errcode = '23514', constraint = 'usage_ledger_entry_no_open_period';
      end if;

      -- The month is incremented as text, never through a date: `to_date('0000-01', 'YYYY-MM-DD')` is 1 BC, so a
      -- round trip through `date` would rewrite year 0000 as 0001.
      candidate_year := pg_catalog.substr(candidate, 1, 4)::int;
      candidate_month := pg_catalog.substr(candidate, 6, 2)::int;
      if candidate_month = 12 then
        candidate_year := candidate_year + 1;
        candidate_month := 1;
      else
        candidate_month := candidate_month + 1;
      end if;
      candidate := pg_catalog.lpad(candidate_year::text, 4, '0') || '-'
        || pg_catalog.lpad(candidate_month::text, 2, '0');

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
