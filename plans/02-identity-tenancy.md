# 02 - Identity and Tenancy (G1–G2, G4)

> Contracts: `00-overview.md` §2 (roles), §5.4 (tenancy and data access), §8 (states), §9 (API conventions), §11 (NFR). Facts: `../DECISIONS.md` §4 (stack), §6 F3/F5/F6, §9 R4/R5. Source: design §3 (technical requirements), §4 (Test creation and management), §6 (Data model, storage constraints), §8 (Security and permission boundaries).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build. Field lists below are starting points for that step.

> **Phases (CR-D-2):** the Milestone column holds the CR-D-2 phase (G1–G4) from [13 §4.4](13-product-phases.md); M0–M5 in this plan's prose refers to the pre-CR-D-2 sequence. Phase totals (raw): G1 27 · G2 12.5 · G4 6.5 person-days.

## 1. Purpose and scope

**Goal.** Every business row is tenant-scoped, and every actor that touches the platform — human, CI job, agent, Evidgate operator — authenticates into a least-privilege identity whose actions are auditable. This plan owns orgs, projects, membership, the role/permission catalogue, all authentication paths, database-enforced tenant isolation and the audit core.

**In scope.** `organization` / `project` / `membership`; roles → permissions as data (00 §2); human login by authorization-code + PKCE — GitHub OAuth2 (no ID token; T6) and Google Workspace OIDC — with server-side sessions; CI authentication (GitHub Actions OIDC JWT → `POST /v1/ci/token` → short-lived `ci_producer` token) and hashed project ingest tokens for other CIs; agent service identity (V2); `FORCE ROW LEVEL SECURITY` + `ITenantScope` tenant transactions (00 §5.4, §5.5 C3); `audit_log` core; support access for `platform_operator`; the cross-tenant test suite that gates M1 (00 §4).

**Out of scope.** Passwords and password reset (V1 never stores a password credential); SAML/enterprise SSO (V3); SCIM and directory sync; quotas, plans and metering arithmetic (12) — this plan only stores the limit columns and enforcement hooks; policy/waiver permissions semantics (07); healing permissions semantics (08); artifact and secret-value access rules (06, 11); UI screens (09 builds them to these contracts).

**Version split.** V1: 02.1–02.4, 02.6–02.9 (M1-M2). V2: 02.5 agent service identity (M4). V3: 02.3.3 SAML (M5).
## 2. Requirements

