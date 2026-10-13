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
- 04.2.2-T1 adds `manifests upload` and §8 (the manifest upload, its 256 KiB cap
  and the 401 purge); 04.2.2-T2 added `runs create` (§9).
- 04.2.3 adds the spool format, its caps, `EVIDGATE_SPOOL_*` and
  `EVIDGATE_SEND_DEADLINE`, and `runs finalize`.
- 04.2.4-T2 adds `quickstart` (§10); `artifacts upload` comes with 04.2.4-T1.
- 04.9.1 adds `replay` and the replay exit codes 10–19 (04.9.1-T4).
- 07.6.1 adds `gate`; 07.7.1 adds the advisory exit rule; 07.8.1 adds
  `gate --release --target`.
- 03.5.1 records `evidgate init`.

Command registry: a command is one `CommandDefinition` entry — `name`
(space-separated words, e.g. `config check`), `summary`, `requireToken`, an
optional `options` (node:util `parseArgs` option config, merged with the global
options for that command), an optional `positionals` usage string and an
optional `validateConfig(config)` pre-flight check. `main` calls
`validateConfig` right after `resolveConfig` and before the `--verbose` config
dump, so a command can refuse an unusable configuration without that dump
echoing the offending value (04.2.4-T2 `quickstart` project guard). The
dispatcher resolves the command by the longest registered name that prefixes the
positionals and passes the remaining positionals; `CommandContext` is
`{ config, io, env, values, positionals, deps, maskSecret }`. `deps` is the
transport/back-off seam `main` forwards from its optional 4th argument
(`ExchangeDeps`: `fetch`, `sleep`); `maskSecret` writes a raw
`::add-mask::<value>` line to stdout, only for a registered secret without a
CR/LF (the redacting writer would otherwise turn the mask into `[REDACTED]`).
Adding a command is one registry entry, never a dispatcher edit. Help column
width is computed from the longest command name + 2.

## 2. Exit codes

Exit codes are the process exit status. Auth and configuration subcodes never
change the exit code; they appear only as the stderr `error.code` (§5).
`CliError` always exits `Error` (3) — it has no exit-code override, so a subcode
can never change the exit code. `CommandDefinition.run` and `main` return
`Promise<ExitCode>`, so a command cannot return an unregistered code.

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

| Field                            | Env                                                               | Flag                                      | Required | Validation                                                                |
| -------------------------------- | ----------------------------------------------------------------- | ----------------------------------------- | -------- | ------------------------------------------------------------------------- |
| apiUrl                           | `EVIDGATE_API_URL`                                                | `--api-url`                               | yes      | absolute URL; no query or fragment; see the rules below                   |
| project                          | `EVIDGATE_PROJECT`                                                | `--project`                               | yes      | trimmed non-empty; no whitespace, no Unicode control or format characters |
| env                              | `EVIDGATE_ENV`                                                    | —                                         | no       | trimmed, lower-cased; see the rules below                                 |
| oidcToken                        | `EVIDGATE_OIDC_TOKEN`                                             | — (never a flag: argv is visible in `ps`) | no       |                                                                           |
| actionsIdTokenRequestUrl / Token | `ACTIONS_ID_TOKEN_REQUEST_URL` / `ACTIONS_ID_TOKEN_REQUEST_TOKEN` | —                                         | no       | pair: both or neither counts as the source                                |
| ingestToken                      | `EVIDGATE_INGEST_TOKEN`                                           | —                                         | no       |                                                                           |
| devToken                         | `EVIDGATE_DEV_TOKEN`                                              | —                                         | no       |                                                                           |
| bindingChallenge                 | `EVIDGATE_BINDING_CHALLENGE`                                      | —                                         | no       | read only; sent by 04.2.1-T1                                              |
| cacheDir                         | `EVIDGATE_CACHE_DIR`                                              | —                                         | no       | absolute path; see the rules below                                        |
| verbose / json                   | —                                                                 | `--verbose` / `--json`                    | no       |                                                                           |

- An empty-string env value counts as unset.
- Token values are never accepted from flags: argv is visible in `ps`.
- `EVIDGATE_ENV`, after trim and lower-casing, must match
  `^[a-z0-9][a-z0-9._-]{0,31}$`. Otherwise exit 3 `invalid_config` with the
  message `invalid configuration: EVIDGATE_ENV: has an invalid value`; the
  value is never echoed.
- `EVIDGATE_PROJECT` / `--project` rejects any Unicode control (`\p{Cc}`) or
  format (`\p{Cf}`) character and any whitespace. The message is
  `invalid configuration: EVIDGATE_PROJECT (or --project): must not contain whitespace or control characters`
  and does not echo the value.
