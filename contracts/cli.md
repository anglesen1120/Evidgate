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
  and the 401 purge); 04.2.2-T2 adds `runs create` (§9).
- 04.2.3 adds §11: the spool format, its caps, `EVIDGATE_SPOOL_DIR` /
  `EVIDGATE_SPOOL_MAX_BYTES` / `EVIDGATE_SEND_DEADLINE`, the spool error family,
  and the `events send` and `runs finalize` commands.
- 04.2.4-T2 adds `quickstart` (§10); 04.2.4-T1 adds `artifacts upload` (§12) with
  its journal, its allowed-directory guard and the presigned-URL redaction rule.
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

| Code  | Name           | Meaning                                                             |
| ----- | -------------- | ------------------------------------------------------------------- |
| 0     | `Ok`           | success; `gate` PASS                                                |
| 1     | `Fail`         | `gate` FAIL (07.6.1)                                                |
| 2     | `Inconclusive` | `gate` INCONCLUSIVE, or still `pending` when `--wait` ends (07.6.1) |
| 3     | `Error`        | tool, usage, configuration, transport or auth error                 |
| 10–19 | reserved       | replay (04.9.1-T4), `[OPEN]` — not emitted by any command yet       |

`gate` exits 2 only for `INCONCLUSIVE` or a run still `pending`: at the
deadline, or when the deadline itself cut the final request off before it
answered. A final poll the server answers `5xx`/`429` past the bounded retries,
or whose decision body breaks or is aborted after its headers, is exit 3 even
when an earlier poll answered `pending`; no such path may exit 0.

The replay range is reserved, not defined: 04.9.1-T4 assigns one code each to a
refused grant, an engine-pin mismatch, a timeout and a partial upload, and
records them here. Until then no command emits 10–19.

## 3. Configuration

`resolveConfig(input)` reads three sources. Precedence is explicit `--flag` >
`EVIDGATE_*` env > built-in default (AC5: env wins over flag defaults).

| Field                            | Env                                                               | Flag                                      | Required | Validation                                                                  |
| -------------------------------- | ----------------------------------------------------------------- | ----------------------------------------- | -------- | --------------------------------------------------------------------------- |
| apiUrl                           | `EVIDGATE_API_URL`                                                | `--api-url`                               | yes      | absolute URL; no query or fragment; see the rules below                     |
| project                          | `EVIDGATE_PROJECT`                                                | `--project`                               | yes      | trimmed non-empty; no whitespace, no Unicode control or format characters   |
| env                              | `EVIDGATE_ENV`                                                    | —                                         | no       | trimmed, lower-cased; see the rules below                                   |
| oidcToken                        | `EVIDGATE_OIDC_TOKEN`                                             | — (never a flag: argv is visible in `ps`) | no       |                                                                             |
| actionsIdTokenRequestUrl / Token | `ACTIONS_ID_TOKEN_REQUEST_URL` / `ACTIONS_ID_TOKEN_REQUEST_TOKEN` | —                                         | no       | pair: both or neither counts as the source                                  |
| ingestToken                      | `EVIDGATE_INGEST_TOKEN`                                           | —                                         | no       |                                                                             |
| devToken                         | `EVIDGATE_DEV_TOKEN`                                              | —                                         | no       |                                                                             |
| bindingChallenge                 | `EVIDGATE_BINDING_CHALLENGE`                                      | —                                         | no       | read only; sent by 04.2.1-T1                                                |
| cacheDir                         | `EVIDGATE_CACHE_DIR`                                              | —                                         | no       | absolute path; see the rules below                                          |
| verbose / json                   | —                                                                 | `--verbose` / `--json`                    | no       |                                                                             |
| spoolDir                         | `EVIDGATE_SPOOL_DIR`                                              | —                                         | §11 only | trimmed, absolute-resolved; a control character is `invalid_config`         |
| spoolMaxBytes                    | `EVIDGATE_SPOOL_MAX_BYTES`                                        | —                                         | no       | positive decimal bytes ≤ 1 TiB; default 268435456 (256 MiB)                 |
| sendDeadlineMs                   | `EVIDGATE_SEND_DEADLINE`                                          | —                                         | no       | `<n>` seconds or `<n>s` / `<n>m` / `<n>h`; default 1800000 ms, ceiling 24 h |
| uploadDeadlineMs                 | `EVIDGATE_UPLOAD_DEADLINE`                                        | —                                         | §12 only | `<n>` seconds or `<n>s` / `<n>m` / `<n>h`; default 1800000 ms, ceiling 24 h |

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
- `EVIDGATE_SPOOL_DIR` is required by the spool commands only (`events send`,
  `runs finalize` — §11), not by `resolveConfig`: it is `undefined` when unset,
  so every other command runs without it. A value is trimmed and
  absolute-resolved (a relative one is anchored at the process's current
  directory, the rule the reporter applies); a control or format character is
  exit 3 `invalid_config` naming the variable. The two spool commands refuse an
  unset value in their `validateConfig` pre-flight with exit 3 `missing_config`
  `missing configuration: EVIDGATE_SPOOL_DIR`, before the token work and before
  the `--verbose` dump (§1).
- `EVIDGATE_SPOOL_MAX_BYTES` caps one run's spool directory (§11). A positive
  decimal byte count, default `268435456` (256 MiB), at most `1099511627776`
  (1 TiB); zero, a fraction, a unit suffix, junk and a larger value are exit 3
  `invalid_config` naming the variable, never echoing the value.
- `EVIDGATE_SEND_DEADLINE` is one deadline for a whole `events send`, as a bare
  integer number of seconds or `<n>s`, `<n>m`, `<n>h`; default `1800000`
  (30 minutes), ceiling `86400000` (24 hours). Zero, a fraction, `1d` and a
  longer deadline are exit 3 `invalid_config` naming the variable.
- `EVIDGATE_UPLOAD_DEADLINE` is the same grammar, default and ceiling for one
  `artifacts upload` (§12.3): `1800000` ms unless set, at most `86400000` ms, and
  zero, a fraction, an unknown suffix or a longer value are exit 3
  `invalid_config` naming the variable, never echoing the value. It is resolved
  lazily too, by `artifacts upload` only (`resolveUploadDeadlineMs`, §12.3), so a
  malformed value never breaks another command.
- All three spool values are resolved lazily, by `events send` and `runs
finalize` only (`resolveSpoolSettings`, memoised on first use): a malformed
  `EVIDGATE_SPOOL_MAX_BYTES` or `EVIDGATE_SEND_DEADLINE` is `invalid_config` for
  those two commands and never breaks `runs create`, `auth login` or any other
  command that does not touch the spool. `EVIDGATE_UPLOAD_DEADLINE` is not one of
  them: it is `artifacts upload`'s own lazily resolved value (the bullet above),
  read by no other command.
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

