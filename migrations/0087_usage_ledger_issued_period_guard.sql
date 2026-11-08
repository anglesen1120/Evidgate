-- An issued period is final: corrections, including adjustment rows, belong to a later open period.
-- Future AdjustUsageEntry must therefore put the adjustment in an open period, not its parent's period.
-- Existing ledger rows in issued periods stay untouched and still show drifted_since_issue; no backfill.
--
-- Each ledger insert takes a shared transaction advisory lock, then one unique-index probe on
-- billing_statement(org_id, period, kind); statement inserts take the matching exclusive lock.
-- Both triggers hash the same `org_id:period` text into the one-key lock space, because the two-key
-- int form is a separate space and could never pair with it. A batch spanning several periods, against
-- an issuer writing several periods in one transaction, can deadlock; PostgreSQL aborts one and the
-- caller retries, so no wrong row is ever committed. Trigger installation takes table DDL locks,
-- bounded by lock_timeout; only future inserts are guarded.
--
-- The statement probe needs a fresh snapshot after the lock, which only READ COMMITTED gives: a
-- REPEATABLE READ or SERIALIZABLE writer whose snapshot predates an issue would not see the statement
-- and could still insert. The ledger trigger therefore refuses any other isolation level outright.
--
-- SECURITY INVOKER is sufficient: both tables' app policies match the inserted tenant, and the
-- owner's permissive policies expose its statement rows even under FORCE RLS. Other application
-- roles cannot insert these rows. Trigger functions need no direct EXECUTE grant.

set local lock_timeout = '5s';

create or replace function evidgate.usage_ledger_entry_period_issued_guard() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
  as $$
  begin
    if pg_catalog.current_setting('transaction_isolation') <> 'read committed' then
      raise exception 'ledger rows are written under READ COMMITTED only, not %',
        pg_catalog.current_setting('transaction_isolation')
        using errcode = '25000';
    end if;

    perform pg_catalog.pg_advisory_xact_lock_shared(
      pg_catalog.hashtextextended(new.org_id::text || ':' || new.period, 0));

    if exists (
      select 1 from public.billing_statement s
      where s.org_id = new.org_id and s.period = new.period
    ) then
      raise exception 'usage period % is already issued', new.period
        using errcode = '23514', constraint = 'usage_ledger_entry_period_issued';
    end if;

    return new;
  end;
  $$;

revoke all on function evidgate.usage_ledger_entry_period_issued_guard()
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;

drop trigger if exists usage_ledger_entry_period_issued on public.usage_ledger_entry;
create trigger usage_ledger_entry_period_issued
  before insert on public.usage_ledger_entry
  for each row execute function evidgate.usage_ledger_entry_period_issued_guard();

create or replace function evidgate.billing_statement_period_lock() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
  as $$
  begin
    perform pg_catalog.pg_advisory_xact_lock(
      pg_catalog.hashtextextended(new.org_id::text || ':' || new.period, 0));
    return new;
  end;
  $$;

revoke all on function evidgate.billing_statement_period_lock()
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;

drop trigger if exists billing_statement_period_lock on public.billing_statement;
create trigger billing_statement_period_lock
  before insert on public.billing_statement
  for each row execute function evidgate.billing_statement_period_lock();
