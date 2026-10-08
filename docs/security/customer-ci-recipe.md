# Customer CI security guide: the fork-safe recipe

Owner: 11.2.1-T1 ([feature plan](../../plans/features/11-security-compliance/11.2.1-a-fork-pull-request-cannot-reach.md), AC1 and AC4; area plan [FR-11-2 and FR-11-3](../../plans/11-security-compliance.md)). Recipe: [`fixtures/ci-recipes/github-fork-safe.yml`](../../fixtures/ci-recipes/github-fork-safe.yml).

Legend: ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Code citations are `path:line` in this repository as of this change; GitHub citations are pages on docs.github.com read on 2026-10-08 unless another date is given.

## 1. Purpose

A customer's CI runs code Evidgate does not control, and a fork pull request runs code the customer does not control either. This guide and its recipe give a maintainer a GitHub Actions workflow that is safe against fork pull requests by construction:

- the workflow that runs fork code holds no Evidgate credential and no secret;
- results reach Evidgate only through a separate job, started by a maintainer or by a push to the default branch, that mints a **project-scoped, short-lived** `ci_producer` token at run time;
- no long-lived Evidgate credential is stored in the repository at all (FR-11-2 e, FR-11-3).

`scripts/lint-ci-recipe.sh` checks the five rules below on any workflow file (§9).

## 2. What GitHub gives a fork pull request