| Code                       | Exit | Meaning                                                                                                                                                                                                                                                                                 |
| -------------------------- | ---- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `usage`                    | 3    | unknown command or flag, or a missing flag value; a one-line help pointer follows                                                                                                                                                                                                       |
| `write_failed`             | 3    | `quickstart --write` could not create or write the file (permissions, read-only filesystem, full disk, I/O); the message is fixed and never names the path                                                                                                                              |
| `missing_config`           | 3    | a required field or token source is unset; the message lists every missing name at once                                                                                                                                                                                                 |
| `invalid_config`           | 3    | a value fails validation; the message names the variable and does not echo the value                                                                                                                                                                                                    |
| `dev_token_refused`        | 3    | `EVIDGATE_DEV_TOKEN` set while `EVIDGATE_ENV` is not exactly `dev`, or the API URL host is not loopback                                                                                                                                                                                 |
| `transport`                | 3    | the exchange request failed at the network layer, timed out (30 s) or was refused a redirect                                                                                                                                                                                            |
| `exchange_failed`          | 3    | a non-2xx reply without a usable error envelope, or a 2xx body that fails validation; the body is never echoed                                                                                                                                                                          |
| `oidc_request_failed`      | 3    | the `ACTIONS_ID_TOKEN_REQUEST_URL` fetch failed (non-2xx, or a body without `value`)                                                                                                                                                                                                    |
| `token_cache_error`        | 3    | the token cache could not be written or deleted; the message names the operation, never a token value                                                                                                                                                                                   |
| `manifest_unreadable`      | 3    | the manifest file could not be read (missing, unreadable, not a regular file or empty); the message names the errno code, never the path or the content                                                                                                                                 |
| `manifest_too_large`       | 3    | the manifest file is larger than the server's 256 KiB transport cap; refused locally, before any request                                                                                                                                                                                |
| `request_failed`           | 3    | a non-2xx reply from a project-scoped command (`manifests upload`, `runs create`) without a usable error envelope, or a 2xx body that fails validation; the body is never echoed                                                                                                        |
| `binding_pending`          | 3    | 403: the repository has not presented the binding challenge yet; hint: set the repository variable `EVIDGATE_BINDING_CHALLENGE` to the challenge shown when the binding was created                                                                                                     |
| `binding_conflict`         | 3    | 409: this repository is already bound to another organization; hint: its admin must disable that binding first                                                                                                                                                                          |
| `transport_failed`         | 3    | an `events send` request failed at the network layer or before a reply (connection refused, the 30 s per-request timeout aborting it, a refused redirect); retried while the send deadline allows and reported only when the retries or the deadline are exhausted                      |
| `deadline_exceeded`        | 3    | `events send` or `artifacts upload` would have to sleep past `EVIDGATE_SEND_DEADLINE` / `EVIDGATE_UPLOAD_DEADLINE`; nothing further is dispatched, and the message names the last error's code, status and `request_id` (plus the unsent batch range for a send)                        |
| `ack_mismatch`             | 3    | an events 200 whose acks are not one per event, in request order, for the same `event_id` and `accepted`/`duplicate`, or whose counts disagree, or whose body is not an object; nothing advances                                                                                        |
| `invalid_spool_line`       | 3    | a line handed to the CLI intake is not one JSON envelope with an advancing integer `sequence`                                                                                                                                                                                           |
| `invalid_spool_id`         | 3    | a run or producer id is not a safe spool path segment, or resolves outside the spool root                                                                                                                                                                                               |
| `invalid_spool_max_bytes`  | 3    | a spool cap that is not a positive integer (`EVIDGATE_SPOOL_MAX_BYTES` parsing is `invalid_config`)                                                                                                                                                                                     |
| `invalid_spool_input`      | 3    | `events send --from` input is unreadable or malformed (blank line, invalid UTF-8, bad `sequence`/`producer_id`); see §11                                                                                                                                                                |
| `envelope_invalid`         | 3    | a line fails the Ajv envelope schema; the message names the line and adds one `detail:` per schema path                                                                                                                                                                                 |
| `event_too_large`          | 3    | one event cannot fit a 1 MiB batch on its own; the message names its `event_id` and `sequence`                                                                                                                                                                                          |
| `spool_cap_exceeded`       | 3    | the run directory would cross `EVIDGATE_SPOOL_MAX_BYTES`; nothing was written; names the run and the unsent range                                                                                                                                                                       |
| `spool_corrupt`            | 3    | a spool line or `cursor.json` that is neither a torn tail nor valid (an `acked_count` above `last_acked_sequence` included), or a line longer than the 32 MiB per-line guard; the message names the chunk and line                                                                      |
| `spool_missing`            | 3    | no spool exists for that run and producer (never written, or purged)                                                                                                                                                                                                                    |
| `spool_locked`             | 3    | a live process holds `spool.lock`, a live taker holds the `spool.lock.takeover` mutex while breaking a stale lock, or a live reporter holds `writer.lock` (§11.2, D-S56-Q8), for that producer directory; the message names its pid                                                     |
| `spool_unsafe_path`        | 3    | a spool path is not a real, owned object — a symlink, another user's directory, or a non-file where a file belongs — or the lock could not be created (the filesystem cannot hard-link) or inspected (an I/O errno); hint: an owned, symlink-free spool on a filesystem with hard links |
| `invalid_ack`              | 3    | a cursor advance that is not a contiguous prefix already durable in the spool (internal guard)                                                                                                                                                                                          |
| `unsent_events`            | 3    | `runs finalize` refused: the spool still holds unacked events; the message names the run and the unsent range                                                                                                                                                                           |
| `upload_path_refused`      | 3    | `artifacts upload --file` is empty, has a control character, is missing/unreadable, is not a regular file (symlink, FIFO, device, directory), changed between the allow-list check and the open, or its real path is outside the allowed roots; refused before any request (§12)        |
| `upload_too_large`         | 3    | the file is larger than the kind's static cap (`artifact-kind-defaults.v1.json`); refused before any request. A project policy cap is the server's 413 `payload_too_large`, surfaced verbatim (§12)                                                                                     |
| `upload_checksum_mismatch` | 3    | the file changed between the hash pass and the streaming PUT (size or digest), so the upload was aborted: no `complete`, never retried; the message names the mismatch, never the bytes (§12)                                                                                           |
| `upload_put_failed`        | 3    | the object store refused the PUT (its XML/text body is never echoed), or the presigned URL expired and no renewal was left; retried only for 408/429/5xx/transport (§12)                                                                                                                |
| `artifact_rejected`        | 3    | `complete` committed `state: rejected`: the message names the artifact id and the server's `failure_code`, with no retry. A re-run asks `complete` again (a rejection is replayable), so a re-run never reports `ready` for rejected bytes (§12)                                        |
| `internal_error`           | 3    | an unexpected exception; the message is redacted, and the stack is printed only under `--verbose` (also redacted)                                                                                                                                                                       |

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
- The spool family (`invalid_spool_line`, `invalid_spool_id`,
  `invalid_spool_max_bytes`, `invalid_spool_input`, `envelope_invalid`,
  `event_too_large`, `spool_cap_exceeded`, `spool_corrupt`, `spool_missing`,
  `spool_locked`, `spool_unsafe_path`, `invalid_ack`, `unsent_events`) is raised
  by `events send` and `runs finalize` (§11) and exits 3 without a server call:
  each message names the
  run and, where it exists, the chunk/line or the unsent sequence range, and never
  a token. `spool.lock` and `cursor.json` hold no credential. `spool_locked`,
  `spool_missing` and `unsent_events` carry a hint (wait for the running process,
  run the reporter or `events send` first); none of them retries anything.
- `transport_failed`, `deadline_exceeded` and `ack_mismatch` come from the send
  path (§11.6), not from the spool: the first after the retry loop or the send
  deadline is exhausted, the second when the next wait would reach the deadline,
  the third when a 200's acks cannot be trusted. None of them advances the cursor,
  and none is a server `error.code`. `deadline_exceeded` is also the upload path's
  one synthetic failure (§12.3): `artifacts upload` raises it when the next wait
  would pass `EVIDGATE_UPLOAD_DEADLINE`, and it never mints a second artifact row.
