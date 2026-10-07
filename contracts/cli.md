# contracts/cli.md — CLI contract registry

The `@evidgate/cli` (`evidgate`) contract: exit codes, configuration, token
sources, error codes and output rules. 00 §9.3 records the gate exit codes and
points here; the file is owned across the 04.2.x stories (04.2.1 §3/§5). Wire
formats stay in [openapi.yaml](openapi.yaml) and [schemas/](schemas/); this file
holds only the process-level contract.

## 1. Scope and ownership

- 04.2.1-T2 (M1) created the file: exit codes, configuration, token sources,
  error codes, output and redaction rules. This closes OI-04-06 in the
  [open-items register](../plans/open-items/02-catalog-ingest-runs-evidence.md).
- 04.2.1-T1 adds the auth-exchange section: `auth login`, the token cache
  location, and the `binding_pending` / `binding_conflict` rows.
- 04.2.2 adds `manifests upload` and `runs create`.
- 04.2.3 adds the spool format, its caps, `EVIDGATE_SPOOL_*` and
  `EVIDGATE_SEND_DEADLINE`, and `runs finalize`.
- 04.2.4 adds `artifacts upload` and `quickstart`.
- 04.9.1 adds `replay` and the replay exit codes 10–19 (04.9.1-T4).
- 07.6.1 adds `gate`; 07.7.1 adds the advisory exit rule; 07.8.1 adds
  `gate --release --target`.
- 03.5.1 records `evidgate init`.

## 2. Exit codes

Exit codes are the process exit status. Auth and configuration subcodes never
change the exit code; they appear only as the stderr `error.code` (§5).

| Code  | Name           | Meaning                                                       |
| ----- | -------------- | ------------------------------------------------------------- |
| 0     | `Ok`           | success; `gate` PASS                                          |
| 1     | `Fail`         | `gate` FAIL (07.6.1)                                          |
| 2     | `Inconclusive` | `gate` INCONCLUSIVE (07.6.1)                                  |
| 3     | `Error`        | tool, usage, configuration, transport or auth error           |
| 10–19 | reserved       | replay (04.9.1-T4), `[OPEN]` — not emitted by any command yet |

The replay range is reserved, not defined: 04.9.1-T4 assigns one code each to a
refused grant, an engine-pin mismatch, a timeout and a partial upload, and
records them here. Until then no command emits 10–19.

## 3. Configuration

`resolveConfig(input)` reads three sources. Precedence is explicit `--flag` >
`EVIDGATE_*` env > built-in default (AC5: env wins over flag defaults).

| Field                            | Env                                                               | Flag                                      | Required | Validation                                             |
| -------------------------------- | ----------------------------------------------------------------- | ----------------------------------------- | -------- | ------------------------------------------------------ |
| apiUrl                           | `EVIDGATE_API_URL`                                                | `--api-url`                               | yes      | absolute URL; see the rules below                      |
| project                          | `EVIDGATE_PROJECT`                                                | `--project`                               | yes      | trimmed non-empty, no whitespace or control characters |
| env                              | `EVIDGATE_ENV`                                                    | —                                         | no       | trimmed, lower-cased                                   |
| oidcToken                        | `EVIDGATE_OIDC_TOKEN`                                             | — (never a flag: argv is visible in `ps`) | no       |                                                        |
| actionsIdTokenRequestUrl / Token | `ACTIONS_ID_TOKEN_REQUEST_URL` / `ACTIONS_ID_TOKEN_REQUEST_TOKEN` | —                                         | no       | pair: both or neither counts as the source             |
| ingestToken                      | `EVIDGATE_INGEST_TOKEN`                                           | —                                         | no       |                                                        |
| devToken                         | `EVIDGATE_DEV_TOKEN`                                              | —                                         | no       |                                                        |
| bindingChallenge                 | `EVIDGATE_BINDING_CHALLENGE`                                      | —                                         | no       | read only; sent by 04.2.1-T1                           |
| verbose / json                   | —                                                                 | `--verbose` / `--json`                    | no       |                                                        |

- An empty-string env value counts as unset.
- Token values are never accepted from flags: argv is visible in `ps`.
- `EVIDGATE_API_URL` / `--api-url` must be an absolute URL. The scheme is
  `https:`, or `http:` only for loopback hosts (`localhost`, `127.0.0.1`,
  `[::1]`). No userinfo, no query and no fragment; a trailing `/` is normalised
  away. A violation is `invalid_config` and the message names the variable.