- `EVIDGATE_API_URL` / `--api-url` must be an absolute URL. The scheme is
  `https:`, or `http:` only for loopback hosts (`localhost`, `127.0.0.1`,
  `[::1]`). No userinfo. Every rejection exits 3 `invalid_config` with the
  message `invalid configuration: EVIDGATE_API_URL (or --api-url): <reason>`,
  where `<reason>` is one of: `must not contain a query or fragment` (any `?` or
  `#` in the raw value), `is not an absolute URL` (an unparseable value),
  `must not contain userinfo`, `http: is only allowed for loopback hosts
(localhost, 127.0.0.1, [::1])`, or `unsupported scheme '<scheme>' (expected
https:)`. The value is never echoed. The normalised value is
  `url.origin + url.pathname` with a trailing `/` removed.
- `EVIDGATE_API_URL` / `--api-url` is the API root **without** the version
  prefix. The openapi document declares `servers: - url: /v1`, so the CLI appends
  `/v1` to every API call (`POST /v1/ci/token`, `POST /v1/projects/{p}/manifests`).
  A value that already ends in `/v1` therefore yields `/v1/v1`; this is not
  validated against (there is no rule forbidding it), so pass the root.
- `EVIDGATE_CACHE_DIR` is used as-is; when unset the default is
  `%LOCALAPPDATA%\evidgate` on Windows, else `$XDG_CACHE_HOME/evidgate`, else
  `<home>/.cache/evidgate`. A relative `EVIDGATE_CACHE_DIR` is exit 3
  `invalid_config` naming the variable; the value is never echoed. A relative
  `XDG_CACHE_HOME` or `LOCALAPPDATA` is ignored and the next default is used
  (XDG Base Directory spec), never an error.
- `ACTIONS_ID_TOKEN_REQUEST_URL` is a secret: it carries a signed query, so it is
  registered with the redactor and shown as `[REDACTED]` (or `unset`) in
  `--verbose` (§6).
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
- `[DECISION]` The dev token is returned only when `EVIDGATE_ENV` is exactly
  `dev` (after trim and lower-casing) **and** the API URL host is loopback
  (`localhost`, `127.0.0.1`, `[::1]`). The reason: the dev token is a real
  ingest token and the env label is self-declared, so the target must also be
  local. The env check runs first: `staging` or `prod` exits 3 with
  `dev_token_refused` (`EVIDGATE_DEV_TOKEN is refused when EVIDGATE_ENV=<env>`);
  unset or any other value exits 3 with `dev_token_refused` and the hint
  `set EVIDGATE_ENV=dev`. A non-loopback host exits 3 with `dev_token_refused`
  and the message `EVIDGATE_DEV_TOKEN is only sent to a loopback API URL` plus
  the hint `point EVIDGATE_API_URL at the local dev stack (http://127.0.0.1:8080)`.
  This is fail-closed and stricter than AC8's staging/prod minimum.
- An OIDC, actions-id-token or ingest source is exchanged at `POST /v1/ci/token`
  (04.2.1-T1, §7). Request shape: a GitHub OIDC JWT goes as `Authorization: Bearer
<jwt>`; an ingest token goes in the body as `{"ingest_token":"egt_…"}` (a body
  ingest token wins over any bearer); the `actions-id-token` source first fetches
  `ACTIONS_ID_TOKEN_REQUEST_URL` with `ACTIONS_ID_TOKEN_REQUEST_TOKEN` as bearer and
  `?audience=evidgate`, then sends the returned `value` as the bearer. That URL
  must be `https:` (or `http:` on a loopback host) with no userinfo and no
  fragment; otherwise exit 3 `invalid_config` naming the variable, never echoing
  the value. When `EVIDGATE_BINDING_CHALLENGE` is set it is forwarded as
  `binding_challenge` (T70). The project is not sent: the server scopes the token
  to the credential's project (00 §9.1). The reply is
  `{token, expires_at, project_id, permission_set}`; a 2xx body without a
  non-empty `token`, an RFC 3339 `expires_at` and a UUID `project_id` is exit 3
  `exchange_failed` (the body is never echoed).
- No source at all is `missing_config` (§3).

## 5. Errors

`CliError { code: string; message: string; hint?: string; requestId?: string }`.
It always exits 3 (`ExitCode.Error`); the constructor takes no exit-code
override.

Every line the CLI writes to stderr starts with `evidgate: `, so no line can
start with `::` (a GitHub workflow command). Multi-line messages and `--verbose`
stacks are split and each line is prefixed. Values interpolated into an error
message (unknown command text, `parseArgs` messages, `EVIDGATE_ENV` in
`dev_token_refused`) have control and format characters escaped as `\uXXXX`, and
newlines never pass through. The escaped set is `\p{Cc}`, `\p{Cf}`, `\p{Zl}` and
`\p{Zp}`, so U+2028 and U+2029 (line and paragraph separators) are escaped too.