- A server reply to `events send` or `runs finalize` keeps the §5 rule above: the
  `error.code` and `request_id` are printed verbatim (`producer_slot_taken`,
  `unknown_producer`, `manifest_invalid`, `idempotency_conflict`, …). Finalize
  never retries; `events send` retries only 429, 408, 5xx and transport failures
  (§11.6) and then exits 3.

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
  `manifests upload` (§8); `runs create` (04.2.2-T2, §9), `events send` and
  `runs finalize` (04.2.3-T1/T2, §11) applied the same rule — for the spool
  commands a 401 also aborts the drain without retrying a batch.

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
| `--release <name>`               | no       | body `release`                                             | trimmed, 1..128 code points        |
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
   - `--release`, after trim, must be 1..128 code points, else exit 3 `usage`; a
     name the server cannot resolve is 422 `release_unresolvable`, mapped as in
     step 4.
   - `--ci-ref` must be a full git ref (below), else exit 3 `usage` with the
     one-line help pointer.
   - `--idempotency-key` must be 1..128 printable ASCII characters (0x21–0x7E,
     no whitespace or control), else exit 3 `usage`.
   - Every option may be given only once: a repeated option (e.g. `--release`
     twice) is exit 3 `usage` with `--<flag> may be given only once`, before any
     request.
2. `getToken()` (04.2.1) supplies the bearer and `createApiClient` builds the
   redirect-refusing client; the generated `createRun` operation is called with
   that client explicitly (§6). The request carries `{p: <project>}`, the
   `Idempotency-Key` header and the body `{manifest_id, release?, ref?}` —
   absent members are omitted and `manifest_hash`, `purpose`, `components` and
   `replay_grant_id` are never sent. `--manifest-hash` feeds only the derived
   key and the output. The call has a 30 s timeout
   (`AbortSignal.timeout(30_000)`) and no retry.
3. A 201 body must be `{ run_id: <uuid>, state: <non-empty string> }` and its
   `state` must match `^[a-z][a-z0-9_]{0,31}$`; anything else is exit 3
   `request_failed` and the body is never echoed.
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
- `refs/pull/<N>/merge` with `N` a safe integer ≥ 1 (≤ 9007199254740991) →
  `{kind: "pull_request", name: "<N>/merge", pr_number: <N>}`; `name` is built
  from `N`'s decimal digits (`<N>/merge`), and a larger `N` is exit 3 `usage`.