- Missing required configuration is a `CliError` with exit 3 and code
  `missing_config`; the message lists every missing name at once, for example
  `missing configuration: EVIDGATE_API_URL (or --api-url), EVIDGATE_PROJECT (or --project)`.
  When a token is required and no source is set, the list also carries
  `a token source: EVIDGATE_OIDC_TOKEN, ACTIONS_ID_TOKEN_REQUEST_URL + ACTIONS_ID_TOKEN_REQUEST_TOKEN, EVIDGATE_INGEST_TOKEN or EVIDGATE_DEV_TOKEN`.

## 4. Token sources and order

`getToken()` resolves the bearer in this order (04.2.1 §5): `EVIDGATE_OIDC_TOKEN`
→ the `ACTIONS_ID_TOKEN_REQUEST_URL` + `ACTIONS_ID_TOKEN_REQUEST_TOKEN` pair →
`EVIDGATE_INGEST_TOKEN` → `EVIDGATE_DEV_TOKEN`.

- The dev path is used only when no OIDC or ingest source is set (AC8). It
  returns `{ token, source: 'dev' }` as the bearer with no network call and no
  file write (no cache).
- `[DECISION]` The dev token is allowed only when `EVIDGATE_ENV` is exactly
  `dev` (after trim and lower-casing). `staging` or `prod` exits 3 with
  `dev_token_refused` (`EVIDGATE_DEV_TOKEN is refused when EVIDGATE_ENV=<env>`);
  unset or any other value exits 3 with `dev_token_refused` and the hint
  `set EVIDGATE_ENV=dev`. This is fail-closed and stricter than AC8's
  staging/prod minimum.
- An OIDC or ingest source present exits 3 with `exchange_unavailable`
  (`token exchange at POST /v1/ci/token ships in 04.2.1-T1`). This is a recorded
  hand-over: 04.2.1-T1 replaces the branch.
- No source at all is `missing_config` (§3).

## 5. Errors

`CliError { code: string; exitCode: ExitCode; message: string; hint?: string; requestId?: string }`.

stderr line format:

- `evidgate: error <code>: <message>`
- `evidgate: hint: <hint>` when a hint is present
- `evidgate: request_id: <id>` when the server returned one

| Code                   | Exit | Meaning                                                                                                           |
| ---------------------- | ---- | ----------------------------------------------------------------------------------------------------------------- |
| `usage`                | 3    | unknown command or flag, or a missing flag value; a one-line help pointer follows                                 |
| `missing_config`       | 3    | a required field or token source is unset; the message lists every missing name at once                           |
| `invalid_config`       | 3    | a value fails validation; the message names the variable                                                          |
| `dev_token_refused`    | 3    | `EVIDGATE_DEV_TOKEN` set while `EVIDGATE_ENV` is not exactly `dev`                                                |
| `exchange_unavailable` | 3    | an OIDC or ingest source is set but the exchange ships in 04.2.1-T1                                               |
| `internal_error`       | 3    | an unexpected exception; the message is redacted, and the stack is printed only under `--verbose` (also redacted) |

Server auth subcodes (`binding_pending`, `binding_conflict`, and the 401/403/409/429
codes of 04.2.1-T1) are echoed verbatim as `error.code` and never change the exit
code.

## 6. Output and redaction

- T78: the CLI never prints a bearer token. Token material exists only in the
  process environment, process memory, the `0600` cache file (04.2.1-T1) or
  `$GITHUB_ENV` behind `::add-mask::`.
- Every secret value read from env (`EVIDGATE_OIDC_TOKEN`,
  `ACTIONS_ID_TOKEN_REQUEST_TOKEN`, `EVIDGATE_INGEST_TOKEN`,
  `EVIDGATE_DEV_TOKEN`, `EVIDGATE_BINDING_CHALLENGE`) is registered with the
  redactor; every stderr and stdout write goes through a writer that replaces a
  registered value with `[REDACTED]`.
- `--verbose` prints the resolved configuration to stderr with secret fields
  shown as `[REDACTED]` (or `unset`).
- `config check` resolves the configuration (a token source is required) and
  runs `getToken()`, then prints
  `ok api_url=<u> project=<p> token_source=<dev|…>`; with `--json` it prints
  `{"ok":true,"api_url":…,"project":…,"token_source":…}`. It never prints a
  token. It exists so AC5 and AC8 are exercisable on the binary.
- `--help` / `-h` / `help` prints usage to stdout and exits 0; `--version`
  prints the package version and exits 0.
