# Environments: dev, staging and prod

Authority: FR-10-1 in [`../../plans/10-operations.md`](../../plans/10-operations.md) §2, story
[`10.1.1`](../../plans/features/10-operations/10.1.1-three-environments.md), and the shared contracts of
[`../../plans/00-overview.md`](../../plans/00-overview.md) §5.2 (layout), §5.7 S3 (secrets only from the
environment/secret store), §9.1 (operational endpoints) and §11 (tenant isolation). The cloud provider,
region and account ids are **not** decided here: that is 10.8.1 (O3), and every value below that depends
on it is marked `[OPEN]` rather than guessed. This runbook is owned by the plan-10 area plan.

## 1. Environment inventory

FR-10-1 asks for three environments with **separate credentials and data**. What each one is today:

| Environment | Runtime and where it runs | Account / region | Replica counts | Data and retention | Backup |
|---|---|---|---|---|---|
| **dev** | One developer workstation: PostgreSQL 18 and SeaweedFS in containers ([`../../infra/compose.dev.yml`](../../infra/compose.dev.yml)), `Evidgate.Api` and `Evidgate.Worker` as host processes under `dotnet watch`, started by [`../../scripts/dev/up.sh`](../../scripts/dev/up.sh) (01.8.1) | none — a local machine; no provider account and no region | 1 Api + 1 Worker process (the containers are the stateful services) | Disposable developer data only; **never** customer evidence. The named volumes `pg-data` and `s3-data` survive `down.sh` and are deleted by `up.sh --reset` | none — the volumes are the whole state |
| **staging** | A provider account of its own, reached by the deploy exactly as prod is (10.2.1/10.2.2) | `[OPEN]` provider, region and account id — chosen by 10.8.1 (O3). It is a **separate account** from prod, recommended by `../../plans/10-operations.md` §11 (open question: whether staging shares prod's object-storage account) | `[OPEN]`: at least 1 Api and 1 Worker, sized by the load test that runs only here (10.6.1-T1) | **Sanitised data only** (FR-10-1): load-test and drill fixtures with a synthetic tenant, never a copy of customer evidence. Retention `[OPEN]` with 10.5.1-T2 | `[OPEN]` — the RPO 15 min / RTO 4 h drill target of 10.5.1 applies to prod; whether staging runs PITR is settled with the provider |
| **prod** | Behind a load balancer, in its own provider account (FR-10-1) | `[OPEN]` provider, region and account id (10.8.1); residency is a selection criterion owned with plan 11, and no customer data lands before O3 closes | **≥ 2 `Evidgate.Api` replicas and ≥ 1 `Evidgate.Worker` replica** (FR-10-1); exact sizing follows the capacity model (10.6.1-T3) | Customer evidence, immutable, with the retention policy of plan 06 and the residency/PDPL rules of plan 11 | Managed PostgreSQL with PITR (10.5.1-T1); the restore drill (RPO 15 min / RTO 4 h) must pass before the pilot — M2 exit gate, 00 §4 |

Cross-environment rules that hold regardless of the provider:

- **No shared credential and no shared network path** between staging and prod (FR-10-1, AC4). Every
  credential is per environment — the database roles, the bucket keys, the ingest-token pepper and the
  OIDC client secret; the network rules that enforce the split are 10.1.1-T3.4.
- **Load tests and drills run against staging only** (area plan §3): production is never a load-test
  target, because a run's evidence must stay trustworthy.
- **Migrations reach an environment only through its deploy job** as the owner role (10.2.2-T1,
  01.4.1-T2); nobody runs them against prod from a developer machine.
- **Secrets are never in this repository and never baked into an image** (00 §5.7 S3): staging and prod
  resolve them from the provider's secret store at runtime (10.1.1-T2/T3), and the only committed
  credentials are the local-only SeaweedFS identity of the dev stack
  ([`../../infra/seaweedfs/s3-config.json`](../../infra/seaweedfs/s3-config.json)) and the
  `__PLACEHOLDER__` values of [`.env.dev.template`](../../.env.dev.template).

## 2. Configuration surface

One machine-readable matrix declares the whole `EVIDGATE_*` surface, and one file per environment
declares what that environment answers for:

| File | Content |
|---|---|
| [`../../infra/config-matrix.json`](../../infra/config-matrix.json) | One row per variable: consumers, type, boot constraints, per-environment `forbiddenValues`, secret-or-not, and per environment `required` + `source` |
| [`../../infra/dev/environment.json`](../../infra/dev/environment.json) | The dev environment: provided keys and their origin, probe behaviour, ingress rule |
| [`../../infra/staging/environment.json`](../../infra/staging/environment.json) | The same for staging |
| [`../../infra/prod/environment.json`](../../infra/prod/environment.json) | The same for prod |

`source` (and the matching `origin` in the environment file) is one of:

| Origin | Meaning |
|---|---|
| `env-file` | A committed dev file: `.env.dev.template`, generated once into the git-ignored `.env.dev` by `up.sh` |
| `environment` | A plain (non-secret) environment variable injected by the deploy pipeline |
| `secret-store` | Secret material resolved from the provider's secret store and injected at runtime (10.1.1-T2/T3, and the O3 swap point of 10.1.1-T2.2) |
| `repo-file` | Materialised by the dev stack from a committed infra file (only the local SeaweedFS identity) |
| `default` | Not provided; the option's own default applies |

Every row carries a `scope`: `runtime` for a variable consumed by `Evidgate.Api`, `Evidgate.Worker` or
`Evidgate.Migrator`, `dev-stack` for one consumed only by the local stack and its scripts
(`EVIDGATE_SEAWEEDFS_S3_CONFIG`, the compose ports, the generated role passwords). Test-harness and
CI-tooling switches are outside the matrix by design: `EVIDGATE_S3PARITY__*`,
`EVIDGATE_ISOLATION__*` and `EVIDGATE_VERIFY_SKIP_DOCKER` configure one test run or one CI job, not an
environment.

A second input is outside the matrix for the same reason — no .NET host reads it: the `apps/web`
static host's CSP allowlist. **`EVIDGATE_WEB_CONNECT_SRC`** is consumed by the edge that emits the
app-origin `Content-Security-Policy` (`apps/web/scripts/csp.mjs`, `parseConnectSrc`), never by an
`EVIDGATE_*` option, so it has no matrix row and `scripts/check-env-matrix.mjs` never sees it.

| Input | Value rule | Pairing | When it is wrong |
|---|---|---|---|
| `EVIDGATE_WEB_CONNECT_SRC` | space- or comma-separated bare `https:` (or `wss:`) origins with an optional port 1–65535; a path, `http:`, a wildcard or an empty entry fails the deploy in `parseConnectSrc` | must contain the **origin of the `evidgate-otlp-endpoint`** URL the same edge injects into `index.html` — that `meta` tag is 10.2.1-T1.4's and is `https:` only (`https://otlp.example.com` for `https://otlp.example.com/v1/traces`); the collector origin itself is **10.3.1-T2 [OPEN]** | missing origin: the browser blocks the export as a `connect-src` violation and the app disables its exporter (one `console.warn`), while tracing and `traceparent` keep working; extra origins: the policy is wider than the endpoint needs |

The committed policy (`apps/web/scripts/app-origin-csp.json`) keeps `connect-src 'self'` and carries
no collector origin. From the hosting side the same rule is in
[`../../infra/README.md`](../../infra/README.md), "Web bundle hosting: CSP and the telemetry
pairing".

The collector must also allow the web app's origin by CORS: the browser exporter POSTs
`Content-Type: application/json` cross-origin, so the collector (or the edge in front of it) answers the
`OPTIONS` preflight and sends `Access-Control-Allow-Origin` for the app origin and
`Access-Control-Allow-Headers: content-type`. A missing header blocks every export silently (the app
detects CSP violations only). Owners: 10.2.1-T1.4 (edge), 10.3.1-T2 (collector) **[OPEN]**.

`requiredAtBoot` in the matrix means the process exits **non-zero, naming the key** when the value is
missing or malformed — the validated-options mechanism of 01.1.2-T1 (`IOptions` +
`ValidateDataAnnotations` + `ValidateOnStart`, and `MigratorOptions.FromEnvironment` for the migration
job). A `[Required]` property whose default initializer already satisfies the attribute is **not**
boot-required, because the missing value leaves the option valid: `EVIDGATE__WORKER__HEALTHADDRESS`
defaults to `0.0.0.0`, so an environment that says nothing boots, and the matrix records
`requiredAtBoot: false` for it.

A third mechanism exists for options a host may never touch: the object-storage options
(`EVIDGATE__OBJECT__*`, 06.1.2-T1) are validated **lazily**, when `IObjectStorage` is first resolved,
not at host start — so a host that never serves an upload boots with no object-storage configuration at
all, and every one of those rows is `requiredAtBoot: false`. The endpoint override is checked there too:
it must be an absolute http(s) URL with no userinfo, query or fragment, so a crafted value cannot
redirect a signed request at another host (SSRF-by-config).

A third kind of row is a non-secret **tunable safety cap**: the ingestion limit options
(`EVIDGATE__INGESTION__MAXEXPANDEDBYTES` and `EVIDGATE__INGESTION__MAXEXPANSIONRATIO`, 04.1.1-T3) are
ordinary bound integers with a safe default (8 MiB expanded, 20:1 gzip ratio), so no environment provides
them — every row is `source: default`, `required: false`, `requiredAtBoot: false` — while the matrix still
records the option's `[Range]` bounds (1 MiB..64 MiB and 1..1000), which the lint derives from the
attributes.

`required` is the lint's per-environment rule: the environment must declare the key whatever the boot
would do without it. That is how a security-relevant default stays visible — every environment declares
the Worker's bind address and the OTLP endpoint even though both have a usable default (or an optional
role), so a deployment cannot inherit one by accident. `requiredAtBoot` implies `required` everywhere;
the converse does not hold, and that asymmetry is deliberate.

