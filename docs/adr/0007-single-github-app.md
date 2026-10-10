# ADR-0007 — One Evidgate GitHub App, per-feature minimal tokens

## Status

proposed (2026-10-11; custodian [OPEN], DECISIONS §8 O11)

## Date

2026-10-11

## Context

- 02.4.3 §1 goal: **one** Evidgate GitHub App, many installations, one active installation per org. The installation id is stored per org; a short-lived installation token is minted on demand from an RS256 app JWT and cached in memory below expiry, never persisted.
- Four consumers need GitHub and none of them stores the installation: 07.6.1-T1 (check runs), 03.4.1-T1 (catalog webhooks + sync), 03.4.2-T1 + 08.6.1-T1 (healing branch/PR), 05.5.2-T3 (re-run trigger). `plans/REVIEW-2026-10-02.md` §5.4 ("GitHub App installation model: one App or several, installation id and token storage and rotation (four tasks use an App; no task stores the installation) — in 02.4/07.6") is the review item this ADR closes.
- 00 §5.4.8: `POST /v1/webhooks/github` is a pre-tenant entry point — it resolves the tenant through an owner-owned `SECURITY DEFINER` function (`evidgate_resolve.installation_by_id` / `github_repo`) that returns only `(org_id, …, status)`; the app role holds `EXECUTE` and nothing else outside RLS. 00 §5.4 rules 6–7 add `unique (org_id, id)` and composite FKs.
- Rulings (plans/features/README.md §3): **T46** — the GitHub App uses a typed `HttpClient` + RS256 app JWT via `Microsoft.IdentityModel.JsonWebTokens`, no Octokit dependency; **T62** — GitHub delivery is a check run first, commit status only as fallback when the App lacks `checks:write`.
- D-A2 (DECISIONS §8.1): secrets reach the process by environment injection through the one secret-provider interface (10.1.1-T2 `ISecretProvider`); each is `secret: true` with a versioned name in `infra/config-matrix.json`, generated per environment, never committed; a managed store replaces it after O3. D-A4 is the precedent for key custody: the ingest-token pepper lives in the secret store, the owner is custodian, versioned `pepper_vN`.
- O3 (cloud provider/region) is deferred (DECISIONS §8 O3), so V1 resolves secrets through the environment-injected provider and the managed store arrives later.
- Open items this ADR must not silently close: OI-02-13 (GitHub App API facts ⚠️), OI-02-14 (App name, distribution, key custodian), OI-03-06 (minimal permission set ⚠️).

## Decision

One Evidgate-owned GitHub App, installed many times — one installation per customer org, one active installation per org (02.4.3 §4). Evidgate registers the App once; each customer installs it on their org. The installation id is stored per org in `github_installation`; the App private key, App id and webhook secret live only in the secret store.

### Permission matrix

One row per consumer. The App-level **registered** permission set is the **union** of the rows; each row is the **ceiling** for that feature — a feature mints a token with only its own row's permissions and never more.

