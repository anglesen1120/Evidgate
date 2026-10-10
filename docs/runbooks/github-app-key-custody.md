# GitHub App private key custody (stub)

Status: **stub** — owned by 10.7.1-T4 (the key-rotation runbook); the full procedure and the
staging drill by a non-author land with 10.7.1 AC1/AC5. Decision source:
[ADR-0007 — Single GitHub App per environment (production + non-production)](../adr/0007-single-github-app.md).
The custodian role is `[OPEN]` in DECISIONS §8 O11.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. Scope

This runbook covers the secrets that authenticate Evidgate as a GitHub App (02.4.3). There are
two Apps: a **production App** (owned by the Evidgate GitHub organization, customer-facing,
public — any account can install it) and a **non-production App** (private, owned by one
Evidgate-owned test organization — installable only on that owning account; dev, staging and CI
share that one installation account) ✅
([Making a GitHub App public or private](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/making-a-github-app-public-or-private)).
Each App has its own private key, client id, client secret, webhook secret and webhook URL. The
production key and secrets are never loaded outside production. Secret names are versioned and
identical in every environment (`github_app_private_key_vN`, `github_app_client_secret_vN`,
`github_webhook_secret_vN`); the values differ per environment (D-A2 "generated per
environment").

| Item | Secret? | Where it lives |
|---|---|---|
| App private key (PEM) — one per App | yes — the single most valuable secret for the App ✅ ([Storing private keys](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps#storing-private-keys)) | the secret store only (see §3) |
| App id — one per App | no — configuration, not a secret | `infra/config-matrix.json` (registered by 02.4.3-T2) |
| App client id — one per App | no — configuration, not a secret | `infra/config-matrix.json` |
| App client secret — one per App | yes — exchanged once for the OAuth `code` at install | the secret store only (see §3) |
| Webhook secret — one per App | yes | the secret store only (see §3) |
| Webhook URL — one per App | no — configuration | `infra/config-matrix.json` |
| Installation access tokens | no — derived, short-lived | in-memory cache only, below expiry, never persisted (02.4.3 §8) |
| User access token (install binding) | no — derived, one-time | in memory for the single `GET /user/installations` check, then discarded; never stored |

The App private key signs the RS256 app JWT that mints installation access tokens ✅
([Generating a JSON Web Token (JWT) for a GitHub App](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app)).
The webhook secret verifies the `X-Hub-Signature-256` HMAC-SHA256 header on every delivery ✅
([Validating webhook deliveries](https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries)).

Install binding: **both Apps** are registered with **Request user authorization (OAuth) during
installation** ✅
([Generating a user access token for a GitHub App](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)).
The install callback exchanges the OAuth `code` for a user access token and binds the row only
when both hold: (a) the `installation_id` appears in the paginated `GET /user/installations`
response for the user who completed the OAuth flow, and (b) that user administers the
installation account — for a User account, `account.id` equals the id from `GET /user`; for an
Organization account, `GET /user/memberships/orgs/{org}` returns `state: active` and
`role: admin` ✅
([Get an organization membership for the authenticated user](https://docs.github.com/en/rest/orgs/members#get-an-organization-membership-for-the-authenticated-user));
⚠️ confirm in 02.4.3-T2 that this endpoint accepts the user access token. Otherwise the callback
returns 403 `installation_not_owned`. The user token is used once and discarded (never stored).
The `installation_id` from the query string alone is never trusted ✅
([About the setup URL](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/about-the-setup-url)).
The install-binding ownership check is `[OPEN]` DECISIONS §8 O12 (recommended `[INFERENCE]`:
the account-admin check above).
The App client id is configuration; the App client secret is a secret (versioned, secret store,
rotated with the key) ✅
([Generating a user access token for a GitHub App](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)).

## 2. Custodian role

**Role: "GitHub App key custodian".** The custodian holds GitHub App admin/owner rights to
manage keys, generates, rotates and deletes keys, and records every action in the log table
of §7. Proposed default: the owner `[OPEN]` (DECISIONS §8 O11; the D-A4 precedent names the
owner custodian of the ingest-token pepper, and O2 records a one-person team).

- The custodian's GitHub account uses a passkey or a hardware security key for 2FA; a password
  plus TOTP alone is not sufficient for an account that can generate App keys.
- The production App is owned by the Evidgate GitHub organization; the non-production App is
  owned by one Evidgate-owned test organization. Each App-owning organization has a second named
  organization owner as the recovery contact for App-admin access `[OPEN]` (DECISIONS §8 O11).
  While the team is one person (O2) there is no second owner; that is the accepted risk, stated
  here explicitly.
- The App private key, the client secret and the webhook secret are listed in the 10.1.2-T1
  secret inventory (name, owner, store, rotation cadence, break-glass holder).
- No break-glass copy of the PEM is made. Recovery is to generate a new key at GitHub, which
  needs App-admin access, so the break-glass holder (10.1.2-T3) is the recovery owner and needs
  that access — not a sealed copy of the key.
- The §7 log and the GitHub organization audit log are a **record, not an independent control**:
  the same person performs the action and writes the entry. Only organization owners can access
  the organization audit log, and it covers the last 180 days ✅
  ([Reviewing the audit log for your organization](https://docs.github.com/en/organizations/keeping-your-organization-secure/managing-security-settings-for-your-organization/reviewing-the-audit-log-for-your-organization)).
  Whether App private-key actions appear as their own audit-log entries is ⚠️ unverified —
  confirm during the 10.7.1-T4 drill and record the answer here.

## 3. Storage

- The App private key, the App id, the client secret and the webhook secret reach the process
  **only** through the one secret-provider interface (DECISIONS §8.1 D-A2, 10.1.1-T2
  `ISecretProvider`).
- Secret names are versioned, e.g. `github_app_private_key_vN`, `github_app_client_secret_vN`
  and `github_webhook_secret_vN`, and are registered in `infra/config-matrix.json` by
  02.4.3-T2 — **not yet**.
- V1 injection is an orchestrator secret or a root-owned `0400` file outside the repository
  tree — never an env-file in the repository, a compose file, shell history, a backup, a crash
  dump or `docker inspect` output. The key is never in the repository, the database, logs, CI
  variables or chat.
- The downloaded PEM is deleted immediately after it is loaded into the store, and the Trash is
  emptied.
- Only the component that mints installation tokens needs the key `[INFERENCE]`; today both
  `Evidgate.Api` and `Evidgate.Worker` mint (§4 step 3), so both hold it. Which one must hold it
  is `[OPEN]` — 02.4.3 §7 does not state it. Revisit with a sign-only store after O3.
- A managed secret store replaces environment injection after O3 (D-A2); GitHub itself
  recommends a key vault over an environment variable ✅
  ([Managing private keys for GitHub Apps](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps)).

## 4. Rotation (scheduled)

Cadence `[OPEN]` — proposal: every 12 months, or on a custodian change. GitHub supports up to
25 private keys per App and recommends multiple keys to rotate without downtime ✅
([Managing private keys for GitHub Apps](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps)).

1. Generate a new private key in the App settings (GitHub stores only the public half) ✅
   ([Generating private keys](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps#generating-private-keys)).
2. Store it as the next version (`github_app_private_key_vN+1`) through the secret interface.
3. Deploy/reload `Evidgate.Api` and `Evidgate.Worker` so both sign with the new key.
4. Verify an installation-token mint succeeds and `github_token_mint_total{result="error"}`
   stays flat (02.4.3 §8).
5. Verify the deployed key fingerprint against the fingerprint GitHub shows for that key pair:
   generate the local fingerprint of the PEM and compare it to the value on the App's
   Credentials page ✅
   ([Verifying private keys](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps#verifying-private-keys)).
6. Delete the old key at GitHub ✅
   ([Deleting private keys](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps#deleting-private-keys)).
   GitHub forbids deleting an App's only key — "you must regenerate a new key before you can
   delete the existing key" ✅ — so the new key from step 1 must exist first.
7. Delete the superseded secret version (`github_app_private_key_vN`) from the store.
8. Record the action in §7.

Client-secret rotation follows the same shape: generate a new client secret in the App settings,
store it as `github_app_client_secret_vN+1`, deploy, then delete the old secret at GitHub and
the superseded version from the store.

Webhook-secret rotation follows the same shape, but a dual-accept window (verify against the
old and the new secret while deliveries are in flight) is a **03.4.1-T2 receiver capability
`[OPEN]`** — this runbook does not assume it exists.

## 5. Compromise response

Order (GitHub forbids deleting an App's only key ✅
([Deleting private keys](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps#deleting-private-keys))):

1. Turn `github_app_enabled` off (02.4.3 §11) — this stops Evidgate's own minting but does not
   invalidate a copy of the key an attacker holds.
2. Revoke every installation token Evidgate holds: call `DELETE /installation/token` with each
   cached token (it revokes the token you authenticate with) ✅
   ([REST API endpoints for GitHub App installations](https://docs.github.com/en/rest/apps/installations)).
   Do this before any restart — a restart only discards the cached tokens, which would stay
   valid up to 1 h.
3. Generate a new private key at GitHub (the old key cannot be deleted until a replacement
   exists ✅
   ([Deleting private keys](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps#deleting-private-keys))).
4. Delete the compromised key at GitHub at once — this is the revoke; it stops new app JWTs
   signed with it.
5. Identify and close the exposure path (host, env file, backup, CI, crash dump) before loading
   the new key.
6. Load the new key into the secret store and deploy/restart `Evidgate.Api` and
   `Evidgate.Worker`.
7. Tokens an attacker minted with the stolen key cannot be enumerated or revoked by Evidgate
   and stay valid up to 1 h after minting — the token expires after 1 hour ✅
   ([Generating an installation access token for a GitHub App](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app)).
   State this residual window in the incident.
8. Rotate the webhook secret and the client secret. Redeliver failed webhook deliveries since
   the change, or run the 02.4.3-T3.2 reconcile task immediately.
9. Open an incident per 10.7.1-T6 and notify affected organization admins (02.10.1) with the
   App id, the exposure window, and a request to review their org audit log for the App's bot
   actor.

A standby key is not kept (a standby would need custody too), so recovery is always
generate-then-delete.

## 6. Verification checklist

- [ ] The App private key, the client secret and the webhook secret resolve only through
  `ISecretProvider`.
- [ ] The App private key, the client secret, the app JWT and installation tokens (`ghs_`
  prefix) are absent from logs, metric labels and exception messages (00 §5.7 S3 redaction
  list).
- [ ] No key material in the repository, database, logs, CI variables or chat.
- [ ] A mint succeeds after rotation and `github_token_mint_total{result="error"}` stays flat.
- [ ] The deployed key fingerprint matches the one GitHub shows (see §4 step 5).
- [ ] The old key is deleted at GitHub and the superseded secret version is deleted from the
  store; the action is recorded in §7.
- [ ] The drill is executed by a person who did not write this runbook (10.7.1 AC1/AC5).

## 7. Drill log

| Date | Executor | Result |
|---|---|---|
| | | |