- **FR-02-1** `organization`, `project` and `membership` exist with `unique (org_id, id)` on every tenant table so business FKs are composite `(org_id, <id>)` (00 §5.4.1, T3). No business row exists without `org_id`; the only exceptions are the single platform list maintained in §4.5 (T3): `user`, `user_identity`, `session`, `permission`, built-in `role`/`role_permission` rows, built-in `template` (owned by 03) and `organization` itself (T4); catalogue rows are readable by every org and written only by the owner/migration role.
- **FR-02-2** Roles are data: `role`, `permission`, `role_permission`, `membership_role`. Permissions are strings `resource:action`; a member may hold several roles; every check is evaluated **per project** and denies by default (unknown permission or missing role → 403).
- **FR-02-3** Human login is authorization-code + PKCE: Google Workspace via OIDC (ID token verified against the issuer JWKS); GitHub via OAuth2 — it returns no ID token, so the subject is the numeric user id from `GET /user` and the email comes from `GET /user/emails` (verified entries only) (T6). No password is stored, hashed or accepted anywhere in V1. An optional per-org email-domain allowlist restricts Google Workspace logins.
- **FR-02-4** A user identity is bound to `(provider, subject)`. Email is never an identity key; an invite binds to a membership only after the provider reports the email verified and it equals the invited address.
- **FR-02-5** Sessions are server-side rows with `expires_at` (idle) and `absolute_expires_at`; the cookie is an opaque `__Host-`-prefixed `HttpOnly; Secure; SameSite=Lax` value, rotated on login and on privilege change. Cookie-authenticated mutations require the ASP.NET Core antiforgery pair: the `XSRF-TOKEN` cookie sent back as the `X-XSRF-TOKEN` header (T38). Revocation is immediate (row revoked → next request 401).
- **FR-02-6** `POST /v1/ci/token` accepts a GitHub Actions OIDC JWT and returns a short-lived `ci_producer` token scoped to exactly one project. Validation: signature via issuer JWKS, `iss`, expected `aud`, `exp`/`nbf` with ≤ 60 s skew, `jti` single-use, and binding match on the immutable `repository_id`, allowed `ref` patterns and allowed `job_workflow_ref` values. Both `sub` formats are accepted: the legacy `repo:<owner>/<repo>:…` form and the immutable form used by repositories created after 2026-07-15 ✅ ([GitHub docs: OpenID Connect](https://docs.github.com/en/actions/concepts/security/openid-connect), read 2026-09-24).
- **FR-02-7** The minted `ci_producer` token carries the CI context claims it was minted from (`repository_id`, `repository`, `ref`, `sha`, `run_id`, `run_attempt`, `job_workflow_ref`), a project scope, the permission set `{run:create, event:write, artifact:write}`, an expiry, and a `jti`; it cannot approve, read other projects, or change policy.
- **FR-02-8** Non-GitHub CI uses a project ingest token: a ≥ 128-bit random secret with a public `prefix`, shown once at creation and stored only as `HMAC-SHA256(pepper_vN, secret)` + `pepper_version` — lookup by prefix, then constant-time compare (`CryptographicOperations.FixedTimeEquals`) (T5); with rotation (overlap window), immediate revoke, `last_used_at` and per-token scope. The pepper lives in the secret store; rotation adds a new pepper version for new tokens while old versions verify until their last token expires (runbook 10.7.1).
- **FR-02-9** V2: an agent has a service identity per `(tenant, pool)`; it can claim/execute jobs only in its pool and cannot approve healing, change policy or read other projects (00 §2).
- **FR-02-10** Tenant isolation is enforced in the database: `FORCE ROW LEVEL SECURITY` on every business table (DECISIONS F5 ✅), application role neither owner nor `BYPASSRLS`, every request/task in one explicit transaction opened by `ITenantScope.BeginAsync` that sets `evidgate.org_id` (and `evidgate.project_id` where relevant) through parameterised `set_config(…, true)` (00 §5.4.2, §5.5 C3), migrations run as a separate owner role (00 §5.4.3).
- **FR-02-11** An unset or empty tenant setting yields **zero rows**, never all rows (fail-closed): RLS policies use `evidgate.current_org_id()` / `evidgate.current_project_id()`, which read `current_setting(…, true)` + `NULLIF(…, '')::uuid` and return NULL when unset or empty (T2). Only the tables on the §4.5 exception list are outside the org policy (D12, T3, T4).
- **FR-02-12** The audit core appends rows `(actor_type, actor_id, action, object_type, object_id, before_hash, after_hash, request_id, trace_id, created_at)` in the same transaction as the business change, chained per org with `prev_hash`/`entry_hash` plus a per-org `seq bigint` (`unique (org_id, seq)`); concurrent writers in one org are serialised by `pg_advisory_xact_lock(hashtext('audit:' ‖ org_id))` taken as the **last** statement before commit, and chain verification walks `seq` (T8); the application role cannot `UPDATE` or `DELETE` them.
- **FR-02-13** `platform_operator` has no standing tenant access. Support access needs a `support_grant` (scopes + reason + expiry) approved by the tenant; the operator path uses a separate database role, is audited, and expires automatically.

**Acceptance criteria (testable).** Given a token whose scope is project A, when it calls `/v1/projects/B/…`, then 403 with `project_scope_mismatch`; given any record of another org, when read by id, then 404. Given a workflow job from an unbound repository, a fork PR whose `job_workflow_ref` is not allowlisted, or a replayed `jti`, then 401/403 and no token is minted. Given an ingest token after `revoke`, then 401 on the next exchange. Given no tenant setting (`set_config` never called), then every RLS-protected table returns 0 rows (§4.5 exception list excepted) and the suite fails loudly. Given two sessions for one user with one revoked, then only the revoked one gets 401.
## 3. Key decisions

| # | Decision | Rationale / rejected alternative |
|---|---|---|
| D1 | Authorization-code login only (OIDC for Google Workspace, OAuth2 + PKCE for GitHub — T6); no password credential in V1 | Smallest secure surface for a B2B pilot (DECISIONS R5); rejects email+password (credential storage, reset flows, breach surface) and magic-link-only (no enterprise story) |
| D2 | Roles and permissions are **data rows**, seeded from one `permissions.json` in `contracts/` | Tenant-specific roles later without a deploy; the same file generates constants for the server (C#), the web app and the CLI, so drift is caught by a test |
| D3 | Identity key is `(provider, subject)`; email is only a claim to verify at invite binding | Email changes and address reuse across providers are not identity; rejects email-as-key auto-merge |
| D4 | CI trust is bound to the immutable `repository_id`, not the repository name | Renames and name reuse cannot silently inherit a binding (F3 ✅ supports both claim forms) |
| D5 | Both OIDC `sub` formats accepted, and no policy keys on `sub` alone | Repos exist on both sides of the 2026-07-15 change ✅; binding uses `repository_id` + `ref` + `job_workflow_ref` |
| D6 | Ingest tokens are ≥ 128-bit random secrets stored as `HMAC-SHA256(pepper_vN, secret)` + `pepper_version`; lookup by `prefix`, constant-time compare (T5) | Re-decided at the stack change (CR-D-1): scrypt was picked for the Node built-in — .NET has no in-box scrypt, and a slow KDF adds no security for random secrets; the pepper lives in the secret store and rotation = a new version for new tokens, old versions verify until their last token expires (runbook 10.7.1) |
| D7 | `ci_producer` is our own short-lived JWT (default 60 min, bounded by job) minted and validated with `Microsoft.IdentityModel.JsonWebTokens`, rather than a session row | Hot path (every batch) without a DB round-trip; replay safety comes from `jti` + short TTL; rejects long-lived CI secrets (R4) |
| D8 | Isolation is enforced by RLS **and** composite FKs, with application checks as the third layer | DECISIONS F5 ✅; an application-only check is one forgotten `where` away from a leak |
| D9 | Sessions are server-side rows, not stateless tokens | Immediate revocation on membership removal or suspected theft; logout-all is an update, not a key rotation |
| D10 | Audit rows are written in the business transaction | A log-tail/CDC design loses actor and `request_id`; rejects "audit later, from logs" |
| D11 | Operator access is grant-based and time-boxed; no standing access | Source §8: support access needs scope and expiry; makes PDPL "who saw personal data" answerable |
| D12 | `user`, `user_identity`, `session` are platform-scoped, not tenant-scoped | They hold no customer business data (see §13 CR-1) |
## 4. Design

### 4.1 Tables owned (columns at high level)

| Table | Key columns | Constraints / notes |
|---|---|---|
| `organization` | `id`, `name`, `slug`, `status`, `retention_default_days`, `allowed_email_domains text[]` | `unique (slug)`; on the §4.5 exception list — no `org_id` column (T4); RLS policy `id = evidgate.current_org_id()`; "which orgs am I in" resolves via `evidgate_resolve.member_orgs(user_id)` (T4) |
| `project` | `id`, `org_id`, `name`, `slug`, `status`, `default_environment_id` | `unique (org_id, slug)`, `unique (org_id, id)`; composite FK `(org_id, default_environment_id)` → 05 `environment`, created by `05.1.2-T1`, which also adds `unique (org_id, id)` on `environment` as the FK target |
| `user` | `id`, `email`, `display_name`, `status`, `last_login_at` | platform scope; `lower(email)` gets a plain (non-unique) index only — email is never an identity key (D3, T6) |
| `user_identity` | `id`, `user_id`, `provider`, `subject`, `email_at_provider`, `linked_at` | `unique (provider, subject)`; provider ∈ {github, google} |
| `session` | `id`, `user_id`, `created_at`, `last_seen_at`, `expires_at`, `absolute_expires_at`, `ip`, `user_agent`, `revoked_at`, `rotated_from` | revocation is a row update (D9); presented via the `__Host-` cookie (T38) |
| `membership` | `id`, `org_id`, `user_id`, `invited_email`, `status`, `invited_by` | `unique (org_id, id)` (T3), `unique (org_id, user_id)`, `unique (org_id, invited_email)` where pending |
| `membership_role` | `id`, `org_id`, `membership_id`, `role_id`, `granted_by`, `granted_at` | `unique (org_id, id)` (T3), `unique (membership_id, role_id)`; composite FKs to `membership` and `role` |
| `role` | `id`, `org_id` (null = built-in), `code`, `name`, `is_builtin` | `unique (org_id, id)` (T3); built-ins mirror 00 §2 role codes; platform catalogue rows: readable by every org, writable only by the owner/migration role; RLS `USING (org_id IS NULL OR org_id = evidgate.current_org_id())` |
| `permission` | `code`, `resource`, `action`, `description`, `sensitive` | seeded from `contracts/permissions.json`; platform catalogue row (no `org_id`): readable by every org, writable only by the owner/migration role |
| `role_permission` | `id`, `org_id` (null = built-in), `role_id`, `permission_code` | `unique (org_id, id)` (T3), `unique (role_id, permission_code)`; built-in rows are platform catalogue rows like their parents (readable by every org, writable only by the owner/migration role) |
| `ci_binding` | `id`, `org_id`, `project_id`, `provider`, `repository_id`, `repository_full_name`, `allowed_ref_patterns text[]`, `allowed_workflow_refs text[]`, `active` | `unique (org_id, id)` (T3), `unique (org_id, project_id, provider, repository_id)`; name changes audited, not trusted |
| `ci_token_issue` | `id`, `jti`, `org_id`, `project_id`, `repository_id`, `ref`, `sha`, `run_id`, `run_attempt`, `job_workflow_ref`, `expires_at` | `unique (org_id, id)` (T3), `unique (jti)` — replay rejection (FR-02-6); a `jti` row is kept for the OIDC token lifetime + 10 min skew, then purged by a daily task (Cronos schedule, unique fire key, 00 §5.3) (T7) |
| `ingest_token` | `id`, `org_id`, `project_id`, `name`, `prefix`, `token_hmac`, `pepper_version`, `scopes text[]`, `expires_at`, `revoked_at`, `rotated_to_id`, `last_used_at` | `unique (org_id, id)` (T3), `unique (prefix)`; `token_hmac` = `HMAC-SHA256(pepper_vN, secret)`, compare via `FixedTimeEquals` (T5); plaintext never stored or logged |
| `service_identity` (V2) | `id`, `org_id`, `kind`, `pool_id`, `name`, `public_key`, `status`, `expires_at` | `unique (org_id, id)` (T3); agent tokens are signed per identity, pool comes from the row |
| `support_grant` | `id`, `org_id`, `operator_user_id`, `scopes text[]`, `reason`, `approved_by`, `approved_at`, `expires_at`, `revoked_at` | `unique (org_id, id)` (T3); explicit approve by a tenant admin |
| `audit_log` | `id`, `org_id`, `seq`, `actor_type`, `actor_id`, `action`, `object_type`, `object_id`, `before_hash`, `after_hash`, `request_id`, `trace_id`, `metadata jsonb`, `prev_hash`, `entry_hash`, `created_at` | `unique (org_id, id)` (T3), `unique (org_id, seq)`; append-only; writers serialise per org on `pg_advisory_xact_lock(hashtext('audit:' ‖ org_id))` before commit (T8); partition by time only when 00 §12 growth data justifies it |

### 4.2 Endpoints owned / used

Owned (shapes land in `contracts/openapi.yaml`; mutations follow 00 §9.1):

| Endpoint | Permission | Notes |
|---|---|---|
| `POST /v1/ci/token` | GitHub OIDC JWT or ingest token | 00 §9.3; resolves the tenant via `evidgate_resolve` before the tenant scope opens (T1); exchanges for a `ci_producer` token; `Idempotency-Key` not required (no side effect beyond `ci_token_issue`) |
| `GET,POST /v1/orgs`, `GET,PATCH /v1/orgs/{o}` | `organization:read` / `organization:write` | org list for the current user via `evidgate_resolve.member_orgs` (T4); slug uniqueness → 409 |
| `GET,POST /v1/orgs/{o}/projects`, `GET,PATCH /v1/projects/{p}` | `project:read` / `project:create` / `project:write` | creating a project is org-level (`project:create`, 00 §9.3); `project:write` is per-project settings (T21); project in URL must match token scope |
| `GET,POST /v1/orgs/{o}/memberships`, `PATCH,DELETE …/{m}`, `POST …/{m}/roles`, `DELETE …/{m}/roles/{r}` | `membership:read` / `membership:write` | last-admin guard (FR: epic 02.1.2) |
| `GET /v1/permissions`, `GET,POST,PATCH /v1/roles` | `role:read` / `role:write` | built-in roles immutable |
| `GET,POST /v1/projects/{p}/ci-bindings`, `PATCH,DELETE /v1/ci-bindings/{b}` | `ci_binding:write` | prints the binding id used by CI setup docs |
| `POST /v1/projects/{p}/ingest-tokens`, `POST /v1/ingest-tokens/{t}/rotate`, `DELETE /v1/ingest-tokens/{t}` | `ingest_token:write` | response carries the plaintext once |
| `GET /v1/orgs/{o}/audit`, `GET /v1/orgs/{o}/audit/export` | `audit:read` | cursor pagination (00 §9.1) |
| `POST /v1/support-grants`, `POST /v1/support-grants/{g}/approve`, `…/revoke` | `support:grant` / `support:approve` | approve by `tenant_admin` of the target org, approver ≠ requester |
| `GET /v1/auth/oidc/{provider}/start`, `GET /v1/auth/oidc/{provider}/callback`, `POST /v1/auth/logout`, `GET /v1/auth/sessions`, `DELETE /v1/auth/sessions/{s}`, `POST /v1/auth/sessions/revoke-all` | session cookie | `__Host-` cookie + `X-XSRF-TOKEN` antiforgery on mutating routes (T38); the four session routes are in the 00 §9.3 core list (F47), the OIDC pair is documented here and in `contracts/`; the admin end-all verb is 02.3.2-T3 |
| `POST /v1/auth/invite-token`, `POST /v1/auth/link/{provider}`, `GET /v1/auth/link/pending`, `DELETE /v1/auth/link/pending`, `POST /v1/auth/link/confirm` | invite-token: anonymous (stages an encrypted cookie); link routes: session cookie | 02.3.1-T2: the token never travels in a URL; linking is staged then confirmed explicitly, never auto-merged; `X-XSRF-TOKEN` is required by the contract on the link mutations and enforced when 02.3.2-T1.2 lands (`contracts/README.md`) |
| `GET /v1/me`, `PATCH /v1/me` | session cookie (own user only) | current user profile (`id`, `email`, `display_name`, `preferred_locale`); PATCH accepts only `preferred_locale ∈ {vi, en}`, `If-Match` on `user.version`; 02.3.1-T3 |

Used: every other plan's endpoints inherit these guards; 04 calls `POST /v1/ci/token` from the CLI/adapter.

### 4.3 Flows

**Human login.** Browser → `start` (the ASP.NET Core auth handler creates `state`, `nonce` and the PKCE verifier in a short-lived `SameSite=Lax` cookie) → provider → `callback` → for Google verify the OIDC ID token against the issuer JWKS; for GitHub exchange the code, then read the numeric user id from `GET /user` and the verified email from `GET /user/emails` (OAuth2 — no ID token; T6) → upsert `user_identity` → resolve the caller's orgs via `evidgate_resolve.member_orgs` (T4) → membership check (else 403 `no_membership`, invite still pending) → insert `session` → set the `__Host-` cookie → redirect. Session id rotates on login and on any role change.

**CI exchange.** Workflow with `id-token: write` requests an OIDC token (`aud` = the Evidgate audience for the environment) → `POST /v1/ci/token` → validate claims with `Microsoft.IdentityModel.JsonWebTokens` (FR-02-6) → resolve `(org_id, project_id, binding_id, status)` through the `evidgate_resolve` SECURITY DEFINER function on the binding key (T1) → open the tenant scope → insert `ci_token_issue` (unique `jti`) → mint `ci_producer` → 200 `{ token, expires_at, project_id, permission_set }`. Non-GitHub CI posts its ingest token instead; the same resolver pattern maps `prefix` → tenant (T1) before the same minting path runs.

**Request transaction.** Resolve credential (session cookie, bearer token or a §5.4.8 pre-tenant resolver) → `ITenantScope.BeginAsync` opens one transaction and sets the tenant context via parameterised `set_config(…, true)` (00 §5.5 C3) → Mediator authorization behaviour → handler → append `audit_log` row + outbox row (00 §5.3) → commit. RLS silently filters anything that escaped the handler; the cross-tenant suite proves it never returns foreign rows.

**Support access.** Operator files a grant request with scopes and reason → tenant admin approves → the operator's session carries the grant id; the operator path connects with the `evidgate_operator` role, sets the tenant context only while the grant is valid, and every read/write is audited with `actor_type = platform_operator`. Expiry is enforced at request start; alerts fire on grant creation and on operator reads of sensitive objects.

### 4.4 States owned here (00 §8 has no entry for these; see §13 CR-2)

`ingest_token`: `active → rotated` (overlap window, both valid) `→ revoked`; `active → revoked`; `active → expired`. `session`: `active → revoked | expired`. `support_grant`: `requested → approved → expired | revoked` (approver ≠ requester). `ci_binding`: `active → disabled`. Every transition writes an audit row.

### 4.5 RLS pattern (00 §5.4)

- Roles: `evidgate_owner` (migrations, table owner), `evidgate_app` (runtime, `NOLOGIN` + `SET ROLE` from the API/worker connection), `evidgate_operator` (support only).
- Helpers `evidgate.current_org_id()` / `evidgate.current_project_id()` read `current_setting(…, true)` + `NULLIF(…, '')::uuid` and return NULL (never an error) when the setting is missing or empty, so a missing context yields zero rows for tenant tables (FR-02-11, T2). Every policy uses the helpers — never a raw `current_setting`.
- Each business table gets `ENABLE` + `FORCE ROW LEVEL SECURITY`, declares `unique (org_id, id)` so it can be the target of a composite FK (T3), and policies `USING (org_id = evidgate.current_org_id())` / `WITH CHECK (same)`; join tables use `EXISTS` against the parent. `organization` itself uses `id = evidgate.current_org_id()` (T4).
- **Exception list (the only tables/rows without `org_id`, T3):** `user`, `user_identity`, `session` (platform identity, D12); `permission`, built-in `role`/`role_permission` rows and built-in `template` (03) — the catalogue, readable by every org (`USING (org_id IS NULL OR org_id = evidgate.current_org_id())`), writable only by the owner/migration role; and `organization` (T4), whose members' org list is served by `evidgate_resolve.member_orgs(user_id)`.
- **Pre-tenant resolution (T1):** entry points that learn the tenant only from the credential — the login callback, `POST /v1/ci/token`, ingest-token authentication — call owner-owned `SECURITY DEFINER` functions in schema `evidgate_resolve` that accept one lookup key and return only `(org_id, project_id, binding_id, status)`; the app role has `EXECUTE` and nothing else outside RLS, and every call is audited. The normal tenant scope then opens. The framework plus the 02-owned functions (`member_orgs`, the ci-binding resolver, the ingest-token resolver) land in 02.6.1; the GitHub-webhook resolver is 03.4.1's.
- The migration template + a CI lint (02.6.1-T2) fail any new table without `org_id`, a policy and `unique (org_id, id)`, checked against the exception list above; a boot-time self-check verifies policy presence.
## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| OIDC provider down or slow | 503 with `Retry-After`; login retries; existing sessions unaffected; no password fallback | No alternate credential path appears under pressure |
| Unknown `kid` / JWKS rotation | Refresh JWKS (cached with TTL, single-flight); if the token still fails, 401 `token_invalid` | Signature is never skipped |
| GitHub OIDC JWT replayed | `unique (jti)` on `ci_token_issue` → 401 `token_replayed`; audit row | One OIDC JWT mints at most one `ci_producer` token |
| Repository renamed | Binding matches on `repository_id`; `repository_full_name` mismatch → 403 + audit warning; admin may update the display name | Trust never follows a name |
| Fork PR run requesting a token | `repository_id` is the fork, or `job_workflow_ref` not allowlisted → 403 `binding_mismatch` | Untrusted code cannot obtain ingest credentials (DECISIONS F4, plan 11) |
| `aud`/`iss` mismatch (copied token from another environment) | 401; the audience is per environment, not shared | A token from dev is useless in prod |
| Clock skew | `nbf`/`exp` tolerance ≤ 60 s; beyond that 401 | Short TTLs stay meaningful |
| Ingest token leaked | Rotate (overlap) then revoke; `last_used_at`, prefix and name identify the token | Plaintext never existed in the database or logs |
| Missing tenant GUC (deploy or code bug) | RLS returns zero rows for tenant tables → requests 404/empty, cross-tenant suite and a startup self-check fail | Fail-closed; never a foreign-org read |
| New table added without a policy | CI lint fails; runtime double-checks policy presence at boot | Isolation cannot regress silently |
| Member removed while sessions live, or an invite for an existing member | Sessions revoked + membership re-read per request (no long cache); pending approvals invalidated; duplicate invite → 409 `unique (org_id, invited_email)` | Removed access ends at once; no duplicate membership |
| Last `tenant_admin` removal | Guard rejects with 409 `last_admin` | A tenant cannot lock itself out |
| Same email at two providers | Linked as two identities on one user only after an explicit confirm in 09; never auto-merged by email | No account takeover via email reuse |
| Operator grant expires mid-session | Grant re-checked at every request start and before each write; in-flight transaction finishes and is audited with the grant id | Support access is time-boxed |
| Tenant deletion / retention (10, 11) | Deletion removes business rows; `audit_log` keeps the minimum (actor, action, object hash) per the retention policy | Deletion is complete and provable |
## 6. Security and privacy

- Threat model lives in 11; this plan implements the identity, session, token, RLS and audit surfaces it names. OWASP ASVS L2 applies to the login and token endpoints (⚠️ level confirmed in 11).
- No password material exists, so no password hashes, reset tokens or reuse checks are needed. Ingest tokens are ≥ 128-bit random secrets verified by `HMAC-SHA256(pepper_vN, secret)` + constant-time compare (T5); the pepper lives in the secret store and each version is used until its last token expires (rotation runbook 10.7.1). Tokens and cookies are never logged (only `jti` / token `id` / prefix), and the log field allowlist is asserted by a test in 11.
- CSRF: cookie-authenticated mutations require the ASP.NET Core antiforgery pair — `XSRF-TOKEN` cookie + `X-XSRF-TOKEN` header — on top of the `__Host-` `SameSite=Lax` session cookie (T38); the token endpoints are bearer-only and exempt.
- Personal data: support access is access to customer personal data under PDPL, so the grant names the scopes and the reason, is audited and is reported in the tenant's audit view (DECISIONS §7, plan 11); operator reads of artifacts still go through 06/11 policies. Account data (email, name, provider subject, IP/UA on sessions) is personal data too: retention and deletion rules are set in 11, and this plan stores the minimum, never copying provider profile data beyond email and display name.
- Session store is the credential; the risk of theft is bounded by idle/absolute timeouts, rotation, the user-visible session list, and step-up re-authentication for sensitive actions (defined in 09/11, hook reserved here).
## 7. Test strategy

- **Behaviours.** Token exchange happy path (GitHub OIDC → `ci_producer` → first accepted call); permission matrix per role from 00 §2; invite → login → membership; role change → session rotation; removal → immediate 401; grant → operator read → expiry.
- **Boundaries.** Claim validation table: wrong `iss`, wrong `aud`, expired, `nbf` in the future, skew at ±59 s/±61 s, unknown `kid`, legacy `sub`, immutable `sub` (fixtures for both), `repository_id` mismatch, `ref` not allowed, `job_workflow_ref` not allowed, replayed `jti`, second call with the same JWT.
- **Failure cases.** Provider error page, JWKS fetch failure, missing tenant setting, RLS bypass attempt, DB role without `BYPASSRLS` verified from `pg_roles`, ingest-token verify with a wrong secret or a retired `pepper_version` (T5), rotation overlap (old + new valid, old revoked after window), support grant used after expiry.
- **Isolation gate (M1 exit).** Story 02.9.1 runs two orgs with overlapping names; every API read of another org's object returns 404; every cross-org insert is rejected by a composite FK; every RLS-protected table returns 0 rows with the GUC unset and correct rows when set (platform-scoped identity tables and platform catalogue rows excepted, §4.5); `relforcerowsecurity` is read from `pg_class` for each table.
- **Fixtures.** Two orgs, 3 users with 5 role combinations, one `ci_binding` with a test JWKS (locally generated keypair served as the issuer's JWKS), token fixtures for both `sub` formats, one active and one revoked ingest token, one expired support grant, one session per user.
- **Stack.** xUnit v3 + Shouldly unit tests; Testcontainers.PostgreSQL integration tests on real PostgreSQL 18 as `evidgate_app` (never as owner, which would hide RLS bugs); the CI test suite runs with `FORCE ROW LEVEL SECURITY` asserted.
## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| GitHub changes OIDC claims or the `sub` format again | medium | high | Bind on `repository_id` + `ref` + `job_workflow_ref` (D4/D5), not `sub`; keep fixtures for both formats; contract tests fail loudly on a claim change ⚠️ changelog not watched automatically |
| OIDC-only login excludes a pilot customer using only a local identity provider | medium | medium | M0 (O1) confirms GitHub/Google; Google Workspace covers corporate accounts; SAML is the V3 answer, no interim exception |
| RLS policies hurt hot-path query plans | low | medium | Policies use the indexed `org_id`; measure in the 10 load test; composite FKs already filter by org |
| Permission catalogue drifts between code constants and seeded roles | medium | medium | One `contracts/permissions.json`; test asserts every code-referenced permission exists in the DB and vice versa |
| Token-endpoint abuse on `POST /v1/ci/token` | low | medium | Prefix/binding lookup first (no token work on an unknown prefix), rate limit per project/IP; HMAC verification is constant-time and cheap (T5) |
| Stolen session cookie | low | high | Rotation, idle + absolute timeout, session list and revoke-all, step-up for sensitive actions; residual risk accepted and recorded in 11 |
| Support-access abuse | low | high | Tenant approval, expiry, separate DB role, full audit, alert on grant creation and operator reads |
| Migration forgets a policy or a composite FK | medium | high | CI lint (02.6.1-T2) + RLS suite (02.9.1-T3) as an M1 gate |
| Operator/tenant needs a role we did not model (e.g. read-only auditor) | medium | low | Roles are data (D2); a new role is a seed row, not a deploy |
## 9. Epics, stories and tasks

Sizes: XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 (person-days). Version = V1/V2/V3; milestones from 00 §4. Each epic's total is the sum of its task rows.
### Epic 02.1 Tenant core
#### Story 02.1.1 Org and project registry — acceptance: an org and its projects are created/read by API; `unique (org_id, slug)` and `unique (org_id, id)` exist; a request for another org's object returns 404 and an audit row is written.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.1.1-T1 | `organization` / `project` schema, composite keys, migrations with the RLS template | M | V1 | G1 | 01.4.1-T3 |
| 02.1.1-T2 | Org/project CRUD commands/queries + scope resolution (org from the authenticated principal, project in URL must match) (C1/C5) | M | V1 | G1 | 02.1.1-T1, 02.2.1-T2 |
| 02.1.1-T3 | Quota/limit columns on org and project + enforcement hooks for 12 | S | V1 | G1 | 02.1.1-T1 |
**Story 02.1.1 total: 2.5** (Epic 02.1 continues in 02.1.2)
#### Story 02.1.2 Membership and invites — acceptance: an invite binds to a membership only on first login with a verified matching email; removing a member revokes their sessions immediately; removing the last `tenant_admin` is rejected.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.1.2-T1 | `membership` + `membership_role` schema and API | M | V1 | G1 | 02.1.1-T1 |
| 02.1.2-T2 | Invite flow: pending invite, verified-email bind on login, resend/cancel | M | V1 | G1 | 02.1.2-T1, 02.3.1-T2 |
| 02.1.2-T3 | Last-admin guard + session revocation on member removal/disable | S | V1 | G1 | 02.1.2-T1, 02.3.2-T2 |
**Epic 02.1 total: 5.0**
### Epic 02.2 Roles and permissions as data
#### Story 02.2.1 Permission catalogue and enforcement — acceptance: the seeded built-in roles match 00 §2 exactly; every endpoint denies by default; a permission referenced in code but missing from the catalogue fails the test suite.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.2.1-T1 | `contracts/permissions.json` + `permission` / `role` / `role_permission` seed with the 00 §2 built-in roles (incl. `billing:read` granted to `billing_operator`, cross-tenant and audited; `project:read` for all member roles, `project:write` and `project:create` for `tenant_admin` — T21, 00 §9.3) | M | V1 | G1 | 02.1.1-T1 |
| 02.2.1-T2 | Mediator authorization behaviour (C2): per-project permission evaluation, deny-by-default, service identities handled like members | M | V1 | G1 | 02.2.1-T1 |
| 02.2.1-T3 | Role × permission matrix tests incl. `viewer` deny paths and an assertion that no permission in the catalogue exposes secret values | M | V1 | G1 | 02.2.1-T2 |
**Epic 02.2 total: 3.0**
### Epic 02.3 Human login (OIDC / OAuth2)
#### Story 02.3.1 Federated login — acceptance: a GitHub (OAuth2 + PKCE) or Google Workspace (OIDC) user with a verified email and an active membership reaches an authenticated session; no password path exists in the API schema.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.3.1-T1 | Login on ASP.NET Core auth handlers: Google Workspace OIDC (ID token + JWKS verify) and GitHub OAuth2 auth-code + PKCE (no ID token; identity from `GET /user`, verified email from `GET /user/emails`); state, nonce/PKCE verifier, redirect allowlist (T6) | L | V1 | G1 | 02.1.2-T1, 01.1.2-T1 |
| 02.3.1-T2 | Identity linking on `(provider, subject)`, email-verification rule, optional org domain allowlist | M | V1 | G1 | 02.3.1-T1 |
| 02.3.1-T3 | `GET /v1/me` profile + `PATCH /v1/me {preferred_locale}` (`vi`/`en`) for the web locale preference (T37) | S | V1 | G1 | 02.3.1-T1 |
**Story 02.3.1 total: 3.5**
#### Story 02.3.2 Sessions and revocation — acceptance: the cookie is opaque, `httpOnly`, `Secure`, `SameSite=Lax`; idle and absolute expiry are enforced; a revoked session gets 401 on the next request; users can list and revoke their sessions.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.3.2-T1 | `session` table, `__Host-` cookie handling, ASP.NET Core antiforgery (`XSRF-TOKEN` cookie / `X-XSRF-TOKEN` header), rotation on login/role change (T38) | M | V1 | G1 | 02.3.1-T1 |
| 02.3.2-T2 | Logout, session list/revoke, revoke-all (self and admin-triggered) | S | V1 | G1 | 02.3.2-T1 |
| 02.3.2-T3 | Admin route to end ALL sessions of one user in an org the caller administers (`membership:write`, same org, `X-Evidgate-Org`, audited like the F48 single revoke) over the existing `ISessionRevocation.RevokeAllForUserAsync`; ASVS-GAP-14 7.4.5 remainder (the 7.5.2 session screen stays with queue row F41) | S | V1 | G1 | 02.3.2-T2 |
**Story 02.3.2 total: 2.0**
#### Story 02.3.3 Enterprise SAML — acceptance: an org can configure a SAML IdP and its users log in and land in the same session model as OIDC users.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.3.3-T1 | SAML 2.0 SSO (metadata, SP-initiated login, assertion mapping, JIT membership policy) | XL | V3 | G4 | 02.3.2-T1, 02.1.2-T2 |
**Epic 02.3 total: 8.5 · (V1 5.5 · V3 3.0)**
### Epic 02.4 CI authentication
#### Story 02.4.1 GitHub OIDC exchange — acceptance: a workflow job of a bound repository with the allowed `ref` and `job_workflow_ref` receives a `ci_producer` token scoped to one project; any claim mismatch, unbound repository, fork workflow, or replayed `jti` receives 401/403 and no token.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.4.1-T1 | GitHub OIDC JWT validator via `Microsoft.IdentityModel.JsonWebTokens`: `iss`, `aud`, JWKS caching/rotation, `exp`/`nbf` ±60 s, both `sub` formats | L | V1 | G1 | 02.1.1-T2 |
| 02.4.1-T2 | `ci_binding` model + admin API (repository_id, ref patterns, workflow refs, disable) | M | V1 | G1 | 02.1.1-T1 |
| 02.4.1-T3 | `POST /v1/ci/token` → pre-tenant resolve via `evidgate_resolve` (T1) → mint short-lived `ci_producer` with `Microsoft.IdentityModel.JsonWebTokens` (claims, scope, permission set, TTL) | M | V1 | G1 | 02.4.1-T1, 02.2.1-T2, 02.6.1-T3 |
| 02.4.1-T4 | `ci_token_issue` replay cache — `jti` kept for the OIDC token lifetime + 10 min skew, daily purge task (Cronos schedule, unique fire key, 00 §5.3) (T7) — + issuance rate limit + clock-skew handling | M | V1 | G2 | 02.4.1-T3 |
**Story 02.4.1 total: 5.0**
#### Story 02.4.2 Non-GitHub CI ingest tokens — acceptance: a token is shown once at creation, only its prefix and hash are stored, rotation keeps the old token valid only for the overlap window, and revoke makes the next exchange fail with 401.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.4.2-T1 | `ingest_token` (prefix + `HMAC-SHA256` + `pepper_version`, T5), show-once response, rotate/revoke API, `last_used_at` tracking | S | V1 | G1 | 02.4.1-T3 |
| 02.4.2-T2 | CI auth contract tests: fork PR, renamed repo, wrong audience, replayed `jti`, rotated and revoked tokens | S | V1 | G2 | 02.4.1-T4, 02.4.2-T1 |
#### Story 02.4.3 GitHub App installation — acceptance: one GitHub App is installed per organization, its installation id is stored per org, a short-lived installation token is minted on demand and never persisted, an uninstall or suspension disables the integrations that use it, and every change is audited. [PROPOSAL review 2026-10-02]
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.4.3-T1 | Decision (ADR): one Evidgate GitHub App serves checks (07.6.1), catalog webhooks (03.4.x), healing PRs (08.6.x) and re-run triggers (05.5.2), with the minimal permission set per feature and a documented owner for its private key | S | V1 | G2 | 02.4.1-T2 |
| 02.4.3-T2 | `github_installation` table (org_id, installation_id, account, permissions, state), install callback with state/nonce, installation-token minting with cache below expiry, RLS + composite FK, audit | M | V1 | G2 | 02.4.3-T1, 02.1.1-T1 |
| 02.4.3-T3 | Installation lifecycle webhooks (`installation`, `installation_repositories`, suspend/unsuspend): update state, disable dependent bindings, notify the org admin | S | V1 | G2 | 02.4.3-T2 |

**Story 02.4.3 total: 2.0**

#### Story 02.4.4 API tokens for automation — acceptance: a project admin creates an expiring, scope-limited API token that works for read/automation calls, cannot exceed its creator's permissions or hold admin permissions, and is shown once.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.4.4-T1 | `api_token` table (project scope, permission subset, mandatory expiry, HMAC + `pepper_version` like 02.4.2-T1, show-once, revoke, `last_used_at`) and create/list/revoke API | M | V1 | G2 | 02.4.2-T1, 02.2.1-T2, 02.7.1-T2 |
| 02.4.4-T2 | Bearer authentication for `evg_` tokens through the permission evaluator; refuses member/role/support/billing-admin permissions and any permission the creator lacks | M | V1 | G2 | 02.4.4-T1 |
| 02.4.4-T3 | Tests: self-escalation, expired, revoked, wrong project, rotated pepper, audit rows on create/use-denied/revoke | S | V1 | G2 | 02.4.4-T2 |

**Story 02.4.4 total: 2.5** — `[INFERENCE]` sizes; added from the missing-features review of 2026-10-02.

**Epic 02.4 total: 10.5**

### Epic 02.5 Agent service identity (V2)
#### Story 02.5.1 Agent identity per tenant and pool — acceptance: an `evidgate-agent` enrols into one pool of one tenant, can claim only that pool's jobs, cannot approve, change policy or read another project; its credential can be rotated or revoked and the revocation takes effect on the next request.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.5.1-T1 | `service_identity` enrolment (tenant + pool), keypair issue, agent token minting | M | V2 | G4 | 02.4.1-T3, 05.7.3-T1 |
| 02.5.1-T2 | Pool-scoped enforcement on `job:claim` / `job:execute` / `artifact:write`; denial of approvals and cross-project reads | S | V2 | G4 | 02.5.1-T1, 02.2.1-T2 |
| 02.5.1-T3 | Rotation/revocation + kill-switch path and runbook hooks | S | V2 | G4 | 02.5.1-T1, 10.7.1-T4 |
**Epic 02.5 total: 2.0**
### Epic 02.6 Tenant isolation (RLS)
#### Story 02.6.1 Database-enforced isolation — acceptance: every business table has `ENABLE` + `FORCE ROW LEVEL SECURITY` and an `org_id` policy (the platform-scoped identity tables are outside the org policy per D12, access goes through membership, and the platform catalogue rows use the all-org read policy of §4.5); the app role is not the owner and has no `BYPASSRLS`; a table without a policy fails CI.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.6.1-T1 | Role split (`evidgate_owner` / `evidgate_app` / `evidgate_operator`), `evidgate.current_org_id()`/`current_project_id()` helpers (T2), policy template, policies for all M1 tables | L | V1 | G1 | 02.1.1-T1 |
| 02.6.1-T2 | RLS lint in CI: new table without `org_id`, a policy or `unique (org_id, id)` fails (T3), checked against the §4.5 exception list (`user`, `user_identity`, `session`, `permission`, built-in `role`/`role_permission`, built-in `template`, `organization`, `report_parser`, and 01's `schema_migration`/`task`); boot-time policy self-check | S | V1 | G1 | 02.6.1-T1 |
| 02.6.1-T3 | `evidgate_resolve` schema + owner-owned SECURITY DEFINER functions (`member_orgs`, ci-binding resolver, ingest-token resolver) returning `(org_id, project_id, binding_id, status)`; app role `EXECUTE` only; every call audited (T1, T4) | M | V1 | G1 | 02.6.1-T1 |
**Story 02.6.1 total: 3.5**
#### Story 02.6.2 Tenant context — acceptance: every request, job and background task opens its transaction via `ITenantScope.BeginAsync` (parameterised `set_config`, C3); a missing setting yields zero rows, never an error or all rows (platform-scoped identity tables and platform catalogue rows excepted, §4.5).
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.6.2-T1 | Entry points only: API request and `task`-queue worker call `ITenantScope` from 01.4.2-T1 (shipped); adds tenant-context helpers and the W7-SEC-06 follow-up, no second scope implementation | S | V1 | G1 | 02.6.1-T1, 01.4.2-T1 |
**Epic 02.6 total: 4.0**
### Epic 02.7 Audit core
#### Story 02.7.1 Audit trail — acceptance: every permission-checked mutation appends one audit row in the same transaction with actor, action, object, before/after hash and `request_id`; the app role cannot update or delete audit rows; a broken chain is detectable by a verification job.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.7.1-T1 | `audit_log` schema, append-only grants, per-org hash chain + `seq` serialised by `pg_advisory_xact_lock` before commit (T8), indexes by `(org_id, created_at)` | M | V1 | G1 | 02.6.1-T1 |
| 02.7.1-T2 | Audit writer used by the Mediator authorization behaviour and by command handlers; `actor_type` for user / ci_producer / agent / operator / system | M | V1 | G1 | 02.7.1-T1, 02.2.1-T2 |
| 02.7.1-T3 | Audit read API + export (JSONL/CSV) + periodic chain-verification task walking `seq` (Cronos schedule, unique fire key, 00 §5.3) (T8) | S | V1 | G1 | 02.7.1-T2 |
**Epic 02.7 total: 2.5**
### Epic 02.8 Support access
#### Story 02.8.1 Time-boxed operator access — acceptance: an operator has no tenant data access until a grant is approved by a tenant admin; every operator read/write is audited with the grant id; access ends at expiry without manual action.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.8.1-T1 | `support_grant` model, request/approve/revoke flow, expiry enforcement, notifications | M | V1 | G2 | 02.7.1-T2, 02.1.2-T1 |
| 02.8.1-T2 | Operator access path: separate DB role, context switch, session banner, alerting on grant and on operator reads | M | V1 | G2 | 02.8.1-T1, 02.6.2-T1 |
**Epic 02.8 total: 2.0**
### Epic 02.9 Cross-tenant isolation test suite (acceptance gate)
#### Story 02.9.1 Isolation suite — acceptance: with two fully populated orgs, no API, FK or RLS test can read, write or infer another tenant's data; the suite is required to pass for M1 to close (00 §4).
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.9.1-T1 | API suite: other-tenant id → 404, token/project scope mismatch → 403, list endpoints never leak counts | M | V1 | G1 | 02.1.1-T2, 02.2.1-T2 |
| 02.9.1-T2 | Composite FK suite: cross-org inserts rejected on every tenant table | M | V1 | G1 | 02.1.1-T1 |
| 02.9.1-T3 | RLS suite: unset GUC → 0 rows, set GUC → exact rows, `relforcerowsecurity` true, app role has no `BYPASSRLS` (platform catalogue rows asserted separately per §4.5; platform-scoped identity tables excluded) | M | V1 | G1 | 02.6.1-T1, 02.6.2-T1, 02.6.1-T2 |
**Epic 02.9 total: 3.0**

### Epic 02.10 Transactional notifications [PROPOSAL review 2026-10-02 P4]
#### Story 02.10.1 One outbound email channel — acceptance: an invite, a support grant, a quota warning and a candidate expiry each send exactly one localized (vi/en) email through the outbox; a bounce or complaint suppresses further mail to that address; no email contains a secret or customer test data.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.10.1-T1 | Provider choice and sender setup (SPF/DKIM/DMARC, sending domain, region vs PDPL transfer note), recorded as a decision in DECISIONS §8 | S | V1 | G2 | — |
| 02.10.1-T2 | `Evidgate.Modules.Notifications`: `notification` table (RLS, idempotency key), outbox-driven sender task with retry/dead-letter, vi/en templates, no PII beyond recipient and org name in logs | L | V1 | G2 | 02.10.1-T1, 01.5.1-T3 |
| 02.10.1-T3 | Bounce/complaint webhook, suppression list, per-org opt-out for non-transactional mail | M | V1 | G2 | 02.10.1-T2 |
| 02.10.1-T4 | Wire consumers: invite (02.1.2-T2), support grant (02.8.1-T1), quota 80 % warning (12.3.1-T4); candidate expiry (08.6.1-T3) is wired in V2 | M | V1 | G2 | 02.10.1-T2, 02.1.2-T2, 02.8.1-T1, 12.3.1-T4 |

#### Story 02.10.2 Chat notifications (Slack, Teams) — acceptance: an org admin routes selected events to a chat incoming webhook without storing the URL as plain text.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 02.10.2-T1 | Incoming-webhook channel per org: URL kept as a secret reference, https-only and private-address guard, per-event opt-in | M | V2 | G4 | 02.10.1-T2, 11.3.1-T1 |
| 02.10.2-T2 | Message templates for blocked release, quota 80 %, candidate expiry; no test content or secrets in the message | S | V2 | G4 | 02.10.2-T1, 02.10.1-T4 |

**Story 02.10.2 total: 1.5** — `[INFERENCE]` sizes; added from the missing-features review of 2026-10-02.

**Epic 02.10 total: 6**
## 10. Dependencies

- **01** (`01.4.1-T1` `Evidgate.Migrator`/DbUp runner, `01.4.2-T1` `ITenantScope`/`set_config` infrastructure, `01.3.1-T1` contracts pipeline, `01.1.2-T1` api/worker boot): SQL migration runner, `contracts/` generation, local PostgreSQL.
- **05** (`05.1.2-T1` `environment` table + `unique (org_id, id)` on it and the composite FK target for `project.default_environment_id`, `05.7.3-T1`): capability matching and the pool model that `service_identity` (02.5.1-T1) references; `05.7.4-T1` consumes this identity from the agent side.
- **10** (`10.7.1-T4` key-rotation runbook, `10.7.1-T5` kill-switch drill): deploy and runbook hooks for credential rotation and operator access.
- **04**: consumes `POST /v1/ci/token` and the ingest token; 09 renders login, membership, token and audit screens against these contracts; 06/07/08 attach their permissions to the catalogue seeded by 02.2.1-T1; 11 consumes the audit and grant surfaces for its threat model and PDPL reporting; 12 reads the quota columns from 02.1.1-T3.
## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| Q1 | Which identity provider does the pilot customer actually use (GitHub org vs Google Workspace)? Confirm in M0 interviews | Tech Lead (O1) | M1 start |
| Q2 | `ci_producer` default TTL (60 min assumed) and whether long CI jobs need a refresh endpoint — **decided: DECISIONS §8.1 D-A3** | Tech Lead | M1 (story 02.4.1-T3) |
| Q3 | Is an org-created custom role allowed in V1, or are built-ins fixed until V2? | Product owner | before 02.2.1-T1 is detailed |
| Q4 | Where does the pepper live in the secret store, and who owns the rotation cadence and the multi-version verify window (T5)? — **decided: DECISIONS §8.1 D-A2, D-A4, D-A5** | Tech Lead + security (11) | before 02.4.2-T1 |
| Q5 | Support-access scope granularity: per object type or per project? | QA Lead + Tech Lead | before 02.8.1-T1 |
| Q6 | Does the pilot need an email-only invite for a user without GitHub/Google (blocked today)? | Product owner | M1 (M0 O1) |
## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 02.1 Tenant core | 5.0 | 0 | 0 | 5.0 |
| 02.2 Roles and permissions as data | 3.0 | 0 | 0 | 3.0 |
| 02.3 Human login (OIDC / OAuth2) | 5.5 | 0 | 3.0 | 8.5 |
| 02.4 CI authentication | 10.5 | 0 | 0 | 10.5 |
| 02.5 Agent service identity | 0 | 2.0 | 0 | 2.0 |
| 02.6 Tenant isolation (RLS) | 4.0 | 0 | 0 | 4.0 |
| 02.7 Audit core | 2.5 | 0 | 0 | 2.5 |
| 02.8 Support access | 2.0 | 0 | 0 | 2.0 |
| 02.9 Cross-tenant isolation suite | 3.0 | 0 | 0 | 3.0 |
| 02.10 Transactional notifications | 4.5 | 1.5 | 0 | 6.0 |
| **Total** | **40.0** | **3.5** | **3.0** | **46.5** |

→ with 20 % buffer **55.8** person-days
## 13. Contract change requests

> **Status:** Applied 2026-09-24: CR-1 → 00 §5.4.1; CR-2 → 00 §8.9; CR-3 → 00 §9.3; CR-4 → 00 §2; CR-5 confirmed (ids resolve).
>
> 2026-09-25 CR-D-1 + TL review: migrated to .NET 10 + CQRS (`ITenantScope`/`set_config` tenant transactions, Mediator authorization behaviour, `Microsoft.IdentityModel.JsonWebTokens`, ASP.NET Core OIDC/OAuth + cookie + antiforgery handlers, xUnit v3/Testcontainers.PostgreSQL); applied T1–T8, T21, T38 — `evidgate_resolve` pre-tenant resolvers, NULL-safe RLS helpers, `unique (org_id, id)` + single exception list, HMAC-SHA256 + versioned pepper (scrypt dropped), GitHub OAuth2 + PKCE login, audit-chain serialisation, `__Host-`/`XSRF-TOKEN` session cookies. Added 02.6.1-T3; resized 02.4.1-T4 (S→M) and 02.4.2-T1 (M→S); totals recomputed (35.5 raw / 42.6 buffered).

> For the integrator; 00/`DECISIONS.md` are not edited by this plan.

| # | Request | Why |
|---|---|---|
| CR-1 | 00 §5.4.1: state that identity tables are platform-scoped, not tenant-scoped — `user`, `user_identity`, `session` carry no customer business data and therefore have no `org_id` (D12) | Rule 1 as written ("every business table has `org_id`") is ambiguous for identity tables; leaving it open invites either a fake `org_id` on users or an isolation test that cannot pass |
| CR-2 | 00 §8: add state machines for the objects introduced here — `ingest_token` (`active → rotated → revoked`, `active → expired`), `session` (`active → revoked|expired`), `support_grant` (`requested → approved → expired|revoked`), `ci_binding` (`active → disabled`) | 00 §8 covers runs, jobs, artifacts, evidence, healing, waivers; identity lifecycles are currently undefined |
| CR-3 | 00 §9.3: add the identity/tenancy endpoints to the core list — `POST /v1/orgs/{o}/projects`, `POST /v1/orgs/{o}/memberships`, `POST /v1/projects/{p}/ci-bindings`, `POST /v1/projects/{p}/ingest-tokens`, `GET /v1/orgs/{o}/audit` | They are referenced by 04, 09 and 12 and are the only way to provision a project; keeping them outside the core list makes the SDK generation story unclear |
| CR-4 | 00 §2: note that `contracts/permissions.json` is the single source of permission strings and that roles are seeded from it (D2) | Prevents a second, hard-coded permission vocabulary later |
| CR-5 | Integrator: 09's dependency on this plan is **reconciled** (re-verified from disk 2026-09-24) — `09.7.1-T2` now uses `02.1.2-T1` + `02.2.1-T1`, and every `02.*` id cited by 09 resolves to a real task here. Keep 02's ids stable: an epic-order clash with 09 is fixed on the 09 side, never by renumbering 02 (the assignment fixes this plan's epic order) | Earlier 09 revisions cited `02.5.1-T1` (V2 agent service identity) for the M1/M2 members page; recorded so the integrator can confirm the 02↔09 matrix in one pass and does not "repair" it by moving scope |