| Consumer task | What it calls at GitHub (endpoint) | Repository permission (minimum level) | Webhook events subscribed | Version / milestone | Evidence |
|---|---|---|---|---|---|
| 07.6.1-T1 — check-run delivery | `POST /repos/{owner}/{repo}/check-runs`; `PATCH /repos/{owner}/{repo}/check-runs/{check_run_id}` | `checks: write` | — (delivery is outbound) | V1 / M3 | ✅ [Permissions required for GitHub Apps → Repository permissions for "Checks"](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-checks) |
| 07.6.1-T1 — commit-status fallback (T62) | `POST /repos/{owner}/{repo}/statuses/{sha}` | `statuses: write` | — | V1 / M3 | ✅ [Repository permissions for "Commit statuses"](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-commit-statuses) |
| 03.4.1-T1 — catalog webhooks + sync | `GET /repos/{owner}/{repo}/git/trees/{tree_sha}`; `GET /repos/{owner}/{repo}/contents/{path}` | `contents: read` | `push`; `installation`; `installation_repositories` | V1 / M2 | ✅ [Repository permissions for "Contents"](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-contents); ✅ [push event](https://docs.github.com/en/webhooks/webhook-events-and-payloads#push) |
| 03.4.2-T1 + 08.6.1-T1 — healing branch/PR | `POST /repos/{owner}/{repo}/git/refs`; `POST /repos/{owner}/{repo}/git/commits`; `POST /repos/{owner}/{repo}/git/blobs`; `POST /repos/{owner}/{repo}/pulls` | `contents: write`; `pull_requests: write` | `pull_request` (head-drift watch, 08.6.1) | V2 / M4 | ✅ [Repository permissions for "Contents"](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-contents); ✅ [Repository permissions for "Pull requests"](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-pull-requests); ✅ [pull_request event](https://docs.github.com/en/webhooks/webhook-events-and-payloads#pull_request) |
| 05.5.2-T3 — re-run trigger | `POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun`; `POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs` | `actions: write` | — | V1 / M3 | ✅ [Re-run a workflow — "Actions" repository permissions (write)](https://docs.github.com/en/rest/actions/workflow-runs#re-run-a-workflow--fine-grained-access-tokens); ✅ [Re-run failed jobs from a workflow run — "Actions" repository permissions (write)](https://docs.github.com/en/rest/actions/workflow-runs#re-run-failed-jobs-from-a-workflow-run--fine-grained-access-tokens) |
| every row (App-level) | `GET /repos/{owner}/{repo}` (binding verify) | `metadata: read` | — | V1 | ⚠️ the docs list the endpoints Metadata read unlocks but do not state it is mandatory; the registration UI shows it as required — confirm at registration (02.4.3-T2) |

Every write is justified; read is used wherever the endpoint permits it:

- `checks: write` — creating and updating a check run is a write-only endpoint; there is no read-level way to publish one, and T62 makes it the primary delivery surface.
- `statuses: write` — the T62 fallback path only: it fires when the installation did not grant `checks: write`, and it is the only way to publish a commit status.
- `contents: write` — creating the branch, commit and blobs of a healing PR is a write; the catalog sync stays on `contents: read`.
- `pull_requests: write` — opening a PR is a write. The `pull_request` webhook subscription needs only read-level "Pull requests", but the row's ceiling is the write the PR creation needs.
- `actions: write` — re-running a workflow is a write-only endpoint.

App-level endpoints used with the app JWT need no repository permission: `GET /app/installations`, `GET /app/installations/{installation_id}` and `POST /app/installations/{installation_id}/access_tokens` each state "You must use a JWT to access this endpoint" ✅.

Re-run vs checks re-request: `POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun` requires `actions: write` ✅ ([workflow-runs → "Actions" repository permissions (write)](https://docs.github.com/en/rest/actions/workflow-runs#re-run-a-workflow--fine-grained-access-tokens)). The checks re-request endpoint `POST /repos/{owner}/{repo}/check-runs/{check_run_id}/rerequest` requires `checks: write` ✅ ([checks/runs → "Checks" repository permissions (write)](https://docs.github.com/en/rest/checks/runs#rerequest-a-check-run--fine-grained-access-tokens)) but only triggers the `check_run` `rerequested` event for a check run owned by the authenticated App ✅ ([403 "if the check run is not rerequestable or doesn't belong to the authenticated GitHub App"](https://docs.github.com/en/rest/checks/runs#rerequest-a-check-run)) — it does **not** re-run the customer's GitHub Actions workflow, so it is **not** an alternative for 05.5.2-T3 ✅ ([checks/runs](https://docs.github.com/en/rest/checks/runs#rerequest-a-check-run)).

### Token-per-feature subset rule

Each consumer mints its own installation token through `IGitHubInstallationTokens.GetAsync(orgId, feature, ct)` (02.4.3 §5), requesting only its row's permissions via the `permissions` body of `POST /app/installations/{installation_id}/access_tokens`, and `repositories`/`repository_ids` where the call is repo-scoped ✅ ("The installation access token cannot be granted permissions that the app was not granted"; up to 500 repositories). Tokens are never shared across features and are cached per `(org, feature, permission set, repo set)`, in memory only, reused until 5 min before `expires_at` (installation tokens expire after 1 h ✅). A feature requesting a permission the installation did not grant gets 409 `permission_not_granted` (02.4.3 §7) — there is no fallback to a broader token. Adding a permission to the App is an ADR amendment plus GitHub re-consent: the account owner is prompted to approve the new permissions ✅, and the `installation` `new_permissions_accepted` event ✅ updates the stored `permissions` map.

### Key custody

The App private key (PEM), App id and webhook secret reach the process only through the one secret-provider interface (D-A2, 10.1.1-T2 `ISecretProvider`); secret names are versioned (e.g. `github_app_private_key_v1`) and are registered in `infra/config-matrix.json` by 02.4.3-T2, not here. The key is never in the repo, the database, logs or CI variables; a managed store replaces the environment-injected provider after O3. The custodian is the named role **GitHub App key custodian** — proposed default the owner (O2: the team is one person; D-A4 precedent), recorded as [OPEN] in DECISIONS §8 O11. Rotation and compromise response are in the runbook [`../runbooks/github-app-key-custody.md`](../runbooks/github-app-key-custody.md).

## Alternatives rejected

- **One App per feature** — four Apps to register, four private keys to custody, four webhook secrets and four install flows for the same customer org; the customer would install four Apps and re-consent four times. One App with per-feature token subsets gives the same least privilege at the token level with one install (02.4.3 §1).
- **Per-customer self-registered Apps** — every customer would create and own an App; Evidgate could not ship a fixed permission manifest, key custody would move to the customer, and 02.4.3's install callback and `evidgate_resolve.installation_by_id` would have no stable App id. Rejected: it removes Evidgate's ability to enforce the minimal permission set.
- **OAuth App / personal access token (PAT)** — an OAuth App requires interactive user authentication and its tokens are long-lived by default ✅, so it acts on behalf of a user, not an org installation; a PAT is a long-lived user credential carrying the user's whole scope, not a per-feature short-lived token. Both contradict 02.4.3 AC3/AC4 (no long-lived credential, token in memory only) and T46.
- **GitHub Actions `GITHUB_TOKEN` only** — GitHub creates the `GITHUB_TOKEN` per workflow job and it expires when the job finishes ✅, so it is scoped to that run and cannot be minted by Evidgate's server to post a check run, open a PR or re-run a workflow from outside the run. Rejected: it cannot serve the four consumers.

## Consequences

- Unblocks 02.4.3-T2/T3 (registration, install callback, token minting, lifecycle webhooks) and, through them, 07.6.1-T1 (Stage B), 03.4.1-T1 (Stage C), 03.4.2-T1 (V2/Stage D) and 05.5.2-T3 (Stage C).
- One App means one permission set to register and one re-consent prompt per customer when a feature adds a permission; the `new_permissions_accepted` event keeps the stored map current.
- The App-level registered set is the union of the matrix; a feature that needs a permission outside its row is an ADR amendment, not a code change.
- GitHub Enterprise Server is out of V1 [OPEN] (02.4.3 §1); the App model assumes github.com.
- The custodian role and the App name/distribution stay [OPEN] (DECISIONS §8 O11, OI-02-14) until the owner names them; `github_app_enabled` stays off until then (02.4.3 §11).

## Evidence

- ✅ Permissions required for GitHub Apps — https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps (fetched 2026-10-11): `POST`/`PATCH /repos/{owner}/{repo}/check-runs` → Checks write; `POST /repos/{owner}/{repo}/statuses/{sha}` → Commit statuses write; `GET /repos/{owner}/{repo}/contents/{path}` and `GET /repos/{owner}/{repo}/git/trees/{tree_sha}` → Contents read; `POST /repos/{owner}/{repo}/git/refs`, `/git/commits`, `/git/blobs` → Contents write; `POST /repos/{owner}/{repo}/pulls` → Pull requests write; `POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun` and `/rerun-failed-jobs` → Actions write; `GET /repos/{owner}/{repo}` → Metadata read.
- ✅ Re-run a workflow / Re-run failed jobs from a workflow run — https://docs.github.com/en/rest/actions/workflow-runs#re-run-a-workflow--fine-grained-access-tokens (fetched 2026-10-11): `POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun` and `POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs` → "Actions" repository permissions (write) ("The fine-grained token must have the following permission set:").
- ✅ Generating a JSON Web Token (JWT) for a GitHub App — https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app (fetched 2026-10-11): RS256; `exp` no more than 10 minutes in the future; `iss` = client id or app id.
- ✅ Generating an installation access token — https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app (fetched 2026-10-11): `POST /app/installations/{installation_id}/access_tokens`; optional `permissions`, `repositories`, `repository_ids`; the token cannot be granted permissions the app was not granted; it expires after 1 hour.
- ✅ REST API endpoints for GitHub Apps — https://docs.github.com/en/rest/apps/apps (fetched 2026-10-11): `GET /app/installations`, `GET /app/installations/{installation_id}` and `POST /app/installations/{installation_id}/access_tokens` each require a JWT; installation tokens expire one hour from creation.
- ✅ Revoke an installation access token — https://docs.github.com/en/rest/apps/installations (fetched 2026-10-11): `DELETE /installation/token` revokes the authenticating installation token (204).
- ✅ Managing private keys for GitHub Apps — https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps (fetched 2026-10-11): up to 25 private keys per app; use multiple keys to rotate without downtime; keys do not expire and are manually revoked; store the key in a key vault, sign-only.
- ✅ Webhook events and payloads — https://docs.github.com/en/webhooks/webhook-events-and-payloads (fetched 2026-10-11): `installation` actions `created`, `deleted`, `new_permissions_accepted`, `suspend`, `unsuspend` and `installation_repositories` are received by all GitHub Apps by default (cannot be manually subscribed); `push` needs at least read-level "Contents"; `pull_request` needs at least read-level "Pull requests".
- ✅ Rerequest a check run — https://docs.github.com/en/rest/checks/runs#rerequest-a-check-run (fetched 2026-10-11): "The fine-grained token must have the following permission set:" → "Checks" repository permissions (write); `POST /repos/{owner}/{repo}/check-runs/{check_run_id}/rerequest` triggers the `check_run` `rerequested` event for a check run owned by the authenticated App (403 "if the check run is not rerequestable or doesn't belong to the authenticated GitHub App"); it does not re-run a workflow.
- ✅ Choosing permissions for a GitHub App — https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/choosing-permissions-for-a-github-app (fetched 2026-10-11): "GitHub Apps don't have any permissions by default"; when permissions change, the owner of each account where the app is installed is prompted to approve the new permissions.
- ✅ Differences between GitHub Apps and OAuth apps — https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/differences-between-github-apps-and-oauth-apps (fetched 2026-10-11): installation access tokens expire after a predefined time (currently 1 hour); OAuth app tokens are long-lived by default; an OAuth app requires interactive user authentication.
- ✅ `GITHUB_TOKEN` — https://docs.github.com/en/actions/concepts/security/github_token (fetched 2026-10-11): GitHub creates a unique `GITHUB_TOKEN` at the start of each workflow job; it expires when the job finishes.
- ⚠️ `metadata: read` mandatory for every App — the docs list the endpoints it unlocks but do not state it is mandatory; the registration UI shows it as required. Confirm at registration (02.4.3-T2). (OI-02-13)
- ⚠️ App name, public/private distribution and the custodian role — OI-02-14 / DECISIONS §8 O11, owner decision.
- ⚠️ Secondary rate limits and the exact shape of the granted-permissions map — OI-02-13, to verify in 02.4.3-T2/T3.

## Revisit trigger

- A consumer needs a permission outside its matrix row (ADR amendment + GitHub re-consent).
- GitHub Enterprise Server support is requested (currently out of V1 [OPEN]).
- O3 (managed secret store) or O11 (custodian) is decided.
- GitHub changes installation-token or app-JWT lifetimes, the `permissions`/`repositories` body contract, or the private-key limit.