### Changing a value

Set the environment variable (dev: edit `.env.dev`; staging/prod: the secret store or the deploy's
environment), restart the process, and nothing in the code changes — that is 10.1.1-T2.2's swap-point
property. Rotation of ingest tokens, DB credentials, JWT keys and the HMAC pepper is
`10.7.1-T4`'s runbook, not this one.

### Secret resolution

Every secret an Evidgate process resolves goes through one interface,
`ISecretProvider.GetSecretAsync(name)` (`Evidgate.BuildingBlocks.SharedKernel.Secrets`), and is never read
from `IConfiguration` or the environment directly. `SecretNames.All` is the registry of those names, and
they are the existing `EVIDGATE_*` variable names — nothing was renamed: the Api/Worker database
connection string (`EVIDGATE__DATABASE__CONNECTIONSTRING`), the task sweep connection string
(`EVIDGATE__TASKS__SWEEPCONNECTIONSTRING`), the CI signing key, pepper and previous pepper
(`EVIDGATE__CI__SIGNINGKEY`, `EVIDGATE__CI__PEPPER`, `EVIDGATE__CI__PREVIOUSPEPPER`), the object-storage
identity (`EVIDGATE__OBJECT__ACCESSKEY`, `EVIDGATE__OBJECT__SECRETKEY`) and the Migrator
connection string (`EVIDGATE_MIGRATOR__CONNECTIONSTRING`). A name outside the registry is refused with an
`ArgumentException` that names only the name. The registry is not the list of `secret: true` rows in
`infra/config-matrix.json`: a matrix row that only the dev stack uses (for example the S3 keys in
`.env.dev.template`) joins `SecretNames` when a process starts to consume it (OIDC 02.3.1-T1), not
before. The object-storage identity is the worked example: the dev stack's `EVIDGATE_S3_*` keys stay
`dev-stack`-only, while the runtime options of 06.1.2-T1 resolve `EVIDGATE__OBJECT__ACCESSKEY` and
`EVIDGATE__OBJECT__SECRETKEY` through the provider and are declared `secret: true`.