- `<name>` must be 1..255 code points. Any other value — unprefixed, empty,
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
  `env` `EVIDGATE_API_URL: ${{ vars.EVIDGATE_API_URL }}` and, on the artifact
  step, `EVIDGATE_EXECUTION: ${{ vars.EVIDGATE_EXECUTION }}` — both repository
  configuration **variables**, not secrets (the execution key is the planned
  execution's 64-hex digest) and the only stored settings — and
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
"$MANIFEST_ID" --manifest-hash "$MANIFEST_HASH" --ci-ref "$GITHUB_REF" --json`
  (04.2.2-T2, §9; the step id is `run` and it writes the masked `run_id` to
  `$GITHUB_OUTPUT` for the spool steps), then the two spool steps of §11 —
  `npx … -- evidgate events send --run "$RUN_ID"` and
  `npx … -- evidgate runs finalize --run "$RUN_ID" --producer shard-1 --shard 1
--manifest-hash "$MANIFEST_HASH"` — with
  `EVIDGATE_SPOOL_DIR: ${{ github.workspace }}/.evidgate-spool` in both, the
  directory the reporter writes (04.3.1-T1/04.3.3-T1, named in a comment where
  the reporter step belongs);
- every step is a real command, never a fake one: `runs create` (04.2.2-T2), the
  two spool commands (04.2.3-T1/T2) and `artifacts upload` (04.2.4-T1), which
  runs twice in the template — a trace and the HTML report, each with
  `--execution-key "$EVIDGATE_EXECUTION"` — because one invocation ships one
  file (§12).

No `secrets.` reference and no token or secret value appears anywhere in the
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
`npx` lines for `auth login`, `manifests upload`, `runs create` (the last without
`--ci-ref`, which needs the GitHub ref; a comment shows the form for a CI that
has one) and the two spool steps of §11, and exports
`EVIDGATE_SPOOL_DIR="${EVIDGATE_SPOOL_DIR:-$PWD/.evidgate-spool}"` before them
so the reporter and the CLI share one directory. `artifacts upload` is a real
step here too (04.2.4-T1), as in the GitHub template.

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

## 11. Spool, `events send` and `runs finalize`

04.2.3-T1/T2 (2026-10-23): the customer-side queue between the reporter (04.3)
and `POST /v1/runs/{r}/events`, and the two commands that drain it. The spool is
plain files under `EVIDGATE_SPOOL_DIR`, so a CLI crash, a lost ACK or a
10-minute outage loses nothing: the next run resumes at the first unacked
sequence (04.2.3 §2 AC1/AC2, A9/A10).

### 11.1 On-disk layout

Layout decision D-S54-Q1: the CLI adopts the merged reporter's layout verbatim
(`packages/adapter-playwright/src/spool.ts`), so one directory is written by the
reporter and read by the CLI with no translation layer:

```
$EVIDGATE_SPOOL_DIR/<run_id>/<producer_id>/
  000001.jsonl      # append-only: one 00 §9.4 envelope per line, \n terminated
  000002.jsonl      # next chunk; rotated at 1 MiB or 1000 lines (D2)
  shards.json       # reporter-owned: { producer_id, shard_index?, shard_total?,
                    #   sequence_next, last_restart_at? }; legacy checkpoint.json
                    #   is read as a fallback
  writer.lock       # reporter-owned exclusive writer lock ({ pid, started_at }),
                    #   present while a reporter writes
  cursor.json       # CLI-owned ack cursor (fields below)
  spool.lock        # exclusive CLI lock, present only while a command holds it
  spool.lock.takeover # transient CLI takeover mutex ({ pid, started_at }), present
                    #   while one process breaks a stale spool.lock; a crashed
                    #   taker's mutex stays until another takeover reclaims it
  uploads/          # 0700: one `<identity>-<artifact_id>.json` upload journal per artifact (§12)
```

A takeover also renames the lock it breaks, and the dead mutex of a taker that
crashed, to unique `.stale` quarantine names
(`spool.lock.<pid>.<uuid>.stale`, `takeover.<uuid>.stale`) for a few syscalls,
so such a name may briefly appear in a producer directory: the takeover removes
it, and a holder's release deletes any `spool.lock.*.stale` name that still
carries its own lock inode (§11.2).

- `<run_id>` and `<producer_id>` are single path segments (§5
  `invalid_spool_id`): `[A-Za-z0-9._-]`, not `.`/`..`, at most 128 characters,
  and the resolved path must stay inside `EVIDGATE_SPOOL_DIR`. There is no
  `<org_hash>` level and no `journal.seq` file: the run id already scopes the
  tree (D-S54-Q1). [DECISION] (D-S54-Q1) Decided A by the coordinator (owner
  rule 2026-10-23, recommended option); recorded for owner phase review as
  D-S54-Q1, non-blocking.
- `shards.json` is the reporter's own sequence record and replaces the
  per-flush `checkpoint.json` rewrite (D-S56-Q1). Its fields are
  `producer_id`, optional `shard_index`/`shard_total`, `sequence_next` and
  optional `last_restart_at`; it is written atomically (temp file, fsync,
  rename, directory fsync) at chunk rotation, at close and when a restart is
  detected — never per callback flush. A stale or torn copy is therefore
  normal, never a corrupt spool. The CLI neither writes nor reads it: the
  CLI's own progress is `cursor.json`. A `checkpoint.json` left by an older
  writer (`{ producer_id, next_sequence }`) is read as a fallback when
  `shards.json` is absent and is never written again.
  [DECISION] (D-S56-Q1) Decided A by the coordinator (owner rule 2026-10-23,
  recommended option); recorded for owner phase review as D-S56-Q1,
  non-blocking.
- Crash resume is `max(shards.json.sequence_next, last chunk sequence + 1)`, so
  `sequence_next` is a FLOOR, not the resume point (D-S56-Q9): the only sequence
  refusal the server makes is reuse (`422 sequence_reused`,
  contracts/events/ordering.md §1), while a hole is accepted (§3), so a sparse
  record can delay a sequence but can never lose one. The reporter itself never
  writes a record ahead of the chunks (`playwright-mapping.md` "## shards"); a
  floor that is ahead of them anyway (a legacy or hand-edited file) leaves a
  hole that `events send` refuses loudly as `spool_corrupt` (§11, exit 3) rather
  than sending around it. A value that is not a safe integer is dropped on read
  rather than trusted. [DECISION] (D-S56-Q9) Decided
  A by the coordinator (owner rule 2026-10-23, recommended option); recorded for
  owner phase review as D-S56-Q9, non-blocking.
- Directories are created `0700`, every spool file is `0600`, and a symlinked or
  foreign-owned run/producer directory or chunk file is refused with exit 3
  `spool_unsafe_path` rather than followed.
- `cursor.json` holds `producer_id`, `next_unacked_sequence`,
  `last_acked_sequence`, `acked_count` and `updated_at`, with
  `last_acked_sequence = next_unacked_sequence - 1`; a missing file means
  "nothing acked yet" (`1`/`0`/`0`). It is replaced atomically — tmp file, fsync,
  rename, directory fsync — and every chunk the cursor claims is durable before
  it may advance: each chunk is fsynced once by this process (a fresh process
  syncs every chunk on its first ack) and the highest chunk — the only one a
  writer may still append to — on every ack, then the directory, so a send costs
  O(chunks) syncs in total rather than O(batches × chunks) and a crash can lose
  an ack but never acknowledge an undurable line.

### 11.2 Chunk retention and the two writer locks

D-S54-Q2: an acked chunk is **never** deleted before a finalize succeeds, so a
restarted reporter's chunk scan can never see a consumed chunk name vanish and
reuse it; the whole producer directory is removed at once after a successful
finalize (§11.7). Rotation always moves to `highest + 1`.

There are two locks, and neither replaces the other.

The **CLI lock** serialises CLI processes: `events send` and `runs finalize` hold
`<producer_dir>/spool.lock` — a `0600` record `{ pid, started_at }` created
atomically by hard-linking a fully written temp file (the `link` is the winner
decision, so the path is never observable empty or torn), containing those two
fields and nothing else — while they read, ack or purge. Because the record is
hard-linked into place, the spool directory must live on a filesystem that
supports hard links; where it does not (`EPERM`, `ENOTSUP`, `EOPNOTSUPP`, `EXDEV`
or `ENOSYS`), the lock is refused with exit 3 `spool_unsafe_path` and an actionable
hint instead of proceeding unlocked. A second
CLI process gets exit 3 `spool_locked` naming the live pid.

A stale lock — one whose pid is dead on this host, or an entry that cannot be a
lock — is taken over only while holding `<producer_dir>/spool.lock.takeover`, a
transient mutex with the same `0600` hard-linked `{ pid, started_at }` record.
The contender links the mutex first; when a live taker already holds it, the
second contender exits 3 `spool_locked` naming that taker's pid instead of
queueing, because a takeover lasts a few syscalls. Under the mutex it re-reads
`spool.lock`. A live record there — a successor that linked its own lock
meanwhile — stays at the path and is exit 3 `spool_locked` naming it; any other
record it did not observe, or none at all, makes the acquisition link
`spool.lock` again. Only the stale record it observed — dead or unreadable — is
renamed to a unique `spool.lock.<pid>.<uuid>.stale` quarantine, and what was
actually moved is read back: the observed stale record is deleted, while any
other record is put back at the path with a `link` that fails when a newer lock
already owns it, and the quarantine name is removed either way. The mutex is
released only while it still carries the contender's own inode; the dead mutex
of a taker that died is reclaimed by renaming it to a unique
`takeover.<uuid>.stale` quarantine, verifying the moved record is that dead one,
and deleting it — or putting it back when it is not. A takeover that did not win
the lock links `spool.lock` again, up to 3 attempts in total. Deleting a lock
therefore requires the moved file to be verified as the stale record the taker
observed, and reclaiming a dead mutex requires the same check; the window both
paths leave open is the one the code names — a crashed taker, two simultaneous
reclaimers, a third publisher, and a contender linking into another reclaim's
restore gap — which the post-rename verification narrows but does not close.
Only "there is nothing to read" (`ENOENT`, `ELOOP`, `ENXIO`) or a
file that cannot be a lock — non-regular, over 1 KiB, or unparseable — counts as a
dead holder, after one re-read following a short pause; the file is opened
`O_NOFOLLOW|O_NONBLOCK`, so a planted symlink, FIFO or device is neither followed
nor blocking. Any other errno (`EACCES`, `EIO`, `EMFILE`, `ENFILE`, …) means the
filesystem could not be inspected: that is exit 3 `spool_unsafe_path` and **no**
takeover, so a live lock can never be broken by a read failure. [DECISION] (D-S54-Q2) Decided A
by the coordinator (owner rule 2026-10-23, recommended option); recorded for
owner phase review as D-S54-Q2, non-blocking.

The **reporter lock** catches the writer the CLI lock cannot see (D-S56-Q8): the
reporter appends chunks without `spool.lock`, so it holds its own
`<producer_dir>/writer.lock` — the same `{ pid, started_at }` record, `0600`,
hard-linked into place — for the whole life of a writer. Every CLI command that
reads, acks, finalizes or deletes chunks (`events send`'s intake append and its
drain, and `runs finalize`) takes `spool.lock` first and then inspects
`writer.lock`:

- a live pid is exit 3 `spool_locked` naming that pid, with the hint "the reporter
  is still writing this producer". Nothing is sent, acked or purged, the command
  never waits and never takes the lock over;
- a pid dead on this host is ignored, and the lock file is left exactly where it
  is: the reporter's next start performs the verified rename-aside takeover, so
  the CLI can never delete a live successor's lock;
- a file that cannot be a lock — symlink, FIFO, device, over 1 KiB, unparseable,
  or `ENOENT`/`ELOOP`/`ENXIO` — counts as absent after one re-read;
- any other errno is exit 3 `spool_unsafe_path` with no takeover, so a lock the
  CLI could not inspect never becomes a purge of a live spool.

`runs finalize` re-checks `writer.lock` immediately before its recursive purge:
if a reporter started writing while the finalize request was in flight, the run
is still finalized but the producer directory is kept and announced as a warning.
The upload journal (`uploads/<identity>-<artifact_id>.json`, §12) is deliberately excluded
from this check: it only needs `spool.lock`, so a running test run never blocks
an artifact upload. [DECISION] (D-S56-Q8) Decided A by the coordinator (owner
rule 2026-10-23, recommended option); recorded for owner phase review as
D-S56-Q8, non-blocking.

### 11.3 Torn writes

D-S54-Q3: the reporter's lines carry no checksum, so a line is trusted when it
(1) ends with `\n`, (2) parses as JSON, (3) passes the Ajv envelope schema the
server checks with (T42), and (4) carries the next contiguous `sequence`. A
final line without its newline is a torn write: the reader ignores it, and only
the CLI truncates it away — when the CLI itself is the appender. A bad line
anywhere else is exit 3 `spool_corrupt` naming the chunk file and line number,
never silently skipped. One size rule rides on the same check: a single line
longer than the 32 MiB per-line guard (`maxSpoolLineBytes`, the same bound the
`--from` intake enforces) cannot be a line this CLI or a writer following this
contract produced, so it is exit 3 `spool_corrupt` naming the chunk and line
rather than buffered whole — the reader never grows its buffer past that bound. A
torn or stale `shards.json` is not a torn write in this sense: it is the
reporter's own record, written atomically and never read by the CLI (§11.1, D-S56-Q1).
[DECISION] (D-S54-Q3) Decided A by the coordinator
(owner rule 2026-10-23, recommended option); recorded for owner phase review as
D-S54-Q3, non-blocking.

### 11.4 Size cap

`EVIDGATE_SPOOL_MAX_BYTES` (default `268435456`, 256 MiB) caps one run directory
— the summed size of every file under `<spool_dir>/<run_id>`, all producers
included. The cap is enforced before an `events send --from` intake writes
anything, and it is reported when `events send` finds the run directory already
over it: exit 3 `spool_cap_exceeded` naming the run, the unsent sequence range and
the cap — never a silent drop (AC3). Raising the variable is the documented
remedy. The reporter enforces the same cap on its own side, before the first byte
of each batch, with the same error (`spool_cap_exceeded`, all-or-nothing), so the
limit is a write-time refusal on both sides rather than a CLI pre-flight only.

The intake is capped on the input side too: the reader stops as soon as the
running byte total would cross what is left (`cap` minus the run directory's
current bytes) and raises `spool_cap_exceeded`, so neither a multi-GB `--from`
file nor an endless stdin stream is ever held in memory. One `--from` line is
additionally bounded by the spool reader's own 32 MiB cap (`maxSpoolLineBytes`),
measured in UTF-8 bytes rather than UTF-16 code units, and refused as
`invalid_spool_input` instead of buffered — so an intake can never spool a line the
reader would reject as `spool_corrupt`. The cap is checked
against **every** producer's lines before the first append, so one intake call is
all-or-nothing across producers: a refused call leaves the spool byte-identical,
and re-running the same file cannot collide with a half-applied prefix.

### 11.5 Environment

| Variable                   | Default    | Effect                                                  |
| -------------------------- | ---------- | ------------------------------------------------------- |
| `EVIDGATE_SPOOL_DIR`       | none       | spool root; required by both commands, `missing_config` |
| `EVIDGATE_SPOOL_MAX_BYTES` | 268435456  | per-run directory cap (§11.4)                           |
| `EVIDGATE_SEND_DEADLINE`   | 1800000 ms | one deadline for a whole send (§11.6)                   |

The parsing rules are in §3; each refuses a bad value with `invalid_config`
naming the variable and never echoing it, and only `events send` and `runs
finalize` resolve them (lazily, §3), so a malformed value cannot break a command
that does not use the spool.

### 11.6 `evidgate events send`

`evidgate events send --run <uuid> [--producer <p>] [--from <file|->] [--json]`
(04.2.3-T1) drains unacked envelopes to `POST /v1/runs/{r}/events` (openapi
`postRunEvents`, 00 §9.3/§9.4).

- `--run` is required (a UUID). `--producer` restricts the command to one
  producer directory; without it every producer of the run is drained in sorted
  order. `--from <file|->` imports envelopes into the spool first (one JSON
  object per line, `-` is stdin) — the only CLI intake, and the only place the
  size cap refuses a write.
- Batching: at most **500 events** and **1 MiB** of raw `{"events":[…]}` body per
  request (AC4). `Content-Encoding: gzip` is applied only when compression saves
  more than 1 KiB **and** the raw body is at most 19× the compressed size, so a
  body the server's 20:1 expansion cap would refuse as `batch_too_large` is sent
  raw instead; otherwise the raw body is sent. One event that cannot fit a 1 MiB
  batch on its own fails the file with exit 3 `event_too_large` naming its
  `event_id` and `sequence`.
- `Idempotency-Key` per batch: `batch:` + lowercase SHA-256 hex of the UTF-8
  JSON array `[version, run_id, producer_id, [[sequence, event_id], …]]`, so the
  key is stable across process restarts and a different batch never reuses it; a
  retry resends the **same bytes with the same key** (belt and braces over the
  server's `event_id` dedupe). Well inside the 128-character cap.
- Concurrency: at most **4 batches in flight** per run.
- Retry: only 429, 408, 5xx and transport failures, with exponential backoff
  (base 500 ms, doubling, capped at 30 s) and full jitter. A server
  `Retry-After` (delta-seconds or HTTP-date) is honoured but never shorter than
  that attempt's jittered backoff — the longer of the two is the wait, so
  `Retry-After: 0` or a past date cannot spin. One deadline
  (`EVIDGATE_SEND_DEADLINE`) bounds the whole send: a wait that would reach the
  deadline fails `deadline_exceeded` instead of sleeping, and a transport failure
  that outlives the retries carries `transport_failed`. An envelope that says
  `retryable: false` is not retried.
- Fail fast, never retried: 401 (the cached CI token is purged, §7), 403
  `producer_slot_taken`, 409, 413 `batch_too_large`, 422 `unknown_producer` /
  `unknown_execution_key` / `sequence_reused` and every other 4xx — exit 3 with
  the server's `error.code` and `request_id` verbatim (T9). On the first fatal
  failure no new batch is dispatched and the ones in flight are awaited, so the
  cursor only advances over acknowledged batches; a batch that has not started
  retries nothing once a sibling failed (it stops instead of waiting out a
  backoff that can no longer deliver). The message names the run, the producer
  and the **full** unsent range as it stands after the send settled
  (`unsentRange()` — from the first unacked sequence to the last durable one),
  not just the failing batch's range. The cursor is not rolled back:
  nothing is marked `failed` on disk, and a later explicit rerun re-sends from
  the first unacked sequence (D-S54-Q4).
- A `duplicate` ack counts as delivered; `accepted` and `duplicate` are summed
  per batch and reported. Every 200 is verified before anything advances: one
  ack per event, in request order, naming the same `event_id` (case-insensitive
  GUID) with status `accepted` or `duplicate`, and body counts that match the
  acks — anything else is exit 3 `ack_mismatch` and the cursor stays put. A
  crash or a lost ACK resumes from
  `cursor.json`'s `next_unacked_sequence` — no event is skipped, and a resend
  keeps its identity (AC2). The command **never deletes spool data**; it only
  advances the cursor.
- Output: text mode, one line per producer that had work —
  `evidgate: sent run=<id> producer=<p> batches=<N> accepted=<A> duplicate=<D>`
  — and `evidgate: nothing to send run=<id>` when nothing was unacked. `--json`
  prints exactly one object:
  `{"run_id":…,"producers":[{"producer_id":…,"batches":N,"accepted":A,"duplicates":D}],"batches":N,"accepted":A,"duplicates":D}`
  (an empty `producers` array and zero totals when nothing was sent). No token
  is ever printed or written to the spool.

Exit codes: `0` when every producer is drained (including "nothing to send");
`3` for a usage, configuration, spool, transport or server error. The command
emits no other code.

### 11.7 `evidgate runs finalize`

`evidgate runs finalize --run <uuid> --producer <p> --shard <1..256>
--manifest-hash <sha256:64hex> [--keep-spool] [--json]` (04.2.3-T2) declares one
producer slot done at `POST /v1/runs/{r}/finalize` (openapi `finalizeRun`,
00 §9.3, 05.3.2-T1) and prints the accepted state.

- Flags: every one is validated **before** `getToken()`, so a bad flag makes no
  request at all — not even the token exchange. Each option may be given once
  (§9 step 1); a repetition is exit 3 `usage`. `--producer` matches the openapi
  `^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$`, `--shard` the slot's own 1-based
  manifest index (1..256, no sign, no leading zero), `--manifest-hash` the
  server's `sha256:` + 64 lowercase hex form.
- `last_sequence` and `count` are **read from the spool, never from flags**:
  `cursor.json`'s `last_acked_sequence` and `acked_count`, under `spool.lock`
  (D-S54-Q2). Finalize never declares a sequence the server has not
  acknowledged: a spool that still holds unacked events is exit 3
  `unsent_events` naming the run and the unsent range (D7), a missing
  producer directory is exit 3 `spool_missing` — the command never creates one —
  and a cursor whose `acked_count` exceeds `last_acked_sequence` is exit 3
  `spool_corrupt` naming the file, never a declaration sent to the server.
- The request body is exactly `{producer_id, shard, last_sequence, count,
manifest_hash}` (`additionalProperties: false`), one POST with a 30 s
  timeout. `Idempotency-Key` is the lowercase SHA-256 hex of the UTF-8 JSON
  array `['evidgate.finalize-key.v1', run_id, producer_id, shard, last_sequence,
count, manifest_hash]` — stable across restarts, different whenever any value
  differs, 64 hex characters, exported as `deriveFinalizeIdempotencyKey` so a
  unit test pins it.
- 202 (no body) prints `evidgate: finalized run=<id> producer=<p> shard=<n>
last_sequence=<L> count=<C>; reconcile accepted (asynchronous)`, or under
  `--json` one line
  `{"run_id":…,"producer_id":…,"shard":…,"last_sequence":…,"count":…,"manifest_hash":…,"state":"reconcile_accepted"}`,
  in that key order. Reconciliation is asynchronous server-side.
- After a 202 the producer directory is purged whole unless `--keep-spool` is
  given (a runner keeps nothing, a local investigation can keep the evidence).
  Immediately before the recursive removal the run and producer directories are
  re-checked as real, owned, non-symlink directories, so a directory swapped for
  a symlink is refused with `spool_unsafe_path` and the link's target is never
  deleted. Any purge failure after the 202 — that refusal included — is a stderr
  warning (`evidgate: warning: … (errno …)`, naming no path) and still exits 0:
  the run is finalized, the leftover spool is only local disk.
- A 403 `producer_slot_taken`, a 422 `unknown_producer` or `manifest_invalid`, a
  409 (`project_deleting`, `idempotency_conflict`) and a 413 `payload_too_large`
  exit 3 with the server `error.code` + `request_id` verbatim and **no retry**:
  exactly one request is made. The hint says the command is idempotent — the server
  arbitrates on the slot's closed marker, not on the key, so a repeat with a
  fresh key is the same no-op 202 — and re-running after fixing the cause is
  safe. A 401 purges the cached CI token (§7); a 403/422 keeps it.

Exit codes: `0` on a 202; `3` for a usage, configuration, spool, transport or
server error. The command emits no other code.

### 11.8 Retention and the abandoned-spool sweep

The only automatic deletion is the purge after a successful finalize
(`--keep-spool` opts out). A spool is **never** deleted while it holds unacked
events, and no CLI command sweeps the spool root: an abandoned
`<spool_dir>/<run_id>` is the customer's to delete (a CI runner disappears with
it; a self-hosted runner is swept by the operator). [INFERENCE] The TTL sweep
itself is not built by 04.2.3 — it is a documented operator action, and a
scheduled sweep lands with the docs site (04.9.2-T1) if it is wanted. The spool
may hold test names and `ext` payloads (04.2.3 §8), so a leftover directory is
sensitive local data and a purge failure is reported, never hidden.

## 12. Artifacts upload

`evidgate artifacts upload` ships one file to evidence storage (04.2.4 §5, §6, §8):

```sh
evidgate artifacts upload --run <run_id> --file <path> --kind <kind>
  [--producer <slot>] --execution-key <sha256>
  [--attempt <n>] [--media-type <type>] [--dir <root>]... [--json]
```

- `--run` is the openapi `RunId` (UUID); `--file` is one file (one artifact per
  invocation); `--kind` is the closed server list (T20): `trace`, `video`,
  `screenshot`, `raw_report`, `dom_snapshot`, `a11y_snapshot`, `patch`,
  `ui_hierarchy`.
- `--producer` is the manifest slot id of T54 (`shard-<index>`); it defaults to
  `shard-1`, the unsharded slot, and a sharded job passes its own `--producer`.
  The charset is the producer guard of every producer command
  (`^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$`).
- `--execution-key` is required: the 64-lowercase-hex digest of the planned
  execution the artifact belongs to. The CLI cannot derive it — only the
  manifest knows which execution produced the file — so guessing one would
  attach the artifact to the wrong execution server-side.
- `--attempt` is the execution attempt (`1` when absent), `--media-type` overrides
  the kind's static default, `--dir` adds an allowed root (repeatable).
- There is no `--sensitivity` flag: `createArtifactUploadUrl` has no such body
  member (its required list is `run_id`, `producer_id`, `execution_key`,
  `attempt_no`, `kind`, `media_type`, `bytes`, `sha256`). Sensitivity is the
  project's policy row, and a policy that refuses the kind is the server's
  verbatim error — the CLI does not invent a field the route would reject.
  [DECISION] (D-S56-Q7) Decided A by the coordinator (owner rule 2026-10-23,
  recommended option); recorded for owner phase review as D-S56-Q7, non-blocking.
- `--json` prints one line:
  `{ "file", "artifact_id", "state", "bytes", "scan_state" }`. Without it the
  line is `artifact_id=<uuid> state=<state> bytes=<n> file=<name>`; `file` is the
  base name, control characters escaped.

The call sequence is the openapi one: `POST /v1/artifacts/upload-url` with body
`{run_id, producer_id, execution_key, attempt_no, kind, media_type, bytes,
sha256}` (`sha256` is `sha256:<64 lowercase hex>`), the PUT to the returned
`upload_url` with exactly the signed `required_headers`, then
`POST /v1/artifacts/{a}/complete` with its own `Idempotency-Key`.

No 2xx body is trusted before it is checked against `contracts/openapi.yaml`:
the `201` needs a UUID `artifact_id` (the same strict check the journal uses),
a non-empty `object_key`, a `upload_url`, a `required_headers` map of strings
and a parseable `expires_at`; the `complete` body needs the documented `state`
and, when ready, a `bytes` count. Anything else is `request_failed` (a
`bad_response`): it names the member, never a value, and no half-read URL is ever
PUT to.

### 12.1 The `upload-url` `Idempotency-Key`

`sha256` of a **length-prefixed** UTF-8 concatenation, one field per member, in
this order: `run_id`, `producer_id`, `execution_key`, `attempt_no` (decimal
`String(n)`), `kind`, `file_sha256` (`sha256:<hex>`), then `mint_no` (decimal) —
the last one only when it is not `0`. Each field is written as
`<byteLength>:<value>` (`byteLength` of the UTF-8 bytes, then `:`), which makes
the pre-image unambiguous: `ab|c` and `a|bc` cannot collide, and the appended
mint cannot be confused with a longer last field. Without the prefix the six
fields would also be a valid seven-field encoding.

The digest is 64 hex characters, inside the route's 128-character cap, and is
identical across process restarts: a retried or resumed upload replays the
stored `201` (T11/T92) instead of minting a second artifact row. `mint_no`
counts the `upload-url` mints for one upload identity inside the run's journal:
`0` is the AC1 key, `n > 0` is a deterministic renewal after an expired
presigned URL, so the dead row is never replayed. The identity is the six-key
`upload-url` body, so a different execution, kind or attempt is a different key
and therefore a different server row; a changed `--media-type` is the one
exception — it keeps the key (it is not a key field) and changes the body, so the
server answers its own `409 idempotency_conflict`, which surfaces verbatim and is
never retried. Either way the CLI starts again at mint `0` instead of resuming
the other record.

`complete` uses the same shape as `runs finalize`: the lowercase-hex SHA-256 of
the UTF-8 JSON array `["evidgate.artifact-complete-key.v1", artifact_id,
file_sha256, size]`.

### 12.2 The presigned PUT

- `Content-Length` and `x-amz-checksum-sha256` are required in the signed headers
  (T18 signs both). A response that signs neither, a length that disagrees with
  the `fstat` size, or a checksum that disagrees with the local digest is
  `request_failed` before the PUT: nothing is uploaded. So is a signed header the
  client must never send — `authorization`, `cookie`, `set-cookie`, `host`,
  `proxy-authorization`, `proxy-*`, `transfer-encoding`, `connection` — and one
  the transport owns, where `fetch` would throw a `TypeError` the retry loop
  would read as a transport failure: `expect`, `te`, `trailer`, `upgrade`,
  `keep-alive`, `content-encoding` (all case-insensitive). A signed header name
  that is not an RFC 9110 token, or a value carrying a character `fetch` refuses
  (CR, LF, NUL, ESC or any other byte outside tab / printable ASCII / Latin-1), is
  refused the same way (`request_failed`, never retried). Every refusal names the
  header — or, for a name that is not a token, the rule — and never its value, and
  nothing is sent.
- The bytes are read in two passes with O(1) memory: pass 1 hashes the **open
  descriptor** (the guard `fstat`ed inode) in 64 KiB chunks; pass 2 streams a
  **re-opened descriptor** for the PUT while re-hashing every chunk. That is the
  "one reading pass each / never buffer the whole file" rule of §8: the signed
  checksum header must exist before the PUT, so one pass cannot both produce it
  and send it.
- The descriptor pass 1 read stays open until the PUT is over, and every PUT
  attempt re-opens the file through the same guard: `O_NOFOLLOW|O_NONBLOCK`, then
  `fstat` with `dev`/`ino`/`size` compared against the pinned descriptor. A file
  replaced, symlinked over or resized between the passes is
  `upload_path_refused` (never retried, never completed), so the uploaded bytes are
  always the bytes that were hashed.
- If the digest or the byte count changes between the passes the upload aborts
  (`upload_checksum_mismatch`), the store object stays uncompleted under
  `pending/` and expires by lifecycle, and `complete` is never called.
- The bearer token never goes to the object store; the signature in the URL is
  the only credential there.
- The PUT has no 30 s per-request timeout (that cap belongs to the two JSON
  calls, §12.3). It is bounded by the remaining upload deadline only, plus an idle
  timeout that resets on every body chunk (60 s): a trace that takes minutes to
  send succeeds, and a connection that stops moving is aborted and retried instead
  of eating the rest of the deadline. The idle timeout covers the **sending** of
  the body; once the last chunk is out it is cleared, and the wait for the store's
  answer is bounded by the deadline alone — a store that takes longer than 60 s to
  verify a large object is not a stall and is not re-PUT from byte 0. A `ready`
  `complete` must also report exactly the local byte count; `bytes` `null`,
  missing or different is `request_failed`, never defaulted to the local size.

### 12.3 Retry, expiry and verbatim refusals

One policy for all three calls, shared with `events send` (`sender.ts`:
`backoffDelay`, `retryAfter`, `attemptSignal`, `isRetryable`):

- retried: `408`, `429` (the longer of `Retry-After` and the jittered backoff),
  every `5xx`, and transport failures (including the 30 s per-request timeout and
  the PUT's idle timeout). The 30 s cap applies to `upload-url` and `complete`
  only; the PUT is bounded by the remaining deadline plus its 60 s idle timeout
  (§12.2), because a multi-GB trace legitimately takes longer to send;
- never retried: `401` (the cached token is purged, best-effort, and the server
  code surfaces with the `evidgate auth login` hint), `403`/`409`/`412`/`422`,
  `413`, and a checksum mismatch;
- a `complete` `409` is terminal for that row, and that is deliberate: the route
  answers `200` when an already-ready artifact's `complete` is replayed, so a
  `409` is a real state conflict (a different row, a closed run) rather than a
  replay the CLI could wait out. Re-running the same command therefore reports the
  same server code and never mutates the row;
- one deadline for the whole upload: `EVIDGATE_UPLOAD_DEADLINE` (same grammar as
  `EVIDGATE_SEND_DEADLINE`: bare seconds or an `s`/`m`/`h` suffix, default `30m`,
  ceiling `24h`, a bad value is `invalid_config`). A wait that would pass the
  deadline is `deadline_exceeded` and names the last error's code, HTTP status
  and `request_id` — never the URL, the file or the bearer;
- a PUT `403`/`404`/`410` means the presigned URL is dead (T18: a `pending/`
  object lives 24 h, a URL about 15 min). The row cannot be re-signed, so the CLI
  requests a fresh `upload-url` with the next `mint_no`, at most three mints per
  invocation; the abandoned row expires under `pending/` and the run's
  `upload-url` count still yields at most one _completed_ artifact per file.

### 12.4 Allowed directories

The file's **realpath** must sit under a root, and both sides are resolved with
`realpath`, so `..` and a symlink planted inside a root cannot escape it:

- the default roots, relative to the working directory: `test-results/` and
  `playwright-report/`;
- every `--dir <root>`, added to the defaults.

Only a root's **final** component is inspected: a root that is itself a symlink is
refused (`upload_path_refused`, `the upload root <name> is a symlink`) before any
file is looked at, because `realpath` would otherwise adopt its target wholesale —
a repo-planted `test-results -> $HOME` would authorise the whole home directory.
Naming the real directory with `--dir` is the fix. Ancestor aliases still resolve
normally (macOS `/var` → `/private/var`, `/tmp` → `/private/tmp`), and a root that
does not exist is skipped rather than refused (it can contain no file).

The file is then opened with `O_NOFOLLOW|O_NONBLOCK`, `fstat`ed, and its
`dev`/`ino` are compared with the resolved path, so a swap between the check and
the open is refused, not followed. A non-regular file (FIFO, device, directory),
an empty file, a file owned by another user and a file over the kind's static cap
are refused before any request — not even the token exchange runs. The caps are
the server's (`contracts/evidence/artifact-kind-defaults.v1.json`): trace 100
MiB, video 200 MiB, screenshot 10 MiB, raw_report 256 MiB; a kind without a
static default (`dom_snapshot`, `a11y_snapshot`, `patch`, `ui_hierarchy`) has no
client-side cap, so an over-cap file there is the server's 413.

The media type comes from the kind's static default matched on the extension
(`trace` `.zip`, `video` `.mp4`, `screenshot` `.png`/`.jpg`/`.jpeg`,
`raw_report` `.xml`/`.json`/`.html`/`.htm`/`.zip`); any other pair needs
`--media-type`, because the CLI cannot know the project's policy and does not
guess a type the server would refuse.

### 12.5 Journal and resume

With `EVIDGATE_SPOOL_DIR` set, one record per upload is written to
`<spool_dir>/<run_id>/<producer_id>/uploads/<identity>-<artifact_id>.json`
(§11.1), where `<identity>` is the first 16 hex of a SHA-256 over the
length-prefixed upload identity — every value the `upload-url` body is built from:

```json
{
  "artifact_id": "…",
  "file_sha256": "sha256:…",
  "size": 0,
  "kind": "trace",
  "execution_key": "…",
  "attempt_no": 1,
  "media_type": "application/zip",
  "put_done": false,
  "complete_done": false,
  "mint_no": 0
}
```

- Writes are tmp + fsync + rename + directory fsync under the producer's
  `spool.lock`, so two CLI processes never interleave; the directory is `0700`
  and a record `0600`. A symlinked `uploads/`, a planted symlink for a record and
  a record that does not parse are `spool_unsafe_path` / `spool_corrupt`.
- A busy `spool.lock` is **waited for**, not failed: `events send` holds the same
  lock for its whole run, so a journal write retries with bounded backoff until
  the upload deadline (a caller may shorten the wait; with no deadline at all it
  stops after 10 s) and re-raises the last `spool_locked` only when that wait is
  spent. The two updates after a transfer are the exception: `put_done` (after the
  PUT) and `complete_done` (after `complete`) are **best-effort** and wait at most
  a short budget (2 s) for the lock before swallowing `spool_locked` — the bytes
  are already in the store, and waiting out the deadline would leave `complete`
  with nothing. The pre-PUT `write` that records the minted row keeps the full
  deadline budget, because that record is what makes a restart resume instead of
  re-mint. A lost `complete_done` costs one repeat of the idempotent `complete`; a
  lost `put_done` costs a re-PUT of the same bytes — or a fresh mint when the
  presigned URL has expired — and never a second artifact.
- The record never carries the presigned URL, a token or a header value: those
  are bearer credentials and the journal survives the process (§6).
- Resume: the highest `mint_no` whose record matches the identity on **every**
  field (`file_sha256`, `size`, `kind`, `execution_key`, `attempt_no`,
  `media_type`) is the row to continue. A record for another execution, kind,
  attempt or media type is never resumed and never lends its `mint_no`: the
  command calls `upload-url` at mint `0` again — a new server row for the first
  three (they are key fields), and the server's `409 idempotency_conflict` for a
  changed `--media-type`, which the CLI reports verbatim. The name being digest-derived means a lookup reads the
  directory once and parses only its own candidates; a record written by an older
  CLI (a legacy `<artifact_id>.json` name, or one without the identity fields) is
  ignored rather than parsed, so it can neither crash a resume nor claim a mint.
- `put_done` means the bytes are in the store, so a restart calls only
  `complete`; `complete_done` means the artifact is committed `ready`, so a re-run
  answers from the journal with no request at all. A rejected `complete` leaves
  `complete_done` false, so a re-run asks the server again instead of reporting a
  rejected artifact as ready — and it asks about the _same_ row, so it is rejected
  again. The error's hint says so: the row stays rejected, fix the cause and
  re-run (a changed file has a new digest and mints a new row), or delete the
  record — `<spool_dir>/<run_id>/<producer_id>/uploads/<identity>-<artifact_id>.json`,
  or the whole `uploads/` directory — to start over.
  `put_done` is kept on a rejection because the bytes were stored successfully;
  only `complete` refused them.
- Without `EVIDGATE_SPOOL_DIR` the upload works exactly the same and has no
  resume state: a crash re-mints, and the deterministic key still keeps the
  server-side row count at one per file (T11/T92).
- `runs finalize` purges the whole producer directory after a successful
  finalize (§11.8), which removes `uploads/` with it; nothing else deletes a
  journal. A completed journal record is cheap (a few hundred bytes) and stays
  until that purge.

### 12.6 Output and redaction

- stdout carries only the artifact id, the state, the byte count and the base
  name; stderr carries the server's `error.code`, `request_id` and hint per §5.
- No presigned URL, signature header or bearer is ever written — not to stdout,
  not to stderr, not to the journal. Beyond the registered-secret rule (§6), a
  server message is passed through a URL rule: every `http(s)` URL with a query
  string loses the query (`…?[REDACTED]`), because a presigned URL IS a bearer
  credential and its signature travels there under whatever parameter name the
  provider uses; `scheme://user:pw@host` loses its userinfo.
- Server text that reaches a message is echoed only in a safe shape. A rejected
  `complete` names the artifact id and its `failure_code` when that is a plain
  token (`[A-Za-z0-9_.-]{1,64}`); any other value — an escape sequence, a newline
  that would forge a line, an over-long string — is replaced by the stable marker
  `invalid_failure_code`, so stderr and any `--json` copy of the message carry no
  server-controlled control characters. A signed header whose name is not an HTTP
  token is refused with a fixed message that does not echo it; a valid token
  longer than 64 characters is reported as `same`; a header value is never
  echoed (§12.2).

[DECISION] (D-S56-Q2) Decided A by the coordinator (owner rule 2026-10-23,
recommended option); recorded for owner phase review as D-S56-Q2, non-blocking:
`--execution-key` is a required flag rather than derived, because only the
manifest knows the planned execution and a wrong value would attach the artifact
to another execution server-side.
[DECISION] (D-S56-Q3) Same wording, non-blocking: `--producer` defaults to the
T54 unsharded slot `shard-1`; a sharded job must pass `--producer shard-<n>`.
[DECISION] (D-S56-Q4) Same wording, non-blocking: the `upload-url` key appends
`mint_no` only when it is not `0`, so the AC1 key is exactly the plan's
`sha256(run_id ‖ producer_id ‖ execution_key ‖ attempt_no ‖ kind ‖ file_sha256)`
and a renewal cannot replay a dead row.
[DECISION] (D-S56-Q5) Same wording, non-blocking: any query string is redacted,
the whole query rather than a named parameter list (see §12.6).
[DECISION] (D-S56-Q6) Same wording, non-blocking: a rejected `complete` does not
set `complete_done`, so a re-run re-asks the server (a rejection is replayable)
instead of answering `ready` from a record that cannot tell rejection from
success; the hint tells the operator the row stays rejected and names the journal
record to delete to start over (§12.5).
