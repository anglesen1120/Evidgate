-- 0060_identity_ingest_token_decision_read.sql
--
-- WHY: the CI gate command (`evidgate gate`, 07.6.1) polls `GET /v1/runs/{r}/decision` with the credential it
-- already holds — the short-lived `ci_producer` token it got from `POST /v1/ci/token` — so `ci_producer` has to
-- hold `decision:read`. Without it the gate's first poll is a 403 before any run is read, and the alternative
-- (a second member credential in CI) contradicts 07.6.1's single-credential caller.
--
-- The permission itself is 0010's row: this file grants it and nothing else. The grant is on the built-in role
-- only (`role.org_id is null`), the scope is the project the token was minted for, and
-- `PermissionEvaluator.ServiceRoleCeiling` plus the minting commands carry the same list in code — the ceiling
-- test and the minted-set tests pin that trio together (`ServiceRoleCeiling_MatchesTheCatalogue`,
-- `CiProducerScopeTests`).
--
-- WHAT: one grant row and one constraint change.
--
--   * `role_permission` gains (`ci_producer`, `decision:read`); `SeedMatrixTests.RolesMatchOverview` diffs the
--     grants against `contracts/permissions.json`.
--   * `ingest_token_scopes_check` widens from three codes to four. An ingest token's menu is the `ci_producer`
--     set (02.4.2 §4): a token that could not carry `decision:read` would mint a producer token without it, so
--     the constraint and `IngestTokenScopes.All` move together. Existing rows already satisfy the wider check,
--     which is why the constraint can be replaced without a backfill — a token created before this migration
--     keeps the scopes it was minted with, and only new tokens may ask for the fourth.
--
-- REFERENCES: 00 §2 (the ci_producer row names run:create, event:write, artifact:write and the read of its own
-- project's decision), 07.6.1 §2 (the gate's caller), 07.5.2 §1 (`GET /v1/runs/{r}/decision`).
--
-- ORDER: additive for every existing reader — no handler loses a permission and no stored token gains one — so
-- migrate before deploying the CLI that polls the decision. No down migration: revoking the grant would 403
-- every gate command mid-release.

set local lock_timeout = '5s';

do $seed$
declare
  grants jsonb := '[
    {"role_code": "ci_producer", "permission_code": "decision:read"}
  ]'::jsonb;
begin
  insert into public.role_permission (id, org_id, role_id, permission_code)
  select uuidv7(), null, r.id, granted.permission_code
    from jsonb_to_recordset(grants) as granted(role_code text, permission_code text)
    join public.role r on r.org_id is null and r.code = granted.role_code
  on conflict (role_id, permission_code) do nothing;
end
$seed$;

-- The token's scope menu is the ci_producer set, so a fourth code joins the closed check. Only the menu and the
-- cardinality widen here: a token minted before this migration already satisfies the new check, so the
-- constraint is replaced without a backfill.
alter table public.ingest_token drop constraint if exists ingest_token_scopes_check;
alter table public.ingest_token
  add constraint ingest_token_scopes_check check (
    cardinality(scopes) between 1 and 4
    and scopes <@ array['event:write', 'artifact:write', 'run:create', 'decision:read']::text[]);
