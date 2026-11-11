-- A billing key identifies one immutable billable payload; a different payload must not be accepted as a replay.
-- NULL marks a legacy row: the application reconstructs its hash from stored billable fields, without backfilling.
-- Deploy this expansion before the application that writes payload_hash. Older writers remain compatible.
-- Rollback: deploy the older application first, then drop the check and column; no ledger payload is transformed.
-- Adding the validated check scans the ledger under the column add's ACCESS EXCLUSIVE lock in this transaction.
set local lock_timeout = '5s';

alter table public.usage_ledger_entry
  add column if not exists payload_hash text;

do $$
begin
  if not exists (
      select 1 from pg_catalog.pg_constraint
       where conrelid = 'public.usage_ledger_entry'::regclass
         and conname = 'usage_ledger_entry_payload_hash_check') then
    alter table public.usage_ledger_entry
      add constraint usage_ledger_entry_payload_hash_check
      check (payload_hash is null or payload_hash ~ '^sha256:[0-9a-f]{64}$');
  end if;
end
$$;