**Api and Worker.** V1 registers `EnvironmentSecretProvider` once, with `TryAddSingleton`, in
`AddEvidgateSecrets` (`src/BuildingBlocks/Persistence/Secrets/SecretProviderExtensions.cs`). It looks a
secret name up in the host `IConfiguration` under its configuration key (`SecretNames.ToConfigurationKey`:
`__` becomes `:`), so **any** configuration source the host has satisfies it — environment variables,
`appsettings*.json`, the command line, or an in-memory test configuration. Production supplies secrets
only through the environment. Never pass a secret on the command line (any local user sees it in `ps`)
and never put one in a committed `appsettings*.json`: the no-secret-literals scan (§4) and the
architecture tests fail on a committed literal. The database and sweep connection strings reach
`DatabaseOptions`/`TasksOptions` through post-configure steps that call the provider after the binder
has run. A value never reaches a log line, an exception message or `ToString`.

**Migrator.** The Migrator is not DI-composed. `MigratorOptions.FromEnvironment`
(`src/Evidgate.Migrator/MigratorOptions.cs`) takes an optional `secrets` parameter; when it is omitted it
builds an `EnvironmentSecretProvider` over the same process-environment snapshot it reads its other
variables from.

**The O3 swap point.** A managed secret store (10.8.1) replaces the single `AddEvidgateSecrets`
registration in the Api and Worker, and is passed as the `secrets` argument of
`MigratorOptions.FromEnvironment` in the Migrator. No consumer changes, because every consumer already
depends on `ISecretProvider` and not on where the value lives. Resolution is synchronous — options
materialisation calls `SecretProviderSyncExtensions.GetSecret`, which relies on the provider completing
synchronously — so a managed store must prefetch its values at boot rather than fetch on demand.