stderr line format:

- `evidgate: error <code>: <message>`
- `evidgate: hint: <hint>` when a hint is present
- `evidgate: request_id: <id>` when the server returned one
- `evidgate: detail: <detail>` once per server `error.details[]` entry, after the
  hint/request_id lines

Server-supplied text fields — the envelope `message`, `request_id` (from the body
or the `X-Request-Id` header fallback) and each detail `path`/`reason` — are
processed in one fixed order: (1) `redact()`, so a registered secret straddling
the cut can never leave a clear-text prefix; (2) cap to the first 200 Unicode
code points, never splitting a surrogate pair or an escape; (3) escape control
characters as above.

A `detail:` line is `index=<n> path=<p> reason=<r>`: `index=` is printed only when
the entry's `index` is a number, `path=` only when its `path` is a string, and
`reason=` only when its `reason` is a string. A detail whose `reason` is missing
or not a string makes the whole envelope unusable, so the reply falls back to the
command's fallback code (`request_failed` for the project-scoped commands
`manifests upload` and `runs create`) with no `detail:` lines (fail closed). At
most 50 `detail:` lines are printed; more collapse into one final
`evidgate: detail: (<k> more)` line. `detail:` lines are printed only by
project-scoped commands (`manifests upload`, `runs create`); the token exchange
(`auth login`, `getToken`) never prints them.

The CLI prints the server's own `path`/`reason` text, after redaction, capping and
escaping. That no submitted value is echoed, and that a parameter is named by
position (`#n`) rather than by value, is the server contract (05.1.1, openapi
`createManifest`), not a CLI guarantee.

| Code                  | Exit | Meaning                                                                                                                                                                             |
| --------------------- | ---- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `usage`               | 3    | unknown command or flag, or a missing flag value; a one-line help pointer follows                                                                                                   |
| `write_failed`        | 3    | `quickstart --write` could not create or write the file (permissions, read-only filesystem, full disk, I/O); the message is fixed and never names the path                          |
| `missing_config`      | 3    | a required field or token source is unset; the message lists every missing name at once                                                                                             |
| `invalid_config`      | 3    | a value fails validation; the message names the variable and does not echo the value                                                                                                |
| `dev_token_refused`   | 3    | `EVIDGATE_DEV_TOKEN` set while `EVIDGATE_ENV` is not exactly `dev`, or the API URL host is not loopback                                                                             |
| `transport`           | 3    | the exchange request failed at the network layer, timed out (30 s) or was refused a redirect                                                                                        |
| `exchange_failed`     | 3    | a non-2xx reply without a usable error envelope, or a 2xx body that fails validation; the body is never echoed                                                                      |
| `oidc_request_failed` | 3    | the `ACTIONS_ID_TOKEN_REQUEST_URL` fetch failed (non-2xx, or a body without `value`)                                                                                                |
| `token_cache_error`   | 3    | the token cache could not be written or deleted; the message names the operation, never a token value                                                                               |
| `manifest_unreadable` | 3    | the manifest file could not be read (missing, unreadable, not a regular file or empty); the message names the errno code, never the path or the content                             |
| `manifest_too_large`  | 3    | the manifest file is larger than the server's 256 KiB transport cap; refused locally, before any request                                                                            |
| `request_failed`      | 3    | a non-2xx reply from a project-scoped command (`manifests upload`, `runs create`) without a usable error envelope, or a 2xx body that fails validation; the body is never echoed    |
| `binding_pending`     | 3    | 403: the repository has not presented the binding challenge yet; hint: set the repository variable `EVIDGATE_BINDING_CHALLENGE` to the challenge shown when the binding was created |
| `binding_conflict`    | 3    | 409: this repository is already bound to another organization; hint: its admin must disable that binding first                                                                      |
| `internal_error`      | 3    | an unexpected exception; the message is redacted, and the stack is printed only under `--verbose` (also redacted)                                                                   |

An uncaught exception, an unhandled rejection or a stdout/stderr `'error'`
(EPIPE) exits 3 `internal_error` — never 1. `bin.ts` routes its backstop through
the redacting io and writes one redacted `evidgate: error internal_error: …`
line (EPIPE on stdout exits 3 silently).

