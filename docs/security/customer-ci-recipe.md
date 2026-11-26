# Customer CI security guide: the fork-safe recipe

Any CI provider can produce reports. Evidgate does not access the repository or verify a branch, commit or pull request. The customer controls which jobs may read a project ingest token.

## 1. Separate test and upload

Run untrusted code in a job without Evidgate credentials or access to a privileged runner. Treat its report and manifest as untrusted data. A separate trusted upload job reads those files without executing them and posts them to the configured Evidgate project.

## 2. Configure the runner

Restrict the upload secret to a protected job and prevent fork jobs from changing its code, dependencies or environment. Verify this restriction in the runner settings, not just in pipeline YAML. Deny access if a protected context cannot be established.

## 3. The five rules

1. Do not execute fork code in a credentialed job.
2. Pin tools and dependencies, including the uploader, to reviewed immutable versions.
3. Grant least privilege to the job and project token; keep optional catalog-write scopes off unless registration is required.
4. Mask secrets before first use. Never send them in arguments, logs, artifacts or unprotected job outputs.
5. Set an expiry, rotate regularly and revoke exposed credentials. Test that untrusted jobs cannot obtain the stored secret.

## 4. Upload and gate

In the trusted context, obtain the project-scoped producer token, upload the manifest and reports, send the evidence and finalize the run. Invoke `evidgate gate` in customer CI and consume its exit code; Evidgate does not publish a check or commit status. Branch, commit, attempt and PR metadata are producer-declared, not authorization. Only non-fork evidence on the declared default branch (or configured release-branch glob) is eligible to gate. If relaying fork results, preserve `ref.is_fork: true`; the trusted uploader’s own branch must not replace the source ref. Fork evidence is advisory-only and cannot bind to a release. Missing evidence never yields PASS.

## 5. Project ingest-token grant
A project admin creates the ingest token; the trusted job supplies it explicitly. Never inject it into a pipeline that executes untrusted contributions.

| Claim | Code |
|---|---|
| Create: `POST /v1/projects/{p}/ingest-tokens` (permission `ingest_token:write`) returns the plaintext once; it is not stored and cannot be read again. | `src/Modules/Evidgate.Modules.Identity/Endpoints/CiAdminEndpoints.cs:45-48`; `src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:56-57,102`; `src/Modules/Evidgate.Modules.Identity.Contracts/CiDtos.cs:85-89` |
| Form `egt_<12-char prefix>_<base64url secret>`; the secret is 256 random bits from the system CSPRNG. | `src/Modules/Evidgate.Modules.Identity/Ci/IngestTokenFormat.cs:7-8,41-43` |
| Storage: only the public prefix (unique platform-wide) and `HMAC-SHA256(pepper_vN, secret)` with its `pepper_version`; a database dump holds nothing usable. | `migrations/0018_identity_ingest_token.sql:3-6,18-20,33,36-37`; `IngestTokenCommands.cs:113-114` |
| Exchange: prefix lookup pre-tenant, then the secret is verified with `CryptographicOperations.FixedTimeEquals`; a retired pepper version fails like an unknown token; revoked/expired tokens are 401 `token_revoked`/`token_expired`. | `CiTokenEndpoints.cs:156-181`; `ExchangeCommands.cs:215-229` |
| Scope: the token belongs to one project (`project_id` foreign key) and carries a subset of `event:write`, `artifact:write`, `run:create`, `decision:read`; the minted `ci_producer` carries only those scopes inside the service ceiling, for that one project. | `migrations/0018_identity_ingest_token.sql:16,34,39-40`; `ExchangeCommands.cs:231-238` |

How to provide it (00 §5.7 S3):

- Store it in the CI's **masked secret store** and inject it as the environment variable `EVIDGATE_INGEST_TOKEN` (`contracts/cli.md` §3 and §10). The CLI reads it from the environment only and registers it with its redactor before any output (`contracts/cli.md` §6).
- Never pass it as a CLI argument; the CLI has no token argument. Never commit it, never echo it.
- Create it with an expiry (`expires_at`) and the fewest scopes the pipeline needs. ⚠️ The API does not require an expiry: `expires_at` is optional (`IngestTokenCommands.cs:79-83`, `migrations/0018_identity_ingest_token.sql:22`, `CiDtos.cs:96-100`), while `contracts/cli.md` §10 describes the generic template's token as "created with an expiry". Setting one is the customer's step.

## 6. Masking

Use the runner’s masked secret store. If a short-lived token must cross a step boundary, use a private credential file or the runner’s protected secret mechanism. Do not publish it as an artifact or job output.

## 7. Rotation and revocation

Rotate or revoke the project credential using the [key-rotation runbook](../runbooks/key-rotation.md). Revocation prevents new exchanges; an already-issued bearer lives until expiration unless the fleet-wide signing key is rotated.

## 8. Replay and untrusted artifacts

Keep replay separate from uploads: run without secrets, limit egress to the application under test, never execute scripts from uploaded results, and do not treat a CI event label as proof of permissions.

## 9. Checking a workflow: `scripts/lint-ci-recipe.sh`

The repository lint checks only the syntax and policy of the historical Actions-format fixtures. For any provider, exercise the credential boundary with a synthetic fork job and a separate trusted job: the fork cannot upload, while the trusted job can upload only to its configured project. Do not print credentials to demonstrate this.