### Adding a key

1. Add the property or constant to the options class (or read it explicitly and fail when it is
   missing).
2. Add its row to `infra/config-matrix.json`. `node scripts/check-env-matrix.mjs` tells you the exact
   row it expects — it derives consumers, type, `requiredAtBoot` and the `[Range]`/`[MinLength]`/
   `[Url]`/`[EnumDataType]` constraints from the source, so a wrong row fails the lint instead of
   documenting a guess.
3. Declare the key in each `infra/<env>/environment.json` with its origin (required keys must be
   declared; a key you do not provide takes `origin: "default"`).
4. Give dev a value in [`.env.dev.template`](../../.env.dev.template), and add it by hand to an existing
   `.env.dev` with the role's current password: the file is generated **once**, and regenerating it would
   mint new passwords while the `pg-data` volume still holds the old ones — the stack would then refuse
   its own credentials. Put the staging/prod values in the provider's secret store (10.1.1-T3).
5. Run `node scripts/check-env-matrix.mjs`.

Never set `EVIDGATE__ISOLATION__SELFCHECK=Disabled` in staging or prod: `Disabled` exists for hosts that
deliberately boot without a database (the contract-surface and shutdown suites). The default is
`Required`, which refuses to start a process that cannot confirm tenant isolation (02.6.1-T1.4). The lint
enforces the ban rather than documenting it: the matrix row carries
`forbiddenValues: {staging: ["Disabled"], prod: ["Disabled"]}`, so an `environment.json` in either
environment that declares the value fails `node scripts/check-env-matrix.mjs` with
`E env: … declares forbidden value "Disabled" in <env>`. The host itself still accepts the value — a test
host legitimately runs with it — so the rule lives where the environment is described, not in the process.

Rows may carry `forbiddenValues` — a map from environment to the values that environment must not
declare — and the lint checks every declared `value` in `infra/<env>/environment.json` against it and
against the row's `constraints.enum`, in both directions: a value outside the enum and a value the
environment forbids are both errors. The enum comes from the option's `[EnumDataType]`, so the matrix
cannot list a value the code would refuse.

### Probe configuration

Each environment file declares the liveness/readiness paths and thresholds used for that environment, and
[`../../scripts/smoke-environment.sh`](../../scripts/smoke-environment.sh) reads them from the same file —
so the probe a deploy uses cannot drift from the documented one.