Any other server `error.code` that matches `^[a-z][a-z0-9_]{0,63}$` is echoed
verbatim as `error.code` (for example `token_invalid`, `token_replayed`,
`token_unknown`, `token_revoked`, `token_expired`, `binding_mismatch`,
`binding_disabled`, `rate_limited`, `unavailable`, `manifest_invalid`); a code that
does not match becomes the command's fallback code (`exchange_failed` for the
exchange, `request_failed` for the project-scoped commands). `request_id` comes
from the body `error.request_id`, else the `X-Request-Id` header. A 429 from the
token exchange
(`POST /v1/ci/token`) is retried once after `Retry-After` (clamped to 0–60 s, default
1 s); `manifests upload` does not retry (§8). 401/403/409 are never retried. No
server subcode changes the exit code.

- `Retry-After` is delta-seconds (digits only) or an HTTP-date; a missing header
  or any other value is 1 s.
- A `transport` error prints a fixed message and never echoes the request URL or
  a credential.
- A 401/403 purge failure never hides the server code: the cache delete is
  best-effort and the original `ExchangeAuthError` propagates.

## 6. Output and redaction

- T78: the CLI never prints a bearer token. Token material exists only in the
  process environment, process memory, the `0600` cache file (04.2.1-T1) or
  `$GITHUB_ENV` behind `::add-mask::`.
- Every secret value read from env (`EVIDGATE_OIDC_TOKEN`,
  `ACTIONS_ID_TOKEN_REQUEST_URL`, `ACTIONS_ID_TOKEN_REQUEST_TOKEN`,
  `EVIDGATE_INGEST_TOKEN`, `EVIDGATE_DEV_TOKEN`, `EVIDGATE_BINDING_CHALLENGE`)
  is registered with the redactor; every stderr and stdout write goes through a
  writer that replaces a registered value with `[REDACTED]`. `resolveConfig`
  registers them, and `main()` calls `resetSecrets()` then
  `registerEnvSecrets(env)` as its first statements, before any argument parsing.
- `registerSecret(value)` also registers the trimmed, `encodeURIComponent`-,
  JSON-string-escaped and control-character-escaped (`\uXXXX`, the form error
  messages print) forms of the value when they differ and are non-empty; a
  value shorter than 4 characters after trim is still registered (fail-safe).
  `redact()` replaces longest-first, so a shorter secret cannot leave the tail
  of a longer one in clear text. `getToken()` registers the token it returns and
  `createApiClient(config, token)` registers `token`.
- Consumer obligation: the registry is process-global and reset per `main()`.
  Any token minted at runtime must go through `registerSecret` before it can
  reach any writer, otherwise T78 rests on "never print it" alone. The
  `ci_producer` token is registered in `auth/exchange.ts` before it is validated
  or returned, and again when the cache is read (`auth/tokenCache.ts`).
- Consumer obligation: `createApiClient(config, token)` returns a standalone
  client. SDK operations must be called with that client explicitly (the SDK
  default client has no base URL and no `Authorization`), so 04.2.2/04.2.3 never
  call an operation bare. The client's wrapper `fetch` always sets
  `redirect: 'error'` on the outgoing Request regardless of per-call options:
  redirects are always refused.
- `--verbose` prints the resolved configuration to stderr with secret fields
  shown as `[REDACTED]` (or `unset`).
- `config check` resolves the configuration (a token source is required) and
  runs `getToken()`, then prints
  `ok api_url=<u> project=<p> token_source=<dev|…>`; with `--json` it prints
  `{"ok":true,"api_url":…,"project":…,"token_source":…}`. It never prints a
  token. For an OIDC/actions-id-token/ingest source it runs the exchange and
  fills the token cache (§7); the dev path stays offline. It takes no
  positionals; an extra positional is a `usage` error. It exists so AC5 and AC8
  are exercisable on the binary.
- `--help` / `-h` / `help` prints usage to stdout and exits 0; `--version`
  prints the package version and exits 0.

## 7. Auth exchange and token cache

The exchange and cache surface 04.2.1-T1 adds on top of §4.

Commands:

- `evidgate auth login [--json] [--github-env]` resolves the token source,
  exchanges it at `POST /v1/ci/token` (§4) and writes the `ci_producer` token to
  the cache. stdout prints `ok expires_at=<iso> project_id=<uuid>`; `--json`
  prints `{"ok":true,"expires_at":…,"project_id":…}`. The token is never printed
  (T78). With `--github-env` the token is appended to `$GITHUB_ENV` as an
  `EVIDGATE_CI_TOKEN` heredoc, after an `::add-mask::<token>` line on stdout;
  `--github-env` checks `$GITHUB_ENV` before any exchange, so an unset
  `$GITHUB_ENV` is exit 3 `missing_config` naming `GITHUB_ENV` with no network
  call. `[DECISION]` the variable name is `EVIDGATE_CI_TOKEN`. `[DECISION]` with
  `--json --github-env` the `::add-mask::` line is always the first stdout line,
  before the JSON line, so a consumer that parses the last line still reads the
  JSON.
