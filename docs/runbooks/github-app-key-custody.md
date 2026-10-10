# GitHub App private key custody (stub)

Status: **stub** — owned by 10.7.1-T4 (the key-rotation runbook); the full procedure and the
staging drill by a non-author land with 10.7.1 AC1/AC5. Decision source:
[ADR-0007 — One Evidgate GitHub App, per-feature minimal tokens](../adr/0007-single-github-app.md).
The custodian role is `[OPEN]` in DECISIONS §8 O11.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. Scope

This runbook covers the secrets that authenticate Evidgate as the single GitHub App (02.4.3):

| Item | Secret? | Where it lives |
|---|---|---|
| App private key (PEM) | yes — the single most valuable secret for the App ✅ | the secret store only (see §3) |
| App id | no — configuration, not a secret | `infra/config-matrix.json` (registered by 02.4.3-T2) |
| Webhook secret | yes | the secret store only (see §3) |
| Installation access tokens | no — derived, short-lived | in-memory cache only, below expiry, never persisted (02.4.3 §8) |

The App private key signs the RS256 app JWT that mints installation access tokens ✅
([Generating a JSON Web Token (JWT) for a GitHub App](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app)).
The webhook secret verifies the `X-Hub-Signature-256` HMAC-SHA256 header on every delivery ✅
([Validating webhook deliveries](https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries)).

## 2. Custodian role

**Role: "GitHub App key custodian".** The custodian holds GitHub App admin/owner rights to
manage keys, generates, rotates and deletes keys, and records every action in the log table
of §7. Proposed default: the owner `[OPEN]` (DECISIONS §8 O11; the D-A4 precedent names the
owner custodian of the ingest-token pepper, and O2 records a one-person team).

A two-person rule is not possible while the team is one person (O2). Compensating control:
every key action leaves an audit trail — a dated entry in §7, plus the GitHub organization
audit log, which only organization owners can read ✅
([Reviewing the audit log for your organization](https://docs.github.com/en/organizations/keeping-your-organization-secure/managing-security-settings-for-your-organization/reviewing-the-audit-log-for-your-organization)).
Whether App private-key actions appear as their own audit-log entries is ⚠️ unverified —
confirm during the 10.7.1-T4 drill and record the answer here.

## 3. Storage

- The App private key, the App id and the webhook secret reach the process **only** through
  the one secret-provider interface (DECISIONS §8.1 D-A2, 10.1.1-T2 `ISecretProvider`).
- Secret names are versioned, e.g. `github_app_private_key_vN` and `github_webhook_secret_vN`,
  and are registered in `infra/config-matrix.json` by 02.4.3-T2 — **not yet**.
- The key is never in the repository, the database, logs, CI variables or chat. A local
  download of the PEM is deleted once it is loaded into the store.
- A managed secret store replaces environment injection after O3 (D-A2); GitHub itself
  recommends a key vault over an environment variable ✅
  ([Managing private keys for GitHub Apps](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps)).

## 4. Rotation (scheduled)

Cadence `[OPEN]` — proposal: every 12 months, or on a custodian change. GitHub supports up to
25 private keys per App and recommends multiple keys to rotate without downtime ✅
([Managing private keys for GitHub Apps](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps)).

1. Generate a new private key in the App settings (GitHub stores only the public half) ✅.
2. Store it as the next version (`github_app_private_key_vN+1`) through the secret interface.
3. Deploy/reload `Evidgate.Api` and `Evidgate.Worker` so both sign with the new key.
4. Verify an installation-token mint succeeds and `github_token_mint_total{result="error"}`
   stays flat (02.4.3 §8).
5. Delete the old key at GitHub ✅.
6. Record the action in §7.

Webhook-secret rotation follows the same shape, but a dual-accept window (verify against the
old and the new secret while deliveries are in flight) is a **03.4.1-T2 receiver capability
`[OPEN]`** — this runbook does not assume it exists.

## 5. Compromise response

1. Delete the compromised key at GitHub immediately. This is the emergency stop of 02.4.3
   §11: the App private key is required to sign the app JWT ✅, so minting that depends on it
   stops `[INFERENCE]`.
2. If no replacement key is ready, turn `github_app_enabled` off (02.4.3 §11).
3. Generate a new key and deploy it per §4 steps 1–4.
4. Installation access tokens already minted stay valid until they expire — the token expires
   after 1 hour ✅
   ([Generating an installation access token for a GitHub App](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app)).
   Revoke what you can with `DELETE /installation/token` (it revokes the token you authenticate
   with) ✅
   ([REST API endpoints for GitHub App installations](https://docs.github.com/en/rest/apps/installations)),
   and restart the processes to drop the in-memory cache.
5. Rotate the webhook secret.
6. Review the GitHub App/installation activity and the Evidgate audit for the exposure window.
7. Open an incident per 10.7.1-T6 and notify affected organization admins (02.10.1).

## 6. Verification checklist

- [ ] The App private key and webhook secret resolve only through `ISecretProvider`.
- [ ] No key material in the repository, database, logs, CI variables or chat.
- [ ] A mint succeeds after rotation and `github_token_mint_total{result="error"}` stays flat.
- [ ] The old key is deleted at GitHub and the action is recorded in §7.
- [ ] The drill is executed by a person who did not write this runbook (10.7.1 AC1/AC5).

## 7. Drill log

| Date | Executor | Result |
|---|---|---|
| | | |