| Process | Path (00 §9.1) | dev / staging | prod |
|---|---|---|---|
| Api, Worker | `/internal/health/live` | every 10 s, 1 s timeout, 3 failures | every 10 s, 2 s timeout, 3 failures |
| Api, Worker | `/internal/health/ready` | every 5 s, 3 s timeout, 3 failures, startup grace 60 s | every 5 s, 3 s timeout, 3 failures, startup grace 120 s |

The Worker serves them on `EVIDGATE__WORKER__HEALTHPORT` (8081) with its own minimal listener, bound to
`EVIDGATE__WORKER__HEALTHADDRESS` — `127.0.0.1` in dev, the instance's own interface in a deployment,
where the edge is what keeps the path off the public ingress; the Api serves them on its
`ASPNETCORE_URLS` endpoint. `ready` is 200 only with a reachable database and the
migration journal at the expected head, and 503 otherwise (01.1.2-T1's checks).

## 3. The ingress rule

00 §9.1: `/internal/health/live`, `/internal/health/ready` and `/metrics` are outside `/v1`, carry no
tenant scope, are absent from `contracts/openapi.yaml`, and **are not reachable from the public ingress**.
Operator actions live under `/internal/operator/*` on the operator ingress only, authenticated as
`platform_operator` with an audited support grant (TL T16).

The rule is expressed provider-neutrally, per environment, in the `ingress` block of
`infra/<env>/environment.json`:

- `publicPaths: ["/v1/*"]` — the only paths the load balancer forwards to `Evidgate.Api`;
- `operatorPaths: ["/internal/operator/*"]` — served on a **separate operator ingress**, not the app's
  public ingress, and additionally authenticated as `platform_operator` with an audited support grant
  (00 §9.1, TL T16). The rule asserted here is only the part both ingresses share: these paths are never
  on the public path list;
- `internalOnlyPaths: ["/internal/*", "/metrics"]` — reachable on the internal path (the instance's
  private network) only.

It is checked in two places:

1. **Statically**, before a deploy: `node scripts/check-env-matrix.mjs` fails when a public path starts
   with `/internal`, when `/internal/*` or `/metrics` is on the public list, when the internal-only list
   omits one of them, or when an operator path appears on the public list.
2. **On the running process**: `scripts/smoke-environment.sh --env <env>` asserts both health paths
   answer on the internal address, that every listener of the probed ports is bound to a loopback address
   only, and — when the host has a non-loopback address — that the internal path is **refused** on it.
   For dev that is the whole rule, because the dev stack has no public ingress at all: compose publishes
   `127.0.0.1:${EVIDGATE_DB_PORT}` and `127.0.0.1:${EVIDGATE_S3_PORT}` and the Api binds
   `http://127.0.0.1:8080`. The Worker's own listener is the one exception today; §5.3 and §6 record it.

What remains provider-side is the edge/load-balancer configuration that implements these path lists —
10.2.1-T1 for the images and the edge, and 10.2.2-T1 for the post-deploy verification that reads
readiness through the internal path before it promotes a release. There is nothing in the application to
configure for it: the hosts serve the operational endpoints and never decide who may reach them.

## 4. Running the checks

```sh
# configuration lint, pre-deploy and in CI (no Docker, no running stack)
node scripts/check-env-matrix.mjs
node scripts/check-env-matrix.mjs --self-test   # the lint's own pass/fail fixtures

# no literal secret committed to the repository or baked into an image (AC3)
scripts/check-no-secret-literals.sh
scripts/check-no-secret-literals.sh --self-test   # the scan's own pass/fail fixtures

# per-environment smoke, against a running environment
scripts/dev/up.sh                     # dev: containers + migrations + Api and Worker
scripts/smoke-environment.sh --env dev
scripts/smoke-environment.sh --env staging --base-url http://<internal-host>:8080
```

`smoke-environment.sh` exits `0` when every declared probe answered and the ingress assertions held;
`1` on a failed assertion (a probe did not answer, liveness was not 200, readiness answered something
other than 200/503, or a port is bound to a non-loopback address); `3` when the paths answered but
readiness is 503 because a dependency is down — a deploy pipeline can then tell a broken release from a
database that is still starting (pass `--allow-503` to treat that as success on purpose); `2` on a usage
error.

`check-no-secret-literals.sh` scans every tracked and untracked-not-ignored file under `src/`, `tests/`,
`packages/`, `apps/`, `scripts/`, `infra/`, `.github/` and `.husky/`, plus every `Dockerfile*` /
`*.Dockerfile`, `compose*.yml` / `docker-compose*.yml`, `appsettings*.json`, `.env*` template and
`.npmrc` anywhere, and the root `*.yml`/`*.yaml`/`*.json`/`*.props`/`*.config` files. Build output
(`node_modules/`, `bin/`, `obj/`, `artifacts/`) is skipped only when it is untracked; a tracked file is
always scanned. Every file is read as bytes, none is skipped as binary, and the scan fails closed: an
unreadable file or any grep error exits `2`, never `0`.

The rules are `pem-private-key`, `aws-access-key-id`, `github-token`, `slack-token`, `jwt`,
`connstr-password` (a connection string with a literal password), `url-userinfo` (a URL with a literal
`user:password`), `secret-assignment` (a configuration or shell key that names a secret assigned a
literal value, including Dockerfile `ENV`, `export`, `env` and `-e` forms and JSON keys) and
`secret-key-material` (in any in-scope file, C# and TypeScript included, an identifier naming a secret
assigned a quoted base64-looking literal of 32 characters or more). Placeholders pass (`$NAME`,
`${NAME}`, `${NAME:?message}`, `${NAME:-}`, `${{ … }}`, `<…>` and the like), but a placeholder with a
non-empty default — `${NAME:-value}` — is a literal and fails `secret-assignment` (the self-test's
"placeholder-default literal" case). A hit prints `file:line: <rule>` and never the matched value; the
scan exits `0` clean, `1` on a hit and `2` on a usage, configuration or read error.

Reviewed non-secrets — the local-only dev SeaweedFS identity, throwaway test credentials — are listed in
[`../../scripts/no-secret-literals-allow.txt`](../../scripts/no-secret-literals-allow.txt), one per line:
`<path>\t<rule id>\t<reason>[\t<line regex>]`. `<path>` is an exact repository path or a glob whose `*`
never crosses `/` (only `**` does); `<rule id>` is exactly one rule, and the wildcard rule `*` is refused
as a configuration error; the optional `<line regex>` is a POSIX ERE the hit line must match, so an entry
covers the reviewed values only and any later literal in the same file still fails.

### When the lint fails

| Message | What it means | Fix |
|---|---|---|
| `E discovery: <KEY> is consumed by the code but missing from infra/config-matrix.json` | A new `EVIDGATE_*` key reached the source without a matrix row | Add the row (the lint's `--print-discovered` prints the derived facts) |
| `E discovery: <KEY> is declared … but nothing consumes it` | A row outlived its key | Delete the row, or the key was renamed |
| `E matrix: <KEY>: consumers … but the reference graph says …` | The row claims the wrong processes | Correct `consumers`; the graph is derived from the `.csproj` references |
| `E matrix: <KEY>: requiredAtBoot …` / `constraints …` | The row disagrees with `[Required]`, `[Range]`, `[MinLength]`, `[Url]` or `[EnumDataType]` in the source | Correct the row, or state the boot requirement in `requiredBy` when it comes from an explicit reader |
| `E env: infra/<env>/environment.json: <KEY> is required in <env> but is not declared in config` | A required variable is missing from the environment | Declare it, or change `required.<env>` in the matrix (a decision, not a shortcut) |
| `E env: … origin … but the matrix source.<env> is …` | The environment file and the matrix disagree about where the value comes from | One of the two is stale; fix the one that is wrong |
| `E env: … declares forbidden value … in <env>` | The environment declares a value its matrix row forbids there (W7-SEC-04: `Disabled` for `EVIDGATE__ISOLATION__SELFCHECK` in staging/prod) | Declare the value the environment is allowed to use, or change `forbiddenValues` in the matrix (a decision, not a shortcut) |
| `E env: … declares value … but the matrix enum is …` | The declared value is not one the option accepts | Use one of the enum's values; the enum is derived from the option's `[EnumDataType]` |
| `E matrix: <KEY>: forbiddenValues.<env> …` | The banned list names an unknown environment or a value the row's enum does not allow | Correct the list — it may only forbid a value the option can take |
| `E ingress: <path> is an operational path and cannot be on the public ingress` | A `/internal/*` or `/metrics` path was listed as public | Remove it from `publicPaths`: 00 §9.1 forbids it |

## 5. Verified evidence

Measured on the machine this repository is developed on (macOS, colima), 2026-09-27, with the dev stack
of `scripts/dev/up.sh`. The boot-failure and lint outputs are reproduced by the commands shown; the
negative proofs were run once and reverted (the files were restored byte-identically).

### 5.1 Configuration lint (AC1)

```console
$ node scripts/check-env-matrix.mjs
check-env-matrix: 39 keys (26 consumed by a process) match infra/config-matrix.json and infra/{dev,staging,prod}/environment.json; /internal/* + /metrics stay off the public ingress   # exit 0

$ node scripts/check-env-matrix.mjs --self-test
ok    clean
ok    consumed-key-omitted-from-matrix
ok    required-key-absent-from-env-dir
ok    internal-path-on-public-ingress
ok    secret-literal-in-env-file
ok    new-literal-key-in-code
ok    boot-requirement-unproven
ok    required-by-missing-file
ok    key-name-only-in-a-comment
ok    forbidden-value-in-prod
ok    value-outside-the-enum
ok    forbidden-value-outside-the-enum
check-env-matrix --self-test: 12 cases passed   # exit 0
```

Negative proof of the two failure modes AC1 names, on this tree (temporary edits, reverted):

```console
# a consumed key with no matrix row: a ProofPort property added to WorkerOptions.cs, plus
# EVIDGATE__TASKS__SWEEPCONNECTIONSTRING removed from infra/prod/environment.json
$ node scripts/check-env-matrix.mjs
E discovery: EVIDGATE__WORKER__PROOFPORT is consumed by the code but missing from infra/config-matrix.json
E env: infra/prod/environment.json: EVIDGATE__TASKS__SWEEPCONNECTIONSTRING is required in prod but is not declared in config
check-env-matrix: 2 error(s)   # exit 1
```

Negative proof of the W7-SEC-04 value rules, on this tree (temporary edits, reverted; the three
`--self-test` cases above fail when the two checks are removed, and these are the same rules against the
real matrix):

```console
# infra/prod/environment.json declares EVIDGATE__ISOLATION__SELFCHECK with origin environment and
# value Disabled, and the matrix source.prod for that row is environment
$ node scripts/check-env-matrix.mjs
E env: infra/prod/environment.json: EVIDGATE__ISOLATION__SELFCHECK declares forbidden value "Disabled" in prod (the matrix forbids Disabled there)
check-env-matrix: 1 error(s)   # exit 1

# the same key declared as "Maybe" instead
$ node scripts/check-env-matrix.mjs
E env: infra/prod/environment.json: EVIDGATE__ISOLATION__SELFCHECK declares value "Maybe" but the matrix enum is Required/Disabled
check-env-matrix: 1 error(s)   # exit 1
```

### 5.2 The boot-failure half of AC1

A host started with no `EVIDGATE__DATABASE__CONNECTIONSTRING` exits non-zero naming the key, and the
health port never binds (01.1.2-T1's validated options; this story consumes them):

```console
$ env -i HOME="$HOME" PATH="$HOME/.dotnet:/usr/bin:/bin" DOTNET_ROOT="$HOME/.dotnet" \
    ASPNETCORE_URLS=http://127.0.0.1:18080 dotnet src/Evidgate.Api/bin/Debug/net10.0/Evidgate.Api.dll
Configuration invalid: DataAnnotation validation failed for 'DatabaseOptions' members: 'ConnectionString' with the error: 'EVIDGATE__DATABASE__CONNECTIONSTRING is required'.
$ echo $?
1
$ lsof -nP -iTCP:18080 -sTCP:LISTEN      # nothing listening
```

### 5.3 The per-environment smoke, run against the dev stack (AC2)

`scripts/dev/up.sh` brought the stack up (49 s warm, 387 s on the first cold run; API and Worker ready,
migration journal head `0011_identity_membership.sql` — re-measured 2026-09-27 after that migration
landed, 18 s warm). Run from the repository root:

```console
$ scripts/smoke-environment.sh --env dev
smoke-environment: env=dev api=http://127.0.0.1:8080 worker=http://127.0.0.1:8081 (from infra/dev/environment.json)
ok    api    GET http://127.0.0.1:8080/internal/health/live -> 200
ok    api    GET http://127.0.0.1:8080/internal/health/ready -> 200 (ready)
ok    worker GET http://127.0.0.1:8081/internal/health/live -> 200
ok    worker GET http://127.0.0.1:8081/internal/health/ready -> 200 (ready)
ok    api    port 8080: every listener is loopback-only
ok    worker port 8081: every listener is loopback-only
ok    GET http://192.168.100.108:8080/internal/health/live -> refused off-loopback (internal path is not on the public ingress)
ok    GET http://192.168.100.108:8081/internal/health/live -> refused off-loopback (internal path is not on the public ingress)
smoke-environment: PASSED (env=dev, 0 skipped assertion(s))   # exit 0
```

```
$ lsof -nP -iTCP:8080 -sTCP:LISTEN | tail -1
Evidgate. 65355 mapmap 305u IPv4 ... TCP 127.0.0.1:8080 (LISTEN)
$ lsof -nP -iTCP:8081 -sTCP:LISTEN | tail -1
Evidgate. 65241 mapmap 225u IPv4 ... TCP 127.0.0.1:8081 (LISTEN)
```

That green run is the *second* run. The first one failed, and the failure was the point of the check:

```console
# excerpt of the first run (before the bind address was configurable)
ok    worker GET http://127.0.0.1:8081/internal/health/ready -> 200 (ready)
ok    api    port 8080: every listener is loopback-only
FAIL  worker port 8081 is bound to * — an operational port must be loopback-only (00 §9.1)
ok    GET http://192.168.100.108:8080/internal/health/live -> refused off-loopback (internal path is not on the public ingress)
FAIL  GET http://192.168.100.108:8081/internal/health/live -> 200 off-loopback: an operational path answered on a public address (00 §9.1)
smoke-environment: FAILED (env=dev, 0 skipped assertion(s))   # exit 1
```

Root cause: the Worker's minimal health listener hard-coded `IPAddress.Any`, so on a developer machine
`:8081/internal/health/*` answered on the LAN address, while the Api binds loopback because
`ASPNETCORE_URLS` is environment configuration. The owner fixed it with the same shape as the Api: a
configured bind address, `EVIDGATE__WORKER__HEALTHADDRESS` (default `0.0.0.0`, what a container probe
needs; `127.0.0.1` in dev), which this story declares per environment in the matrix and the three
environment files and re-verified with the green run above.

The `503` branch of AC2 ("as dependency state dictates"), with the compose database stopped for two
seconds and started again — liveness stays 200 throughout:

```console
attempt 1: api-live=200 api-ready=503 worker-ready=503
--- api readiness body ---
{"status":"unready","reason":"database_unreachable","journal_head":null,"expected_head":"0011_identity_membership.sql"}
--- worker readiness body ---
{"status":"unready","reason":"database_unreachable","journal_head":null,"expected_head":"0011_identity_membership.sql"}
recovered: api-ready=200
```

## 6. Open items

- `[OPEN]` Provider, region and account ids for staging and prod — 10.8.1 (O3), owner decision, needed
  before the first pilot data lands. Blocks 10.1.1-T3's final targets and 10.5.1-T1.
- `[OPEN]` Whether staging shares prod's object-storage account (plan §11 recommends separate
  accounts).
- `[OPEN]` Observability backend and the exact OTLP endpoints for staging and prod — 10.3.1-T2,
  licence-checked against `DECISIONS.md` §4.
- `[OPEN]` Staging replica counts and retention — sized by 10.6.1-T1/T3.
- `⚠️` The image's listen port and the probe's execution location (`10.2.1-T1`); the probe configuration
  here uses the loopback ports of the current hosts.

