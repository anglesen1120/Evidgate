-- An issued period is final for UPDATE and DELETE as well as INSERT (0087), for every role, the owner included.
-- Plain row triggers rather than grants or policies: the owner's repair path and any future role with DML on
-- these tables meet the same refusal, and a grant change cannot lift it.
--
-- billing_statement: a row exists only once its period is issued and carries no draft state, so every UPDATE
-- and DELETE is refused. The issue flow only inserts (`insert ... on conflict do nothing`), and a re-issue
-- reads the stored row back, so no code path mutates a statement.
--
-- usage_ledger_entry: UPDATE and DELETE are refused when the row's (org_id, period) has a statement, and an
-- UPDATE is also refused when it moves the row INTO an issued (org_id, period); a move of an issued row out
-- is already refused by the first test. Ledger rows of periods without a statement stay mutable.
--
-- The ledger trigger takes the same shared advisory lock as the 0087 insert guard (old key first, then the
-- new one) so it serialises with a concurrent issue, and it refuses any isolation level but READ COMMITTED
-- for the same snapshot reason. SECURITY INVOKER as in 0087: the app role sees its own org's statements
-- through its tenant policy and the owner sees all of them through its permissive policy; no other runtime
-- role holds UPDATE or DELETE on the ledger. Existing rows in issued periods are not touched or backfilled.
--
-- Rollback: drop both triggers, then both functions. Deploy: any order, one transaction.

set local lock_timeout = '5s';

create or replace function evidgate.usage_ledger_entry_issued_mutation_guard() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
  as $$
  begin
    if pg_catalog.current_setting('transaction_isolation') <> 'read committed' then
      raise exception 'ledger rows are changed under READ COMMITTED only, not %',
        pg_catalog.current_setting('transaction_isolation')
        using errcode = '25000';
    end if;

    perform pg_catalog.pg_advisory_xact_lock_shared(
      pg_catalog.hashtextextended(old.org_id::text || ':' || old.period, 0));

    if exists (
      select 1 from public.billing_statement s
      where s.org_id = old.org_id and s.period = old.period
    ) then
      raise exception 'usage period % is already issued', old.period
        using errcode = '23514', constraint = 'usage_ledger_entry_issued_mutation';
    end if;

    if tg_op = 'UPDATE' and (new.org_id, new.period) is distinct from (old.org_id, old.period) then
      perform pg_catalog.pg_advisory_xact_lock_shared(
        pg_catalog.hashtextextended(new.org_id::text || ':' || new.period, 0));

      if exists (
        select 1 from public.billing_statement s
        where s.org_id = new.org_id and s.period = new.period
      ) then
        raise exception 'usage period % is already issued', new.period
          using errcode = '23514', constraint = 'usage_ledger_entry_issued_mutation';
      end if;
    end if;

    if tg_op = 'DELETE' then
      return old;
    end if;
    return new;
  end;
  $$;

revoke all on function evidgate.usage_ledger_entry_issued_mutation_guard()
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;

drop trigger if exists usage_ledger_entry_issued_mutation on public.usage_ledger_entry;
create trigger usage_ledger_entry_issued_mutation
  before update or delete on public.usage_ledger_entry
  for each row execute function evidgate.usage_ledger_entry_issued_mutation_guard();

create or replace function evidgate.billing_statement_issued_mutation_guard() returns trigger
  language plpgsql
  security invoker
  set search_path = ''
  as $$
  begin
    raise exception 'billing statement of period % is issued and cannot be changed', old.period
      using errcode = '23514', constraint = 'billing_statement_issued_mutation';
  end;
  $$;

revoke all on function evidgate.billing_statement_issued_mutation_guard()
  from public, evidgate_app, evidgate_operator, evidgate_worker_sched;

drop trigger if exists billing_statement_issued_mutation on public.billing_statement;
create trigger billing_statement_issued_mutation
  before update or delete on public.billing_statement
  for each row execute function evidgate.billing_statement_issued_mutation_guard();