- `evidgate auth logout` deletes the cache file for the resolved api_url +
  project. An absent file is not an error.

Cache:

- Location: `EVIDGATE_CACHE_DIR` used as-is, else `%LOCALAPPDATA%\evidgate` on
  Windows, else `$XDG_CACHE_HOME/evidgate`, else `<home>/.cache/evidgate` (§3).
- File name: `ci-token.<sha256 hex of api_url + "\n" + project>.json`.
  `[DECISION]` the `\n` separator keeps the two fields unambiguous.
- Mode `0600` in a `0700` directory; the directory is `chmod 0700` when it is
  owned by the user, and a directory owned by another uid is refused
  (`token_cache_error`) on write and ignored on read. The write is atomic — a
  `0600` temp file in the same directory is renamed over the target, which
  replaces a symlink rather than following it; the cache file is opened with
  `O_NOFOLLOW`. Content is exactly `{"token":…,"expires_at":…}`.
- A file that is not a regular file, is group/world-readable or foreign-owned
  (POSIX), or does not parse is treated as absent — never trusted.
- Reuse: a cached token is reused while `exp - now > 60 s`; within the margin it
  is re-exchanged and the cache rewritten (AC3). An entry whose expiry is more
  than 65 min ahead is treated as stale (`maxTokenLifetimeMs = 65 min`: server
  tokens live at most 60 min, +5 min skew).
- Purge: a 401/403 from `/v1/ci/token` deletes the cache entry before the error
  propagates (a delete failure is best-effort and never hides the server code);
  `auth logout` deletes it explicitly (04.2.1 §8 retention).
- `[DECISION]` a cache write or delete failure is exit 3 `token_cache_error`
  whose message names the operation and never a token value.
- `[note]` worst-case time to failure is bounded at ~150 s: 30 s for the
  `ACTIONS_ID_TOKEN_REQUEST_URL` fetch + 30 s for the exchange + up to 60 s
  `Retry-After` + 30 s for the single retry.
- `[DECISION]` concurrent invocations with a stale cache each exchange once; the
  atomic rename keeps the file consistent, so no lock is taken.
- `[DECISION]` the cache is keyed by api URL + project only, so jobs of the same
  OS user on the same runner share it for that project (the scope is identical).
- A project-scoped command purges the cache entry when the server answers 401
  (best-effort — a delete failure never hides the server code, which still
  surfaces), so the next `getToken()` re-exchanges; a 403 keeps the entry, because
  a scope refusal does not invalidate the token. First implemented by
  `manifests upload` (§8); `runs create` (04.2.2-T2, §9) applied the same rule.

## 8. Manifest upload

`evidgate manifests upload --file <path> [--json]` (04.2.2-T1) uploads one run
manifest to `POST /v1/projects/{p}/manifests` (openapi `createManifest`, 05.1.1
§5) and prints the stored `manifest_id` and its `manifest_hash`.

Order of operations (observable):

1. `--file` missing or empty is exit 3 `usage` (`missing required flag --file`),
   with no network call.
2. The file is read and capped BEFORE `getToken()`, so an unreadable or oversize
   file makes no request at all — not even the token exchange. The file is opened
   and `fstat`ed; a non-regular file, an open/read failure (the message names the
   errno code) and an empty file are exit 3 `manifest_unreadable`. At most
   `maxManifestBytes + 1` bytes are read — the size is never trusted from `stat`
   alone — and more than `maxManifestBytes` is exit 3 `manifest_too_large`
   (`the manifest file exceeds 262144 bytes`).
3. `getToken()` (04.2.1) supplies the bearer, `createApiClient` builds the
   redirect-refusing client, and the generated `createManifest` operation is
   called with that client explicitly (§6). The body is the RAW file bytes — no
   JSON re-serialisation, because the server hashes the RFC 8785 canonical form
   and a round trip could lose number precision — sent as `application/json`
   with a 30 s timeout (`AbortSignal.timeout(30_000)`) and no retry. The
   `Idempotency-Key` the route requires (00 §9.1) is the SHA-256 hex of those
   same bytes, so a retry of one manifest replays the stored 201 instead of
   writing a second record.
4. A 2xx body must be `{ manifest_id: <uuid>, manifest_hash: sha256:<64 hex> }`;
   anything else is exit 3 `request_failed` and the body is never echoed.
5. A non-2xx reply maps through §5: the server's `error.code` when it is a safe
   identifier (else `request_failed`), the server's message redacted, capped at
   200 code points and escaped, `request_id` from the body else the
   `X-Request-Id` header (same treatment), and one `detail:` line per
   `error.details[]` entry — so a 422 `manifest_invalid` passes its per-item
   causes through. A non-2xx reply without a usable envelope is `request_failed`
   with `the manifest upload failed (HTTP <status>)`.
