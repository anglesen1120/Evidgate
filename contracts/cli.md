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
  and the 401 purge); 04.2.2-T2 adds `runs create`.
- 04.2.3 adds the spool format, its caps, `EVIDGATE_SPOOL_*` and
  `EVIDGATE_SEND_DEADLINE`, and `runs finalize`.
- 04.2.4 adds `artifacts upload` and `quickstart`.
- 04.9.1 adds `replay` and the replay exit codes 10–19 (04.9.1-T4).
- 07.6.1 adds `gate`; 07.7.1 adds the advisory exit rule; 07.8.1 adds
  `gate --release --target`.
- 03.5.1 records `evidgate init`.

Command registry: a command is one `CommandDefinition` entry — `name`
(space-separated words, e.g. `config check`), `summary`, `requireToken`, an
optional `options` (node:util `parseArgs` option config, merged with the global
options for that command) and an optional `positionals` usage string. The
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
command's fallback code (`request_failed` for `manifests upload`) with no
`detail:` lines (fail closed). At most 50 `detail:` lines are printed; more
collapse into one final `evidgate: detail: (<k> more)` line. `detail:` lines are
printed only by project-scoped commands (`manifests upload`); the token exchange
(`auth login`, `getToken`) never prints them.

The CLI prints the server's own `path`/`reason` text, after redaction, capping and
escaping. That no submitted value is echoed, and that a parameter is named by
position (`#n`) rather than by value, is the server contract (05.1.1, openapi
`createManifest`), not a CLI guarantee.

| Code                  | Exit | Meaning                                                                                                                                                                             |
| --------------------- | ---- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `usage`               | 3    | unknown command or flag, or a missing flag value; a one-line help pointer follows                                                                                                   |
| `missing_config`      | 3    | a required field or token source is unset; the message lists every missing name at once                                                                                             |
| `invalid_config`      | 3    | a value fails validation; the message names the variable and does not echo the value                                                                                                |
| `dev_token_refused`   | 3    | `EVIDGATE_DEV_TOKEN` set while `EVIDGATE_ENV` is not exactly `dev`, or the API URL host is not loopback                                                                             |
| `transport`           | 3    | the exchange request failed at the network layer, timed out (30 s) or was refused a redirect                                                                                        |
| `exchange_failed`     | 3    | a non-2xx reply without a usable error envelope, or a 2xx body that fails validation; the body is never echoed                                                                      |
| `oidc_request_failed` | 3    | the `ACTIONS_ID_TOKEN_REQUEST_URL` fetch failed (non-2xx, or a body without `value`)                                                                                                |
| `token_cache_error`   | 3    | the token cache could not be written or deleted; the message names the operation, never a token value                                                                               |
| `manifest_unreadable` | 3    | the manifest file could not be read (missing, unreadable, not a regular file or empty); the message names the errno code, never the path or the content                             |
| `manifest_too_large`  | 3    | the manifest file is larger than the server's 256 KiB transport cap; refused locally, before any request                                                                            |
| `request_failed`      | 3    | a non-2xx manifest upload without a usable error envelope, or a 2xx body that fails validation; the body is never echoed                                                            |
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
exchange, `request_failed` for `manifests upload`). `request_id` comes from the body
`error.request_id`, else the `X-Request-Id` header. A 429 from the token exchange
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
  `manifests upload` (§8); `runs create` (04.2.2-T2) applies the same rule.

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
