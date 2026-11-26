-- 0137_github_app_removal.sql
--
-- Part 1 (artifact owner): `public.artifact.owner_credential_id` records the stable ingest-token id that minted a
-- pending upload (`IRequestPrincipal.CredentialId`, the id that survives token exchanges, never a jti). The
-- completion route and the writer command then refuse any other credential, so a token of the same project can no
-- longer complete or reject another producer's artifact once the CI-run-ref guard is gone. The refusal answers the
-- same 404 as an unknown id.
--
-- No foreign key: the value is an opaque credential identity that is not one table's key (`producer_slot` keeps
-- its `bound_token_jti` the same way). It is bounded 1..256 like that column. Rows minted before this migration
-- keep a NULL owner and the earlier rule (any caller authorised for the project): nothing expires a
-- `pending_upload` row (`artifact_expiry_idx` covers `ready` rows only), so refusing NULL owners could strand an
-- upload that is still legitimate. Only rows pending at deploy time are in that state, because every new mint
-- stamps its owner.
--
-- Grants and triggers: INSERT is table-level for `evidgate_app` and the column is deliberately absent from its
-- column-level UPDATE grant (0028), so the owner is written once and no transition can change it.
-- `artifact_guard_transition_trg` (0095) reads `state` only; RLS policies are unchanged.
--
-- Locks: `set local lock_timeout` bounds only the wait for ACCESS EXCLUSIVE on `public.artifact`. The nullable
-- ADD COLUMN is metadata-only; the CHECK on the new, all-null column may scan the table once while the lock is
-- held, and the migrator applies the script in one transaction, so the lock lasts until commit.
--
-- Part 2 (GitHub App removal) is appended below.

set local lock_timeout = '5s';

alter table public.artifact
  add column if not exists owner_credential_id text;

do $$
begin
  if not exists (select 1 from pg_constraint where conname = 'artifact_owner_credential_id_check' and conrelid = 'public.artifact'::regclass) then
    alter table public.artifact
      add constraint artifact_owner_credential_id_check
      check (owner_credential_id is null or char_length(owner_credential_id) between 1 and 256);
  end if;
end
$$;