6. A 401 purges the cached CI token before the error propagates (§7); a 403 does
   not. A transport failure, a timeout or a refused redirect is exit 3
   `transport` with a fixed message.

Output:

- text: `manifest_id=<id> manifest_hash=<hash>` and nothing else on stdout.
- `--json`: exactly one line `{"manifest_id":"…","manifest_hash":"…"}`.

The cap is the server's documented transport limit: openapi `createManifest`
("Body ≤ 256 KiB → 413") and 05.1.1 §8 (`MaxManifestBytes = 256 * 1024`). The
CLI refuses a larger file locally, so the 413 is never reached. The manifest
content is never echoed, not even under `--verbose`.

## 9. Run create

`evidgate runs create --manifest-id <uuid> --manifest-hash <sha256:64hex>
[--release <name>] [--ci-ref <ref>] [--idempotency-key <k>] [--json]`
(04.2.2-T2) creates one run for an already-uploaded manifest at
`POST /v1/projects/{p}/runs` (openapi `createRun`, 00 §9.3, 05.2.1) and prints the
`run_id`, the manifest hash and the returned state.

| Flag                             | Required | Sent as                                                    | Validation                         |
| -------------------------------- | -------- | ---------------------------------------------------------- | ---------------------------------- |
| `--manifest-id <uuid>`           | yes      | body `manifest_id`                                         | a UUID                             |
| `--manifest-hash <sha256:64hex>` | yes      | not sent — the derived key and `manifest_hash` output only | `^sha256:[0-9a-f]{64}$`            |
| `--release <name>`               | no       | body `release`                                             | trimmed, 1..128 characters         |
| `--ci-ref <ref>`                 | no       | body `ref`                                                 | a full git ref (below)             |
| `--idempotency-key <k>`          | no       | header `Idempotency-Key`                                   | 1..128 printable ASCII (0x21–0x7E) |

The route requires an `Idempotency-Key` header (00 §9.1): the CLI sends its own
or a derived one (below), so a retried step replays the stored 201 instead of
creating a second run. `--policy`, `--suite` and `--env` are not defined flags:
the server pins the suite and environment from the manifest and the policy from
the project's active revision (`[DECISION]` D-S47-Q1, below).

Order of operations (observable):

1. Every flag is validated BEFORE `getToken()`, so a bad flag makes no request
   at all — not even the token exchange:
   - `--manifest-id` missing or not a UUID is exit 3 `usage`.
   - `--manifest-hash` missing or not `^sha256:[0-9a-f]{64}$` is exit 3 `usage`.
   - `--release`, after trim, must be 1..128 characters, else exit 3 `usage`; a
     name the server cannot resolve is 422 `release_unresolvable`, mapped as in
     step 4.
   - `--ci-ref` must be a full git ref (below), else exit 3 `usage` with the
     one-line help pointer.
   - `--idempotency-key` must be 1..128 printable ASCII characters (0x21–0x7E,
     no whitespace or control), else exit 3 `usage`.
2. `getToken()` (04.2.1) supplies the bearer and `createApiClient` builds the
   redirect-refusing client; the generated `createRun` operation is called with
   that client explicitly (§6). The request carries `{p: <project>}`, the
   `Idempotency-Key` header and the body `{manifest_id, release?, ref?}` —
   absent members are omitted and `manifest_hash`, `purpose`, `components` and
   `replay_grant_id` are never sent. `--manifest-hash` feeds only the derived
   key and the output. The call has a 30 s timeout
   (`AbortSignal.timeout(30_000)`) and no retry.
3. A 201 body must be `{ run_id: <uuid>, state: <non-empty string> }`; anything
   else is exit 3 `request_failed` and the body is never echoed.
4. A non-2xx reply maps through §5: the server's `error.code` when it is a safe
   identifier (else `request_failed`), its message redacted, capped at 200 code
   points and escaped, `request_id` from the body else the `X-Request-Id`
   header, and one `detail:` line per `error.details[]` entry. A 409
   `idempotency_conflict` — the same key with a different body — is therefore
   `evidgate: error idempotency_conflict` and exits 3, and a 404 or 422
   (`manifest_invalid`, `release_unresolvable`, `execution_key_conflict`,
   `request_invalid`, …) passes its server code through. A non-2xx reply
   without a usable envelope is exit 3 `request_failed` with `the run create
failed (HTTP <status>)`.
5. A 401 purges the cached CI token before the error propagates (§7); a 403
   does not. A transport failure, a timeout or a refused redirect is exit 3
   `transport` with a fixed message.

Identity (`--ci-ref`): it is the only identity input — the CLI never reads
`GITHUB_*` or any other CI environment variable — and one full git ref maps to
the request's `ref` member:

