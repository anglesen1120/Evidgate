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
-- Part 2 (GitHub App removal) also retires the unused CI-binding permission and all its role grants.

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

-- Part 2: GitHub App removal
-- Retires the GitHub App store (second part of this file). The App installation binding, its one-time install-state
-- nonces, the pre-tenant installation resolver, the nonce purger, the repository bindings the App verified, the
-- inbound webhook delivery ledger and the catalog sync queue are removed. Federated GitHub login rows (`user`,
-- `user_identity`) and the outbound Policy webhooks (`webhook_endpoint`, `webhook_outbound_delivery`) are not touched.
--
-- Objects: tables `catalog_sync`, `git_repo_binding`, `webhook_delivery`, `github_install_state`,
-- `github_installation`; their indexes and guard triggers/functions; `evidgate_resolve.installation_by_id(bigint)`;
-- `evidgate.purge_expired_github_install_state(integer)`. `platform_sweep_audit` is append-only history and keeps the
-- earlier `github_installation.resolve` / `github_install_state.purge` rows.
-- `ci_binding:write` and its built-in and tenant role grants are removed; `ingest_token:write` and historical
-- `audit_log` action text are unchanged. The grant-to-permission foreign key requires deleting grants first.
--
-- Order: definers and grants first, then `catalog_sync` before `git_repo_binding` (the sync table holds the foreign
-- key), then the other tables, then the guard functions their triggers used. No CASCADE.
--
-- LOCKS: ACCESS EXCLUSIVE on the five tables until commit; they are written only by the removed install, binding and
-- sync code, so stop the API and Worker of the previous release before applying. Once committed the data is gone: the
-- way back is a restore, not a re-run.

do $$
begin
  if to_regprocedure('evidgate_resolve.installation_by_id(bigint)') is not null then
    revoke all on function evidgate_resolve.installation_by_id(bigint) from evidgate_app;
  end if;
  if to_regprocedure('evidgate.purge_expired_github_install_state(integer)') is not null then
    revoke all on function evidgate.purge_expired_github_install_state(integer) from evidgate_worker_sched;
  end if;
  if to_regclass('public.catalog_sync') is not null then
    revoke all on public.catalog_sync from evidgate_app, evidgate_operator;
  end if;
  if to_regclass('public.git_repo_binding') is not null then
    revoke all on public.git_repo_binding from evidgate_app, evidgate_operator;
  end if;
  if to_regclass('public.webhook_delivery') is not null then
    revoke all on public.webhook_delivery from evidgate_app, evidgate_operator;
  end if;
  if to_regclass('public.github_install_state') is not null then
    revoke all on public.github_install_state from evidgate_app, evidgate_operator;
  end if;
  if to_regclass('public.github_installation') is not null then
    revoke all on public.github_installation from evidgate_app, evidgate_operator;
  end if;
end
$$;

drop function if exists evidgate_resolve.installation_by_id(bigint);
drop function if exists evidgate.purge_expired_github_install_state(integer);

drop index if exists public.catalog_sync_list;
drop index if exists public.catalog_sync_binding;
drop index if exists public.github_install_state_expires_at_idx;
drop index if exists public.github_install_state_user_idx;
drop index if exists public.github_installation_installed_by_idx;
drop index if exists public.github_installation_active_org_key;

drop table if exists public.catalog_sync;
drop table if exists public.git_repo_binding;
drop table if exists public.webhook_delivery;
drop table if exists public.github_install_state;
drop table if exists public.github_installation;

drop function if exists public.catalog_sync_guard();
drop function if exists public.git_repo_binding_guard();
drop function if exists public.webhook_delivery_guard();
drop function if exists public.github_install_state_guard();
drop function if exists public.github_installation_guard();

-- Grants first (foreign key `role_permission_permission_fk`), then the catalogue row. A re-run finds nothing to delete.
delete from public.role_permission where permission_code = 'ci_binding:write';
delete from public.permission where code = 'ci_binding:write';