| Fact | Source |
|---|---|
| With the exception of `GITHUB_TOKEN`, secrets are not passed to the runner when a workflow is triggered from a forked repository, and `GITHUB_TOKEN` has read-only permissions in pull requests from forks. | ✅ [Events that trigger workflows — `pull_request`, workflows in forked repositories](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows) |
| For a pull request event other than `pull_request_target` from a fork, write permissions requested in the workflow are changed to read-only unless an admin enabled "Send write tokens to workflows from pull requests". | ✅ [Workflow syntax — `permissions`, "How permissions are calculated for a workflow job"](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax) |
| Requesting a GitHub OIDC token needs `id-token: write`; without it the OIDC JWT cannot be requested. | ✅ [OIDC reference — "Workflow permissions for the requesting the OIDC token"](https://docs.github.com/en/actions/reference/security/oidc) |
| Hence a fork `pull_request` run gets no OIDC token, because its `id-token: write` is downgraded with every other write permission. The pages above do not state this combination in one sentence. | `[INFERENCE]` from the two rows above ⚠️ |
| `pull_request_target` runs in the context of the default branch of the base repository. `pull_request_target` and `workflow_run` are privileged: they may have repository write access and access to referenced secrets, and combined with a checkout of untrusted pull request code they can be exploited to take over a repository. | ✅ [Events that trigger workflows — `pull_request_target`](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows); ✅ [Secure use reference — "Mitigating the risks of untrusted code checkout"](https://docs.github.com/en/actions/reference/security/secure-use) (read 2026-09-24 and 2026-10-08) |
| A workflow started by `workflow_run` can access secrets and write tokens even if the previous workflow could not. | ✅ [Events that trigger workflows — `workflow_run`](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows) |
| A `pull_request` run has `GITHUB_REF` set to `refs/pull/<number>/merge`. | ✅ [Events that trigger workflows — `pull_request`](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows) |
| An environment with required reviewers holds a job until one reviewer approves; deployment branch rules restrict which branches can deploy to the environment. | ✅ [Deployments and environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments) |

## 3. The five rules

Each rule is FR-11-2 (a)–(e). The lint names a violation by the rule id in the first column.

| Rule id | Rule | Why | GitHub source | In the recipe |
|---|---|---|---|---|
| `R1-no-pull-request-target` | `on:` uses only `push`, `pull_request`, `workflow_dispatch` or `merge_group`; never `pull_request_target` or `workflow_run` (FR-11-2 a). | Both triggers run with the base repository's privileges and secrets; checking out fork code in them hands those privileges to the fork. The safe split is an unprivileged `pull_request` job plus a maintainer-gated job. | ✅ [Secure use — avoid `pull_request_target`/`workflow_run` with untrusted code](https://docs.github.com/en/actions/reference/security/secure-use) (read 2026-09-24) | `github-fork-safe.yml:35-45` |
| `R2-sha-pinned` | Every remote `uses:` is `owner/repo[/path]@<40 lowercase hex>`; local `./…` is allowed; `docker://` is pinned by `@sha256:<64 hex>` (FR-11-2 b). | A tag or branch can be moved to a backdoored commit; a full commit SHA cannot without a SHA-1 collision. | ✅ [Secure use — "Pin actions to a full-length commit SHA… the only way to use an action as an immutable release"](https://docs.github.com/en/actions/reference/security/secure-use) | `github-fork-safe.yml:59,62,69,103,117`, each with a `# vX.Y.Z` comment |
| `R3-token-permissions` | A top-level `permissions:` is required and is `{}`, `read-all`, or a map of `read`/`none` values only; jobs elevate individually (FR-11-2 c). | `GITHUB_TOKEN` should get the minimum permissions; read-only by default, raised per job as needed. | ✅ [Secure use — principle of least privilege for `GITHUB_TOKEN`](https://docs.github.com/en/actions/reference/security/secure-use) | `permissions: {}` at `:48`; `test` gets `contents: read` (`:55-56`); `upload` gets `contents: read`, `actions: read`, `id-token: write` (`:91-94`) |
| `R4-add-mask` | In a `run:` script, a derived value written to `$GITHUB_ENV`, `$GITHUB_OUTPUT` or `$GITHUB_STATE` must first be masked with `::add-mask::` in the same script (FR-11-2 d). | Only stored secrets are redacted automatically; a value minted at run time is printed in clear unless it is registered as a mask before any log line or workflow command uses it. | ✅ [Secure use — "Mask all sensitive information that is not a GitHub secret by using `::add-mask::VALUE`"](https://docs.github.com/en/actions/reference/security/secure-use); ✅ [Workflow commands — "Masking a value in a log": register the secret before outputting it; once per value per job](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-commands) | no `run:` step writes to an environment file; the CLI masks the minted token before writing it (§6) |
| `R5-no-long-lived-credential` | No `secrets` reference except `secrets.GITHUB_TOKEN` (including `secrets['X']`, `toJSON(secrets)`, `secrets: inherit`, job `secrets:` maps); no `--token`-style CLI argument carrying a credential; no `EVIDGATE_*TOKEN*` variable set in the workflow (FR-11-2 e, 00 §5.7 S3). | A stored credential is reachable by every workflow that can read the secret and by everyone with write access; a credential in an argument can show in process listings and logs `[INFERENCE]`. Evidgate's GitHub path needs no stored credential at all. | ✅ [Secure use — least privilege: anyone with write access can read all repository secrets](https://docs.github.com/en/actions/reference/security/secure-use) | no `secrets` reference; stored settings are configuration variables only (`:96,99`); the token is minted at run time (`:127`) |

## 4. The fork-safe flow

```text
fork PR / push ──► job "test"  (permissions: contents read; no Evidgate setting)
                     runs the suite, uploads results as artifact "evidgate-results" (data)
maintainer ──────► workflow_dispatch(run_id)  or  push to main after a green "test"
                     ──► job "upload" in environment "evidgate-upload" (no secrets, reviewers)
                           no checkout; download artifact of run_id as data
                           evidgate auth login --github-env
                             GitHub OIDC JWT (aud "evidgate") ──► POST /v1/ci/token
                             ◄── ci_producer JWT: one project, ≤ 60 min
                           ::add-mask:: then EVIDGATE_CI_TOKEN >> $GITHUB_ENV
                           evidgate manifests upload (more upload steps land with 04.2.2–04.2.4)
```

1. **Test without credentials.** `test` (`github-fork-safe.yml:51-77`) runs on `pull_request` and `push`. It references no variable, no secret and no Evidgate setting, checks out with `persist-credentials: false` (`:61`), and uploads `test-results/` and `evidgate-manifest.json` as an artifact (`:69-77`). A fork run completes without any Evidgate credential (FR-11-3).
2. **Gate the upload.** `upload` (`:79-133`) runs only for `workflow_dispatch` or for a push to `refs/heads/main` after `test` succeeded (`:84`); never for a `pull_request` event. It runs in the environment `evidgate-upload` (`:88`), which the maintainer creates with **no secrets**, required reviewers, and deployment branches limited to `main`.
3. **Treat results as data.** `upload` never checks out code. It validates the run id as a number (`:106-116`) and downloads the artifact with `actions/download-artifact` (`:117-122`) using the job's own `github.token`. Nothing from the artifact is executed; the API validates the uploaded manifest like any other upload.
4. **Mint a project-scoped, short-lived token.** `evidgate auth login --github-env` (`:127`) requests a GitHub OIDC token with audience `evidgate` (`packages/cli/src/auth/exchange.ts:53`) and exchanges it at `POST /v1/ci/token` for a `ci_producer` token. The server-side checks are listed in §4.1.
5. **Mask and export.** The CLI prints `::add-mask::<token>` and only then appends `EVIDGATE_CI_TOKEN` to `$GITHUB_ENV` (§6). Later steps read it from the environment; no step passes it as an argument.

**Why a fork run can never obtain a tenant-level credential (AC4).** Evidgate has no tenant-level CI credential: both grants at `POST /v1/ci/token` mint a token bound to exactly one project (§4.1, §5). The job-level `if:` (`:84`) is not the guarantee: a fork pull request runs its own, possibly edited, copy of the workflow on `pull_request`, so it can drop the condition or add steps. The guarantee rests on three controls the fork cannot change:

- **No credential reaches a fork `pull_request` run.** GitHub gives it a read-only `GITHUB_TOKEN` and no secrets (§2); the `id-token: write` permission it asks for is not granted, so it cannot request an OIDC token `[INFERENCE]` ⚠️ (§2).
- **The `evidgate-upload` environment** allows deployments only from `main`, holds no secrets and requires reviewers ✅ [Deployments and environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments); a job from a fork ref cannot deploy to it.
- **The server checks the token, not the workflow text.** Even with an OIDC token, `ref` must match the binding's `allowed_ref_patterns` and `job_workflow_ref` its `allowed_workflow_refs` (`ExchangeCommands.cs:84-89`); a fork `pull_request` has `ref` `refs/pull/<n>/merge`, which a `refs/heads/main` allowlist refuses (§4.1).

The server refuses an OIDC token whose `event_name` is `pull_request_target` (`GitHubOidc.cs:133-137`, `CiAuthOptions.cs:66-70`) and, with the default `DeniedEventNames`, no other event type. A `workflow_run` run of the allowed workflow file on `main` has `ref` `refs/heads/main` and the same `job_workflow_ref`, so it passes the ref/workflow allowlist above and would mint a token; the only guard is that the workflow file on `main` has no such trigger (rule `R1-no-pull-request-target`, plus branch protection on that file). Whether the server should also refuse `workflow_run` (an event allowlist, or a check of the `environment` claim) is handed to the token-design review (11.2.1-T3). An ingest token (§5) must never be stored as a repository or organisation secret for a workflow that fork pull requests trigger: GitHub withholds secrets from fork runs today, but one changed trigger (`pull_request_target`, `workflow_run`) or one admin setting would hand the secret to fork code.

### 4.1 What the server enforces on the OIDC grant

| Claim | Code |
|---|---|
| `POST /v1/ci/token` is one route for both grants: an `ingest_token` in the body selects the ingest grant, otherwise a GitHub OIDC JWT is expected as the bearer. | `src/Modules/Evidgate.Modules.Identity/Endpoints/CiTokenEndpoints.cs:41-43,76-90` |
| The OIDC JWT must be RS256, signed by a key from the GitHub JWKS, with issuer `https://token.actions.githubusercontent.com`, audience `evidgate`, and a valid lifetime within 60 s skew. | `src/Modules/Evidgate.Modules.Identity/Ci/GitHubOidc.cs:83-121`; defaults `src/Modules/Evidgate.Modules.Identity/Options/CiAuthOptions.cs:50-64` |
| A token whose `event_name` is `pull_request_target` is refused. | `GitHubOidc.cs:133-137`; `CiAuthOptions.cs:66-70` |
| The claims `jti`, `sub`, `repository`, `ref`, `sha`, `job_workflow_ref`, `repository_id`, `run_id`, `run_attempt` are required. | `GitHubOidc.cs:142-157` |
| The tenant is resolved pre-tenant from the `ci_binding` of the immutable `repository_id`; an unbound, unproven or disabled binding is 403 (`binding_mismatch`, `binding_pending`, `binding_disabled`). | `CiTokenEndpoints.cs:120-134`; `migrations/0016_identity_ci_binding.sql:5,23` |
| `ref` must match the binding's `allowed_ref_patterns` and `job_workflow_ref` its `allowed_workflow_refs`, else 403 `binding_mismatch`. A fork `pull_request` has `ref` `refs/pull/<n>/merge` (§2), which an allowlist of `refs/heads/main` does not admit. | `src/Modules/Evidgate.Modules.Identity/Application/Commands/ExchangeCommands.cs:84-89`; `migrations/0016_identity_ci_binding.sql:25-26` |
| A binding starts pending and activates only on an exchange that presents its one-time challenge (`EVIDGATE_BINDING_CHALLENGE`); the stored challenge hash is then cleared. | `ExchangeCommands.cs:71-81` |
| Replay: each OIDC `jti` is recorded in `ci_token_issue`; a second exchange of the same JWT hits `unique (jti)` and is 401 `token_replayed`. | `ExchangeCommands.cs:108-136`; `migrations/0017_identity_ci_token_issue.sql:3-5,30` |
| The minted `ci_producer` JWT is HS256 with our issuer and audience, `token_use` `ci_producer`, one `project_id`, a fresh `jti`, the permission set of the service ceiling, and the run claims; it lives `TokenLifetimeMinutes` (1–60, default 60) and is never refreshed. | `src/Modules/Evidgate.Modules.Identity/Ci/CiProducerTokens.cs:42-46,65-108`; `CiAuthOptions.cs:32-34`; `ExchangeCommands.cs:99-106` |
| A `ci_producer` caller is a service principal scoped to its project: any request addressing another project is 403 `project_scope_mismatch`. | `CiProducerTokens.cs:150-160`; `src/Modules/Evidgate.Modules.Identity/Authorization/PermissionEvaluator.cs:127-130`; `src/BuildingBlocks/Pipeline/Authorization/IRequestPrincipal.cs:43-48` |

## 5. Non-OIDC CIs: the ingest-token grant (explicit provide)

For a CI that cannot present an OIDC token the binding accepts, a project admin creates an **ingest token** and the CI provides it explicitly. Use it only in a **trusted, non-fork context** (a pipeline that runs only on protected branches of the customer's own repository), never in a workflow that untrusted pull requests trigger.

| Claim | Code |
|---|---|
| Create: `POST /v1/projects/{p}/ingest-tokens` (permission `ingest_token:write`) returns the plaintext once; it is not stored and cannot be read again. | `src/Modules/Evidgate.Modules.Identity/Endpoints/CiAdminEndpoints.cs:45-48`; `src/Modules/Evidgate.Modules.Identity/Application/Commands/IngestTokenCommands.cs:56-57,102`; `src/Modules/Evidgate.Modules.Identity.Contracts/CiDtos.cs:85-89` |
| Form `egt_<12-char prefix>_<base64url secret>`; the secret is 256 random bits from the system CSPRNG. | `src/Modules/Evidgate.Modules.Identity/Ci/IngestTokenFormat.cs:7-8,41-43` |
| Storage: only the public prefix (unique platform-wide) and `HMAC-SHA256(pepper_vN, secret)` with its `pepper_version`; a database dump holds nothing usable. | `migrations/0018_identity_ingest_token.sql:3-6,18-20,33,36-37`; `IngestTokenCommands.cs:113-114` |
| Exchange: prefix lookup pre-tenant, then the secret is verified with `CryptographicOperations.FixedTimeEquals`; a retired pepper version fails like an unknown token; revoked/expired tokens are 401 `token_revoked`/`token_expired`. | `CiTokenEndpoints.cs:156-181`; `ExchangeCommands.cs:215-229` |
| Scope: the token belongs to one project (`project_id` foreign key) and carries a subset of `event:write`, `artifact:write`, `run:create`; the minted `ci_producer` carries only those scopes inside the service ceiling, for that one project. | `migrations/0018_identity_ingest_token.sql:16,34,39-40`; `ExchangeCommands.cs:231-238` |

How to provide it (00 §5.7 S3):

- Store it in the CI's **masked secret store** and inject it as the environment variable `EVIDGATE_INGEST_TOKEN` (`contracts/cli.md` §3 and §9). The CLI reads it from the environment only and registers it with its redactor before any output (`contracts/cli.md` §6).
- Never pass it as a CLI argument; the CLI has no token argument. Never commit it, never echo it.
- Create it with an expiry (`expires_at`) and the fewest scopes the pipeline needs. ⚠️ The API does not require an expiry: `expires_at` is optional (`IngestTokenCommands.cs:79-83`, `migrations/0018_identity_ingest_token.sql:22`, `CiDtos.cs:96-100`), while `contracts/cli.md` §9 describes the generic template's token as "created with an expiry". Setting one is the customer's step.

## 6. Masking

- `evidgate auth login --github-env` checks `$GITHUB_ENV` before any exchange, so an unset `$GITHUB_ENV` fails with `missing_config` and nothing is minted or written (`packages/cli/src/commands/authLogin.ts:25-31`; `contracts/cli.md` §7).
- The CLI registers the minted token with its redactor before validating or returning it (`packages/cli/src/auth/exchange.ts:278-285`), refuses a token with a line break, prints `::add-mask::<token>` to the raw stdout, and only then appends an `EVIDGATE_CI_TOKEN` heredoc with a random delimiter to `$GITHUB_ENV` (`authLogin.ts:51-71`). The token is never printed (`authLogin.ts:37-46`).
- Every credential the CLI reads from the environment (`EVIDGATE_OIDC_TOKEN`, the `ACTIONS_ID_TOKEN_REQUEST_*` pair, `EVIDGATE_INGEST_TOKEN`, `EVIDGATE_DEV_TOKEN`, `EVIDGATE_BINDING_CHALLENGE`) is registered with the redactor (`contracts/cli.md` §6).
- Your own steps: if a `run:` script derives a sensitive value (command output, a value computed from a token), print `echo "::add-mask::$VALUE"` as its own command before any other use, in the same job; GitHub needs this once per value per job ✅ [Workflow commands — masking](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-commands). A masked value can be passed to a later step of the same job through `$GITHUB_ENV` or `$GITHUB_OUTPUT` (the page's "Masking a generated output within a single job" example), but not as a job output to another job; use a secret store for that. `R4-add-mask` enforces the order.
- Masking is a log control, not access control: a step that can read the variable can still send it anywhere. Keep untrusted code out of the job that holds the token (§4).

## 7. Rotation and revocation

- **`ci_producer` tokens** are stateless JWTs: at most 60 minutes, never refreshed (`CiProducerTokens.cs:42-46`; `CiAuthOptions.cs:32-34`). There is no revocation of a minted token in V1; the TTL bounds the exposure, a recorded risk in [02.4.1 §6](../../plans/features/02-identity-tenancy/02.4.1-github-oidc-exchange.md#6-states-and-transitions). To stop new tokens for a repository, disable its binding: `DELETE /v1/ci-bindings/{b}` or `PATCH /v1/ci-bindings/{b}` with `status: disabled` (`CiAdminEndpoints.cs:37-43`); the next exchange is 403 `binding_disabled` (`ExchangeCommands.cs:66-69`). Tokens already minted stay valid until they expire (`CiProducerTokens.cs:131-160` checks signature, lifetime and claims only).
- **Ingest token rotation:** `POST /v1/ingest-tokens/{t}/rotate` (`ingest_token:write`) mints a successor with the same project, scopes and expiry and returns its plaintext once; the old token keeps verifying for 24 h, then expires (`CiAdminEndpoints.cs:52-54`; `IngestTokenCommands.cs:132-221`, overlap at `:149-150`). Steps: rotate → put the new value into the CI's masked secret store → run the pipeline once → revoke the old token (or let the overlap end).
- **Ingest token revocation:** `DELETE /v1/ingest-tokens/{t}` is immediate; the next exchange is 401 `token_revoked`; revoking a rotated token ends its overlap now (`CiAdminEndpoints.cs:55-58`; `IngestTokenCommands.cs:224-283`). Revoke at once when a token may have leaked (for example after it was printed in a log), then create a new one. `ci_producer` tokens it already minted stay valid until they expire (≤ 60 min).
- **Pepper rotation** (the server key behind the ingest-token HMAC) is an operator task: a new pepper version signs new tokens while the previous version keeps verifying (`CiAuthOptions.cs:36-48`); the versioned rotation runbook is 10.7.1-T4 and is reviewed in 11.2.1-T3 (AC6).
- **Binding challenge:** single-use. After the first exchange activates the binding, delete the `EVIDGATE_BINDING_CHALLENGE` variable (`ExchangeCommands.cs:77-80` clears the stored hash).

## 8. Residual risk T77: replay in customer CI

Ruling T77 ([plans/features/README.md](../../plans/features/README.md)): shadow replay in customer CI (O10 option a) runs patches from an untrusted context next to the CI's secrets. The written acceptance of that residual risk lives in the V1 replay-sandbox spec (11.2.1-T6, `docs/security/replay-sandbox-v1.md`, not yet written) and the 11.1.1 threat model. The mitigations this recipe supplies:

- `permissions: {}` at workflow level and least-privilege jobs (`github-fork-safe.yml:48,55-56,91-94`);
- no `secrets` reference anywhere (`R5-no-long-lived-credential`);
- the privileged step runs only from `workflow_dispatch` or a push to `main`, in an environment that holds no secrets (`:84,88`);
- egress limited to the app under test: this is the customer's runner or network configuration; the recipe cannot express it. ⚠️ GitHub-hosted runners do not restrict egress by default `[INFERENCE]`.

The second mitigation, the CLI refusing `replay` when the job exposes `GITHUB_TOKEN` write scopes or secret-like environment variables, belongs to 08.4.3 and is heuristic ⚠️.

## 9. Checking a workflow: `scripts/lint-ci-recipe.sh`

The lint lives in the Evidgate repository and needs Node.js and the repository's `npm ci` (for the `yaml` parser); without it it exits 2 (`yaml package is missing`). Run it from an Evidgate checkout against your workflow file:

```sh
scripts/lint-ci-recipe.sh /path/to/your-repo/.github/workflows/evidgate.yml [more.yml ...]
```

Shipping it to customers (as a CLI command or a published package) is not part of this task; it is handed to 11.6.1-T2 with the gate wiring (§10).

It reports each violation on stderr as `lint-ci-recipe: <file>: <rule-id>: <detail>` (control characters in workflow text are escaped) and exits 0 when every file is clean, 1 on any violation or on YAML it cannot parse unambiguously, 2 on a usage or I/O error.

| Rule id | What it checks |
|---|---|
| `R1-no-pull-request-target` | every `on:` event is one of `push`, `pull_request`, `workflow_dispatch`, `merge_group`; anything else (notably `pull_request_target`, `workflow_run`) fails |
| `R2-sha-pinned` | every remote `uses:` (steps and job-level reusable workflows) ends in `@<40 lowercase hex>`; `./` paths pass; `docker://` needs `@sha256:<64 hex>` |
| `R3-token-permissions` | a top-level `permissions:` exists and is `{}`, `read-all` or a map of `read`/`none`; jobs may elevate |
| `R4-add-mask` | a `run:` step must be bash/sh: the step, job or workflow `defaults.run.shell` says so, or there is no `shell:` and `runs-on` is a literal `ubuntu-*`/`macos-*` label (or a self-hosted label set naming `linux`/`macos`); a matrix or expression `runs-on`, or a Windows runner, needs `shell: bash`. A write to `$GITHUB_ENV`, `$GITHUB_OUTPUT`, `$GITHUB_STATE` or `$GITHUB_PATH` (also via `${{ github.env }}` & co., a variable assigned from one, or the bare name as in `$(printenv GITHUB_ENV)`) is accepted only as an `echo`/`printf` redirection or a `cat` heredoc, and every command substitution, `secrets.*`/`steps.*` expression and shell variable in it (except runner-set `GITHUB_*`/`RUNNER_*`) was masked earlier by exactly `echo "::add-mask::$X"` (one variable, expression or command substitution) as its own unconditional, unredirected command outside any `if`/`case`/loop/`{ }`/`( )` body, with no reassignment (`=` or `+=`) of `X` in between; `tee`, `cp`, `dd`, pipelines, env-prefix assignments, `${!…}` indirection, `::stop-commands::` (even quote-split) and command output redirected straight into the file fail; a script line over 8192 characters is refused unread |
| `R5-no-long-lived-credential` | no `secrets` reference other than `secrets.GITHUB_TOKEN`; no `egt_` ingest-token literal; no literal value, and no `vars.*`, under an `env:` key or `with:` input named like token/secret/password/credential/api-key (fail-closed: a non-secret setting with such a name, for example `SECRET_SCANNING_MODE: strict`, is refused too; rename it or pass it from an expression); no credential-carrying CLI flag (`--token`, `--ingest-token`, `--ingest_token`, `--api-key`, `--pass`, `--auth`, `--bearer`, `--pat`, …, or `-t`/`-k` with a value on an `evidgate` command); no `EVIDGATE_*TOKEN*` variable set from anything but a masked runtime value |

The recipe passes; each of the five negative fixtures in [`fixtures/ci-recipes/negative/`](../../fixtures/ci-recipes/negative/) fails the one rule its file name names (AC1).

## 10. Not covered and limits

- **GitHub only.** The recipe is a GitHub Actions workflow; other CIs use the ingest-token grant (§5) and their own controls.
- **Admin settings.** The setting "Send write tokens to workflows from pull requests" changes what a fork run gets ✅ [Workflow syntax — `permissions`](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax); a setting that sends secrets to fork pull-request workflows, or a self-hosted runner that runs fork code, would do the same ⚠️ (not verified this session). The recipe assumes the defaults; the lint cannot see repository settings. Self-hosted runners do not isolate jobs even with environments ✅ [Deployments and environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments).
- **Artifact content is attacker-controlled on fork runs.** The `upload` job sends fork-produced results to the project when a maintainer dispatches it with a `run_id`; the recipe checks only that `run_id` is numeric, not which repository or event produced that run. That is the intent (data, project-scoped, ≤ 60 min token), but a maintainer should dispatch only runs they mean to record and check the run's event and head repository in the Actions UI first. Producer-slot binding (T9) limits which identity may write a run's slots; an automatic provenance check is for the live fork-PR proof (11.2.1-T2).
- **Minted tokens are not revocable** before their expiry (§7).
- **Binding scope is the customer's.** Too broad an allowlist (for example `refs/*` or a wildcard workflow ref) admits more refs or workflow files than the recipe intends; keep `allowed_ref_patterns` to protected branches and `allowed_workflow_refs` to this one file at `refs/heads/main`.
- **Lint scope.** The lint checks the workflow text against the five rules; it does not prove the absence of script injection (for example `${{ github.event.pull_request.title }}` inside `run:`), does not resolve composite actions or reusable workflows it calls, does not read `actions/github-script` scripts (`core.exportVariable`) or other interpreters fed from bash (`python - <<EOF`), and does not model calls to shell functions, `eval`, `source`, `read` or file-descriptor variables (`scripts/lint-ci-recipe.mjs` header); a mask inside a conditional or loop body is not credited. It is not wired into `scripts/verify.sh` or CI by this task; that wiring is handed to 11.6.1-T2.
- **The quickstart template predates this recipe.** `docs/quickstart/github-actions.yml` (04.2.4-T2) sets `id-token: write` at the top level and mints a token on `pull_request`, so it fails `R3-token-permissions`. A repository that accepts fork pull requests must use this recipe; aligning the quickstart template is handed to 11.6.1-T2.
- **Not yet runnable end to end.** `runs create`, `events send`/`runs finalize` and `artifacts upload` are comment placeholders until 04.2.2-T2, 04.2.3-T1/T2 and 04.2.4-T1 land. The live fork-PR proof is 11.2.1-T2 (`fixtures/fork-pr-repo/`).

## 11. Code-versus-plan notes

- ⚠️ Ingest-token expiry is optional in the API (`IngestTokenCommands.cs:79-83`, `migrations/0018_identity_ingest_token.sql:22`), while `contracts/cli.md` §9 says the generic template's token is "created with an expiry". This guide tells customers to set one (§5); the token-design review (11.2.1-T3, AC6) should decide whether the API must require it.
- ⚠️ The comment at `ExchangeCommands.cs:87` says a fork pull request "runs under another repository_id (no binding)". For a `pull_request` event the workflow runs in the base repository `[INFERENCE]`, so the effective guard is the `ref`/`job_workflow_ref` allowlist on the same lines (fork `ref` is `refs/pull/<n>/merge`, §2), plus the missing OIDC token. The behaviour is safe with a protected-branch allowlist; the comment's reason is imprecise. For 11.2.1-T3 to confirm.