- `refs/heads/<name>` → `{kind: "branch", name: "<name>"}`.
- `refs/tags/<name>` → `{kind: "tag", name: "<name>"}`.
- `refs/pull/<N>/merge` with a positive integer `N` →
  `{kind: "pull_request", name: "<N>/merge", pr_number: <N>}`.
- `<name>` must be 1..255 characters. Any other value — unprefixed, empty,
  another `refs/` namespace or a malformed PR ref — is exit 3 `usage` with the
  help pointer, before any token work.

A `ref` in the body marks the run `unverified_ref=true` server side; a verified
CI caller's own claim wins there (05.2.2-T1), so `--ci-ref` never overrides a
verified identity.

Derived key (AC4): when `--idempotency-key` is omitted the CLI derives the key
as the lowercase SHA-256 hex of the UTF-8 JSON encoding of this five-element
array, in this fixed order:

`["evidgate.run-key.v1", <manifestHash>, <manifestId>, <release ?? null>, <ciRef ?? null>]`

- an absent `--release` or `--ci-ref` is JSON `null` — never omitted and never
  an empty string; `release` is the trimmed flag value and `ciRef` is the raw
  validated `--ci-ref` string;
- the result is 64 hex characters, well inside the ≤ 128 cap;
- a JSON array is unambiguous — no separator can collide with a value — and
  every value the CLI sends in the body feeds the key, so a different body
  never reuses a key; the key is stable across invocations and process
  restarts;
- `deriveRunIdempotencyKey(input)` is exported from the command module, so a
  unit test pins the format.

Output:

- text: `run_id=<id> manifest_hash=<hash> state=<state>` and nothing else on
  stdout.
- `--json`: exactly one line `{"run_id":"…","manifest_hash":"…","state":"…"}`,
  in that key order.
- `manifest_hash` is the validated `--manifest-hash` flag value. The
  `Idempotency-Key`, the bearer token and any server body outside the mapped
  error are never printed, and the command adds no output of its own under
  `--verbose`.

Exit codes: `0` on success; `3` for a usage, configuration, transport, auth or
server error. The command emits no other code.

`[DECISION]` (D-S47-Q1, coordinator, 2026-10-14) `--manifest-id` is required in
addition to `--manifest-hash`: the server resolves the manifest by id, and the
hash is only the derived key's input and an echoed value.

`[DECISION]` (D-S47-Q1) the flag set is exactly the table above; the
`--policy`, `--suite` and `--env` flags and a `--manifest-hash` request member
are dropped, because `CreateRunRequest` has no such member
(`additionalProperties: false`) and the server pins the suite and environment
from the manifest and the policy from the project's active revision (07.1.1-T4).

`[DECISION]` (D-S47-Q1) the derived key is the SHA-256 of the JSON array above,
not of a concatenated string.

## 10. Quickstart

`evidgate quickstart [--format github|generic] [--write <path>]` (04.2.4-T2)
renders a CI recipe for the project to stdout, or writes it to a file. It is a
local template command: no network call, no token read, no cache read. It still
resolves configuration like every other command (§3), so `EVIDGATE_API_URL` (or
`--api-url`) and `EVIDGATE_PROJECT` (or `--project`) are required; a missing one
fails with the usual configuration error.

Options:

- `--format <github|generic>` — default `github`; any other value exits 3
  `usage` with `--format must be github or generic`.
- `--write <path>` — write the rendered template to `<path>` instead of stdout.

Project guard: the resolved project must match
`^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`. A value that does not match exits 3
`invalid_config` with `invalid configuration: EVIDGATE_PROJECT (or --project):
not renderable in a CI template`. The guard runs as the command's
`validateConfig` pre-flight check (§1), before `main`'s `--verbose` config dump,
so the value is never echoed — not even under `--verbose` — and a project name
carrying YAML, a GitHub `${{ }}` expression or shell metacharacters cannot reach
the rendered file.

Pinned version: both templates pin the exact `version` from
`packages/cli/package.json` (`@evidgate/cli@<version>`), never a range and never
`latest`. `readCliVersion()` is shared between `main.ts` and the command, so the
pin follows the package automatically.

GitHub template (`--format github`, YAML):

- top-level `permissions: {}` — the workflow grants nothing; each job elevates
  for itself (11.6.1-T2, the fork-safe shape of
  `fixtures/ci-recipes/github-fork-safe.yml`);
- `on: push` (branches `main`), `pull_request` and `workflow_dispatch`;
- two jobs on `ubuntu-24.04`: a credential-free `test` job on
  `pull_request`/`push` with `permissions: { contents: read }`, and an `upload`
  job that runs only on `workflow_dispatch` or a push to the default branch,
  inside the environment `evidgate-upload`, with
  `permissions: { contents: read, id-token: write }`; the `upload` job carries
  `env` `EVIDGATE_API_URL: ${{ vars.EVIDGATE_API_URL }}` — a repository
  configuration **variable**, not a secret, and the only stored setting — and
  `EVIDGATE_PROJECT: "<project>"` rendered literal;
- steps: `actions/checkout` (with `persist-credentials: false`) and
  `actions/setup-node` pinned by commit SHA (the same SHAs as the repo
  `ci.yml`), `node-version: "22"`, then
  `npx --yes --package @evidgate/cli@<version> -- evidgate auth login` (OIDC via
  `ACTIONS_ID_TOKEN_REQUEST_*`, exchanged at `POST /v1/ci/token`; the token goes
  only to the 0600 cache, never printed),
  `npx … -- evidgate manifests upload --file evidgate-manifest.json --json`
  (its `manifest_id`/`manifest_hash` are captured into `$MANIFEST_ID` and
  `$MANIFEST_HASH`) and `npx … -- evidgate runs create --manifest-id
"$MANIFEST_ID" --manifest-hash "$MANIFEST_HASH" --ci-ref "$GITHUB_REF"`
  (04.2.2-T2, §9);
- steps not yet built are comments naming the owning task, never fake commands:
  sending events and `runs finalize` (04.2.3-T1/T2), and `artifacts upload`
  (04.2.4-T1). `runs create` (04.2.2-T2) is a real step since §9 landed.

No `secrets.` reference and no token or key value appears anywhere in the
template.

Generic template (`--format generic`, POSIX `sh`): `#!/bin/sh` with `set -eu`.
[DECISION] Generic CI renders a shell script, not YAML: V1 CI integration is
GitHub Actions, and other CIs are per-project configuration (00 §1, 2026-10-02).
The script expects `EVIDGATE_API_URL` from the CI's non-secret variables, guards
it with `: "${EVIDGATE_API_URL:?…}"` and then `export`s it, so the pinned `npx`
steps inherit it, and renders `EVIDGATE_PROJECT='<project>'`. The credential is
named only:
`EVIDGATE_OIDC_TOKEN` when the CI injects a short-lived OIDC JWT the project
binding accepts, else `EVIDGATE_INGEST_TOKEN` injected by the CI's masked secret
store — a project-scoped, revocable, rotatable ingest token created with an
expiry (02.4.2), never committed and never echoed. It then runs the same pinned
`npx` lines for `auth login`, `manifests upload` and `runs create` — the last
without `--ci-ref`, which needs the GitHub ref; a comment shows the form for a
CI that has one — with the same commented placeholders for events/finalize and
`artifacts upload` as the GitHub template.

`--write <path>`:

- containment is decided only by comparing `realpath` of the nearest existing
  parent of the target against `realpath(cwd)`: the target must be that
  directory or sit under it, decided with `path.relative` so the check is also
  correct when the cwd is `/`. A `..` escape or a symlinked parent that points
  outside exits 3 `usage`; an absolute path that reaches the cwd through a
  symlink alias (macOS `/tmp` → `/private/tmp`) is accepted;
- missing parent directories inside cwd are created (`mkdir -p`);
- the file is created at the realpath'd target with flag `wx` (exclusive — an
  existing file exits 3 `usage` `file exists`) and mode `0o644`;
- every filesystem failure is mapped to a fixed message that never carries the
  Node error text or an absolute path: a path-shape errno (`ENOENT`, `ENOTDIR`,
  `EISDIR`, `EEXIST`) exits 3 `usage` (`cannot write --write path: not a
directory`, `cannot write --write path: no such directory`, `cannot write
--write path: is a directory`, `file exists`); any other failure (`EACCES`,
  `EPERM`, `EROFS`, `ENOSPC`, `EIO`, …) exits 3 `write_failed` (`cannot write
--write path`);
- a write that fails after the exclusive create closes the handle and removes
  the file it created, so a retry does not hit `file exists`;
- after the file is created its `realpath` is re-checked against `realpath(cwd)`
  and removed with `usage` `--write path escapes the working directory` if it
  escaped. Residual race: a local actor with write access inside the cwd could
  swap a parent directory for a symlink between the containment check and the
  create; the post-create re-check is what catches it;
- stdout prints `wrote <relative path>` (relative to `realpath(cwd)`), with
  control and format characters escaped as `\uXXXX` (§5) so a filename cannot
  forge a log line.

Without `--write`, the template goes to stdout and nothing else is printed.

Exit codes: `0` on success; `3` for a usage error (`--format`, `--write`), an
invalid configuration (the project guard) or any other error. The command emits
no other code.
