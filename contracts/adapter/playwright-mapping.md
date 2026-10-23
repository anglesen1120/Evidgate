# Playwright adapter mapping — identity and status (04.3.2-T1, 04.3.2-T2)

How `@evidgate/playwright-reporter` (04.3.1-T1) maps a Playwright run to the
identity and status fields of the 00 §9.4 event envelope. The identity part
(`ext.case_id`, `ext.variant_hash`, `ext.target_env`, `ext.identity_conflict` and
the `ext.warn` channel) is 04.3.2-T1; the status mapping (retries, `timedOut`,
interrupted, `skip` vs `fixme`, xfail, `ext.error_summary`) is 04.3.2-T2 and is
the [`## status`](#status) section below.

Sources: 00 §7 (identifier contract), 00 §8.3 (normalised status), 00 §9.4
(envelope), 03.1.1-T1 (annotation and sidecar), 03.2.1-T2.1 (`VariantNormalizer`),
04.3.2 §2 AC5–AC7/§3/§5/§6, 05.2.1-T3
(`execution_key`). Legend: ✅ verified with a link · ⚠️ unverified ·
`[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## Case identity

`ext.case_id` is the durable catalog case id (a UUID) the test belongs to. It is
resolved per test, in this order:

1. **Annotation.** A `test.annotations` entry with `type === 'evidgate.id'` and a
   `description` that is the strict canonical case id the server accepts
   (`AnnotationParser.IsCanonicalUuid`): the 36-character hyphenated UUID in any
   version and any case, **never** the nil UUID. The resolved id is **lower-cased**
   before use. Any other description — a non-UUID, a `urn:uuid:`-prefixed id or the
   nil UUID — is reported once as `invalid_annotation` and treated as **absent**
   (never as an id), exactly as the sidecar path refuses the same spellings per entry.
2. **Sidecar manifest.** `evidgate.cases.yaml` in the test file's own directory or
   in an ancestor directory up to the **common ancestor** of the suite's test files
   (03.1.1-T1, TL T55; schema `contracts/catalog/sidecar.v1.json`). The index is
   built **once** at `onBegin` from those directories and maps an absolute test-file
   path to the case id of the entry whose `file` is relative to the manifest's
   directory (`RepoPath.Combine` on the server). A manifest strictly above that
   common ancestor is not read: the reporter has no tree snapshot, so the common
   ancestor is the closest thing to the repository root it can derive.

Both present and **different** is a **conflict** (00 §7, 03.1.1 AC4): the reporter
emits `ext.identity_conflict: true`, emits **no** `ext.case_id`, and reports an
`identity_conflict` warning naming the test location. It never silently picks one
source. Both present and **equal** is mapped, with `source = 'annotation'`.

`ext.case_id` is absent when the test is unmapped (neither source) or conflicted.
The per-test state (`unmapped → mapped | conflicted`) is computed at
`onTestBegin` and cached by the test's Playwright `id`, so a conflicted test is
never re-mapped within the run (04.3.2 §6) and a retry of the same test reuses the
same identity.

The `duplicate_case_id` check keys the first use of a case id by the test's
location file plus its title path **without the project segment**, so one test
running under several Playwright projects (case × variant) and its `repeatEach`
repeats collapse to one identity. Two tests mapped by **one sidecar file** are not
a duplicate either: a sidecar maps one file to one case (the server's
`CaseDiscovery` is one case per file), so every test in that file shares the id by
design.

### Sidecar rules

The sidecar is untrusted input from a customer repository, so it is read as
**data** (04.3.2 §3, mirroring the server reader):

- Read at most **1 MiB** per file, checked **before** the content is read.
- Decoded as strict UTF-8; malformed bytes are refused (never repaired to U+FFFD,
  which could silently map a different, replacement-character filename).
- Parsed with the pinned `yaml` 2.9.1 package in the **core schema**: plain data
  only, no custom tags. Anchors, aliases, merge keys, duplicate keys and a second
  document are **refused** (the server refuses them too).
- Validated against `contracts/catalog/sidecar.v1.json` with Ajv 8 (2020-12).
- Each entry's `file` must resolve inside the manifest's own directory (no
  absolute path, no `..` segment) and name an existing file. A `file` may name a
  nested path (`sub/a.spec.ts`), so a manifest in an ancestor directory maps a test
  in a subdirectory.
- Each entry's `evidgate.id` must be the strict canonical UUID the server accepts
  (`AnnotationParser.IsCanonicalUuid`): the 36-character hyphenated form in any
  case, never the nil UUID. An id that is not a UUID at all fails the schema and
  refuses the **whole manifest** (as the server's format assertion does); a
  `urn:uuid:`-prefixed or nil id passes the schema and is refused **per entry**
  with `invalid_sidecar_entry`, so its valid siblings keep their mapping.
- Read without following a symlink (where the platform has `O_NOFOLLOW`), from a
  descriptor that must be a regular file, and at most one byte past the 1 MiB cap,
  so an oversized manifest is refused without being allocated in full.
- The bytes read for one run are capped in aggregate at **8 MiB**; past it one
  `invalid_sidecar` warning is emitted and the remaining manifests are skipped.
- A refused manifest or entry yields a warning and **no** mapping for it; every
  other directory is still processed.

## `variant_hash`

`ext.variant_hash` is the **server** variant hash of the test's declared
parameters — the same value 03.2.1-T2.1's `VariantNormalizer` produces, so a
producer-side hash and a server-side hash of the same parameters are identical
(AC3):

- **Params source:** the Playwright project's `metadata.evidgate.params` object
  (`FullProject.metadata`, user-defined). This is **explicit and opt-in**: the
  reporter never derives parameters from `use` options, the project name, the
  test title or the file path. Absent → the empty parameter set `{}`.
- **Normalisation:** exactly the merged `VariantNormalizer.Normalize` semantics
  (defaults, key sort, enum case, non-identifying drop) plus the .NET
  `CanonicalHash`: `sha256:<lowercase hex>` over the **RFC 8785** canonical JSON of
  the normalised params **only** — there is no `{project, params}` wrapper. The
  Playwright project name is hashed only if the user put it inside the declared
  params.
- **Refusal:** a params set the normaliser refuses (a secret-shaped key or value,
  a non-scalar leaf, an over-long string, a set over the 8000-byte cap) yields
  **no** `variant_hash` and one `invalid_variant_params` warning per project
  naming the problem **paths and reasons only** — a refused value may itself be
  the secret and is never echoed. A test whose params were refused therefore
  carries no `variant_hash` and counts toward the identity gate (S3, below).
- **Size cap:** the server refuses a params set whose
  `Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result))` exceeds 8000
  bytes (`VariantNormalizer.MaxParamsBytes`), measured on its own serialisation —
  the default `JavaScriptEncoder` writes every non-ASCII character, the backtick,
  and the ASCII characters `"`, `&`, `'`, `+`, `<`, `>` as a six-byte `\uXXXX`
  escape (an astral character as two escapes, twelve bytes) and `\` as `\\` — not
  on the canonical form. The reporter reproduces that measure, so a set of
  non-ASCII or HTML-sensitive text between roughly 4 KB and 8 KB is refused by
  both, where a canonical-byte-count rule would accept it.
- **Number spelling:** the reporter receives a JavaScript `number`, not the
  author's JSON text, so it cannot reproduce the server's `long`-versus-`double`
  branch (`VariantNormalizer.Scalar`). Three spellings disagree and are recorded
  as known gaps, not corpus vectors: a value in [2^53, 1e21) written with an
  exponent or a fraction (`9.1e15`, `1e16`, `1e20`) is accepted by the server and
  refused by the reporter; a value of 1e21 or more written in full digits
  (`1000000000000000000000`) is refused by the server and accepted by the
  reporter; `-0.0` against `-0` hashes the same but is one byte longer on the
  server.
- **Stability:** the hash is memoised per params object, so 5k tests of one
  project cost one hash; retries and shards of one project therefore carry the
  same `variant_hash`. Unicode titles and file paths never enter the hash.
- **Golden vectors:** `fixtures/variant-hash/vectors.json` (04.3.2-T1.3) is the
  parity corpus — the same file is asserted by the reporter's Vitest suite and by
  the server's `VariantGoldenVectorTests` (xUnit), so the two implementations
  cannot drift (T42). The `*-near-limit-*` vectors sit either side of the
  8000-byte cap on the server's serialised measure alone (below).

## `target_env`

`ext.target_env` names the environment the run targets. It is read **once** in the
reporter constructor (no `process.env` read outside it):

1. `EVIDGATE_TARGET_ENV`, trimmed. It must match the manifest `targetEnv` shape
   (`contracts/manifest/manifest.v1.json` `$defs.targetEnv`): `minLength` 1,
   `maxLength` 64, pattern `^[a-z0-9][a-z0-9_-]{0,63}$`. An invalid value is
   reported once as `invalid_target_env` and falls back to (2).
2. The Playwright project name (`test.parent.project()?.name`), validated against
   the same shape. A name that does not match is **omitted** (no `target_env`).

When **both** sources are invalid (no valid `EVIDGATE_TARGET_ENV` and an invalid
project name), the reporter emits one `invalid_target_env` warning **per project**
(memoised on the project object) and never echoes the rejected name; the test then
carries no `target_env` and counts toward the identity gate (S3, below).

No other environment variable is read for the target env.

## `execution_key`

`execution_key = sha256(case_id ‖ variant_hash ‖ target_env)` is **computed by the
server** from the manifest (00 §7); the reporter must not derive it. Until
05.2.1-T3.1 defines the byte layout and owns the wiring, the reporter emits
`PLACEHOLDER_EXECUTION_KEY` (`'0'.repeat(64)`, valid per the envelope pattern
`^[0-9a-f]{64}$`) behind its injectable `identity` resolver seam, and emits
`ext.case_id`, `ext.variant_hash` and `ext.target_env` so the ingest path can
derive the real key from them. `[DECISION]` coordinator 2026-10-07, owner confirmed
2026-10-07 (04.3.2 §3; STATUS.md "04.3.2-T1 coordinator decisions, owner confirmed"). `[OPEN]` until 05.2.1-T3 lands, an adapter batch cannot match
a planned execution (T10 `unknown_execution_key`).

## status

Each Playwright attempt (`onTestEnd`) becomes one `test.finished` envelope. The
raw `TestResult.status` is **never overwritten**: it travels in the envelope
`status`; the 00 §8.3 normalised value goes to `ext.normalized` (FR-04-6). The
mapping is a pure, table-driven function (`mapStatus` in
`packages/adapter-playwright/src/status.ts`).

✅ Playwright 1.63 types (`node_modules/playwright/types/testReporter.d.ts`,
`"version": "1.63.0"`): `TestResult.status` is
`"passed"|"failed"|"timedOut"|"skipped"|"interrupted"`, `TestResult.retry` is the
0-based retry, `TestResult.annotations` and `TestCase.annotations` are
`Array<{type, description?, …}>`, `TestResult.errors` is `Array<TestError>`
(`message?`, `value?`, `stack?`), `TestCase.expectedStatus` uses the same five values.

### Status table (AC6)

| Raw `status`  | `expectedStatus` | Annotations                      | `ext.normalized` | Other `ext`                        |
| ------------- | ---------------- | -------------------------------- | ---------------- | ---------------------------------- |
| `passed`      | not `failed`     | any                              | `passed`         | —                                  |
| `failed`      | not `failed`     | any                              | `failed`         | —                                  |
| `timedOut`    | any              | any                              | `timed_out`      | —                                  |
| `interrupted` | any              | any                              | `error`          | —                                  |
| `skipped`     | any              | `skip`, or none                  | `skipped`        | —                                  |
| `skipped`     | any              | `fixme` (with or without `skip`) | `skipped`        | `skip_reason: "fixme"`             |
| `failed`      | `failed`         | any                              | `passed`         | `xfail: true`                      |
| `passed`      | `failed`         | any                              | `failed`         | `xfail: true` (`[DECISION]` below) |
| anything else | any              | any                              | `error`          | + one `unknown_status` warning     |

- **`interrupted` vs `timedOut`** stay distinct in both fields: `interrupted` →
  `error` (the run was stopped, the attempt has no verdict), `timedOut` →
  `timed_out`.
- **xfail** applies only to the two rows above. `expectedStatus: "failed"` with a
  `timedOut`, `interrupted` or `skipped` outcome maps by the plain row, without
  `xfail`.
- `[DECISION]` 04.3.2-T2 (orchestrator, S18), owner confirmed 2026-10-10: an
  **unexpected pass** (`expectedStatus: "failed"`, raw `passed`) maps to `failed` +
  `xfail: true` — fail closed, an unexpected pass is never evidence of a PASS.
  Playwright itself reports it as an unexpected outcome.
- **`skip` vs `fixme`:** `skip_reason: "fixme"` is set when any annotation has
  `type: "fixme"`, so `fixme` wins over `skip`; a `skip` annotation or none leaves
  `skip_reason` absent. `skip_reason` is set **only** when the raw status is
  `skipped` — a `fixme` annotation on a `failed` attempt sets nothing.
- **Annotation source:** `test.annotations` followed by `result.annotations`. A
  `fixme`/`skip` added at runtime on a retry (only in that attempt's
  `result.annotations`) therefore counts for that attempt and not for earlier ones.
- **Raw status length:** the envelope `status` is the raw value verbatim; only a
  value longer than 64 UTF-16 units (the schema `maxLength`) is cut to 64, never
  ending on a lone high surrogate (then 63).

### Retries and `attempt_no` (AC5, AC7)

`attempt_no = result.retry + 1` (`attemptOf`) on both `test.started` and
`test.finished`. Every attempt is its own event pair; statuses are never merged. A
flaky test (retry 0 failed, retry 1 passed) yields `attempt_no` 1 `failed` +
`attempt_no` 2 `passed`. A `fixme`/`skip` annotation that appears only on a retry
does not change the numbering: `attempt_no` stays contiguous `1..n`, no gap.

`[DECISION]` 04.3.2-T2 (S18), owner confirmed 2026-10-10: `attempt`, listed among
the plan §3 `ext` additions, is **not** an `ext` key: the envelope's own
`attempt_no` carries the attempt, and a second copy could disagree.

### Unknown status

A raw status that is not an own key of the table (a future Playwright value, an
empty string, a different case such as `PASSED`, or a prototype name such as
`constructor`) maps to `ext.normalized: "error"`, keeps its raw value in `status`,
and emits an `unknown_status` warning (see Warning channel) naming the raw status
(≤ 64 chars, made printable) and the test `file:line:column`. The warning is sent
**once per distinct raw status** per run, so a future status shared by many tests
cannot use up the capped warning budget that identity warnings need; every attempt
still carries `ext.normalized: "error"`. The server marks an unknown status
incomplete (00 §8.5); the reporter never guesses a mapping.

### `error_summary`

`ext.error_summary` is the first line of the first `TestResult.errors` entry whose
`message` (else `value`) has text:

- At most the first 4096 UTF-16 units of the message are read, so a huge message
  costs a bounded scan.
- ANSI escape sequences (CSI colours, OSC hyperlinks) are stripped, then the text is
  cut at the first line terminator (`\r\n`, `\n`, `\r`, NEL, U+2028, U+2029) and only
  the **first line** is kept.
- Remaining control characters (lone ESC, C1, NUL, BEL) and format characters (BIDI
  overrides, zero-width marks) are removed, tabs become spaces, and the line is
  trimmed.
- URL userinfo and the query/fragment are redacted (`https://[redacted]@host/path?[redacted]`):
  Playwright's own first lines embed the call URL (`page.goto: … at <url>`), the
  value class the PS1 rule already withholds from `pw:api` step titles. Scheme, host
  and path stay. Redaction runs over the whole first line before the cap, so the cap
  never leaves a credential prefix behind.
- Errors whose first line is empty are skipped; `stack` is never read, so no stack
  frame and no later line ever reaches `ext`.
- Capped at **256** UTF-16 units (`ERROR_SUMMARY_MAX`), never ending on a lone high
  surrogate.
- Absent when no error carries text. Present for any status that carries error
  text, including an xfail `passed`.

Residual risk `[DECISION]` (S18 security review SEC-2), owner confirmed 2026-10-10: any other value a test author
writes into the first line of an error (a thrown string, an `expect(…, message)`
text, an assertion echoing a received value) is carried as written. T2.1 requires
`ext.error_summary`; the first-line cap, the 256-unit cap and the URL redaction
bound it, and no value-pattern redaction is attempted.

The raw `errors`, `error` and `stack` members never reach `ext` (not allow-listed).

## `ext` keys this adapter emits

Only allow-listed keys reach `ext` (04 §6); everything else is dropped, and a key
whose name matches `/token|secret|password/i` is dropped and counted
(`reporter.secretKeysDropped`). The identity and status keys:

| Key                 | Type      | When                                                                       |
| ------------------- | --------- | -------------------------------------------------------------------------- |
| `case_id`           | `string`  | the resolved case id (lower-case UUID); absent when unmapped/conflicted    |
| `variant_hash`      | `string`  | `sha256:<64 hex>`; absent when the params set is refused                   |
| `target_env`        | `string`  | the validated target env; absent when neither source is valid              |
| `identity_conflict` | `true`    | annotation and sidecar disagree; never `false`, never with `case_id`       |
| `warn`              | `object`  | a warning payload on a `run.log` envelope (see below)                      |
| `normalized`        | `string`  | 00 §8.3 normalised status (§status); `test.finished` only                  |
| `skip_reason`       | `"fixme"` | raw `skipped` with a `fixme` annotation; `test.finished` only              |
| `xfail`             | `true`    | `expectedStatus: "failed"` with a `failed`/`passed` outcome; never `false` |
| `error_summary`     | `string`  | first error line, ≤ 256 UTF-16 units, no stack; `test.finished` only       |

`case_id`, `variant_hash`, `target_env` and `identity_conflict` are set on
`test.started`, `test.finished` and `test.step` envelopes; `normalized`,
`skip_reason`, `xfail` and `error_summary` only on `test.finished` (never on
`test.started`, even when the `TestResult` has same-named own members); `warn` is
set on `run.log` envelopes. `ext` remains an open object that requires only `schema`
(`playwright/1`), so these keys need no envelope-schema change; every emitted
envelope still validates against `contracts/schemas/event-envelope.v1.json`.

## Warning channel

Warnings are **data**: each is a `run.log` envelope whose `ext.warn` carries
`{ code, message, path?, count? }`. A message names the offending path or key and
never echoes file content or a parameter value.

| `code`                   | Meaning                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| ------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `duplicate_case_id`      | the same `case_id` resolved for two different tests; one warning naming both `file:line:column` locations (with the test titles when the two tests share one location), emitted when the second test is seen. "Different" is by location file + title path **without the project segment**, so one test under several Playwright projects (case × variant) or `repeatEach` repeats is one test, not a duplicate; two tests mapped by the same sidecar file are not a duplicate either (one file → one case, as the server's `CaseDiscovery`) |
| `identity_conflict`      | annotation and sidecar disagree (or two different `evidgate.id` annotations); names the test location                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| `invalid_annotation`     | an `evidgate.id` annotation whose description is not a canonical case id (a non-UUID, a `urn:uuid:`-prefixed id or the nil UUID); treated as absent                                                                                                                                                                                                                                                                                                                                                                                          |
| `invalid_variant_params` | the declared params were refused; names the problem paths and reasons only                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| `invalid_target_env`     | `EVIDGATE_TARGET_ENV` does not match the `targetEnv` shape (warned once, falls back to the project name); additionally one warning **per project** when the project name is invalid too (no value echoed)                                                                                                                                                                                                                                                                                                                                    |
| `invalid_sidecar`        | a sidecar manifest was refused (size, encoding, YAML shape, schema) or the 8 MiB aggregate budget was exhausted                                                                                                                                                                                                                                                                                                                                                                                                                              |
| `invalid_sidecar_entry`  | one sidecar entry was refused (path outside the directory, missing file, an `evidgate.id` that is a `urn:uuid:`-prefixed or nil UUID)                                                                                                                                                                                                                                                                                                                                                                                                        |
| `sidecar_conflict`       | two sidecars claim the same test file                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| `unknown_status`         | a `TestResult.status` the status table does not know, once per distinct raw status per run; the message names the raw status (≤ 64 chars, made printable) and the test `file:line:column` of its first occurrence, `path` is that location; every attempt with it maps to `error` (§status)                                                                                                                                                                                                                                                  |
| `unmapped`               | `onEnd` summary: how many tests ended without a **complete identity** — no `case_id`, no `variant_hash` or no `target_env`, or a conflict — with the per-cause breakdown in the message (`conflicted`, `no case_id`, `no variant_hash`, `no target_env`; counted independently, so one test can appear in several)                                                                                                                                                                                                                           |
| `warnings_truncated`     | `onEnd` summary: how many warnings the reporter's caps suppressed — the 100-envelope cap plus the sidecar loader's own warning cap                                                                                                                                                                                                                                                                                                                                                                                                           |

Sidecar warnings are emitted at `onBegin` (after the capability and `run_begin`
markers); the rest as they occur. At most **100** warning envelopes are emitted
per run (`MAX_WARNING_ENVELOPES`); the overflow is counted and summarised by one
`warnings_truncated` envelope at `onEnd`, so a hostile suite cannot flood the
spool. The two `onEnd` summaries (`unmapped`, `warnings_truncated`) bypass the
cap, so the counts that explain a failed run always reach the spool. The sidecar
loader's own 2000-warning cap (`MAX_WARNINGS`) is counted the same way: its
overflow is added to the `warnings_truncated` total.

## Exit code

`onEnd` returns `{ status: 'failed' }` when any test lacked a **complete
identity** — no `case_id` (unmapped or conflicted), no `variant_hash` (the params
were refused) or no `target_env` (neither source is valid) — unless
`EVIDGATE_ALLOW_UNMAPPED=1` (read once in the constructor). The override applies
only to a `passed` run: an `interrupted` or `timedout` run keeps its own status.
When the gate trips, `onEnd` also writes one line to **stderr** so the CI log
explains the failure:

    [evidgate] N test(s) without a complete identity (C conflicted, C no case_id, C no variant_hash, C no target_env); set EVIDGATE_ALLOW_UNMAPPED=1 to allow

The leading `N` counts each test once; the four per-cause counts are independent,
so one test can appear in several of them (a conflicted test has no `case_id` and
is counted in both `conflicted` and `no case_id`). The same breakdown is carried in
the `unmapped` summary envelope's `message`, so the spool explains the failure too.
The most common cause is a Playwright project name that is not a valid target env
(the default unnamed project, or a name like `Mobile Safari`), which
`EVIDGATE_TARGET_ENV` fixes.

An injected `identity` resolver (the reporter's `identity` option, which wins over
the built-in mapping) is held to the **same completeness gate**: every identity it
returns must carry `case_id`, `variant_hash` and `target_env`, or the run fails at
`onEnd` unless `EVIDGATE_ALLOW_UNMAPPED=1`. A custom resolver that returns only
`execution_key` and `case_id` therefore fails every passed run.

Playwright awaits the reporter's `onEnd` result and overrides the run status with
it, so a run that silently lost identity fails the CI job instead of passing with
partial evidence (AC2). A buffered spool write failure still throws from `onEnd`,
as before.

## shards (04.3.3-T1)

Each Playwright shard is its own producer with its own contiguous sequence (T54, T84).
The identity is derived, never generated: a restarted shard reuses the same id.
Index base and slot naming are **D-S56-Q10** (1-based, so slot `i` of the manifest is
producer `shard-i`; an unsharded run is `shard-1`).

### Verified Playwright 1.63.0 surface

All of the following was read from the installed `@playwright/test@1.63.0`
(`node_modules/playwright`), not assumed:

| Fact                                                                                                                                          | Source                                                                        |
| --------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| `FullConfig.shard` is `null` or `{ current, total }`, and `current` is **one-based**                                                          | `types/test.d.ts:1675,1691-1698`; default `null` at `lib/runner/index.js:581` |
| `--shard <current>/<total>` CLI flag, one-based, e.g. `3/5`                                                                                   | `lib/program.js:216`                                                          |
| **No `PLAYWRIGHT_SHARD` environment variable** exists (nothing in `lib/` or `types/` reads it)                                                | grep of `node_modules/playwright/{lib,types}`: no match                       |
| Blob report file name: `report[-<commandHash>]-<current>.zip`, `current` zero-padded to the width of `total`; unsharded is plain `report.zip` | `lib/runner/index.js:3151-3161` (`_defaultReportName`)                        |
| `merge-reports` is a CLI subcommand (`playwright merge-reports [dir]`)                                                                        | `lib/program.js:108-109`                                                      |
| The merged config has **`shard: null`** — `mergeConfigs` overwrites it                                                                        | `lib/runner/index.js:8211-8219`                                               |
| A custom reporter is wrapped in `ReporterV2Wrapper`, which has no `onReportConfigure`, so the per-blob shard config is not visible to it      | `lib/runner/index.js:1703-1715`, dispatched at `:8012`                        |

The area plan's `[WARN]` ("confirm the `--shard` env surface") resolves to **`[OK]`**:
the CLI/config surface is `FullConfig.shard`, there is no environment variable, and the
blob filename carries the same one-based index the config does. This adapter reads
`config.shard` only; it never parses a blob filename or an env var.

### `producer_id` naming

`resolveProducerId(config)` returns `{ producer_id, shard_index, shard_total }`:

| Run                                 | `producer_id`                  | `shards.json`                                                              |
| ----------------------------------- | ------------------------------ | -------------------------------------------------------------------------- |
| Unsharded (`config.shard === null`) | `shard-1`                      | `{ shard_index: 1 }`, no `shard_total` (the manifest owns the shard count) |
| Sharded                             | `shard-<config.shard.current>` | `{ shard_index: current, shard_total: total }`                             |

- The index is **1-based**, matching `FullConfig.shard.current` and therefore the
  manifest's `producer_slot` rows (T9): slot `i` ↔ producer `shard-i` (D-S56-Q10).
- The id must match the spool-safe pattern `^[A-Za-z0-9._-]{1,128}$`; a derived id always
  does, and an explicit override that does not is refused before any envelope.
- The reporter option `producer_id` still wins, but **only** if it equals the derived id
  when Playwright selected a shard. A disagreement is a hard refusal (buffered from
  `onBegin`, thrown from `onEnd`, zero envelopes written): the server would answer 422
  `unknown_producer`, or two shards would interleave one sequence. Without a shard config
  an explicit id is honoured as before, and no shard slot is claimed for it.
  (D-S56-Q12; the negative case is `packages/adapter-playwright/src/shard.test.ts:272`.)
- No suffix, generation counter or host name is ever appended (T84).

### Restart (T84)

A restarted shard process derives the same `producer_id`, opens the same
`<spool>/<run>/<producer_id>/` directory and resumes from the store: the chunk scan is
authoritative, `shards.json` is the checkpoint. `sequence` continues at
`sequence_next` — it never restarts at 1 and never repeats a `(run, producer, sequence)`
tuple. An unknown producer is a server-side 422 `unknown_producer` (the id is not in the
manifest); the reporter never invents an id outside `shard-<index>`.

`shards.json` (per-producer, written at rotation, close and restart detection only):

| Field              | Meaning                                                                                                                                                                                                                                                                                                                                                                                                |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `producer_id`      | The deterministic id, verbatim                                                                                                                                                                                                                                                                                                                                                                         |
| `shard_index?`     | The 1-based manifest slot this producer stands for; absent only when an explicit custom `producer_id` was honoured on a run without a shard config, because then no slot is claimed                                                                                                                                                                                                                    |
| `shard_total?`     | Present only when Playwright declared a shard count                                                                                                                                                                                                                                                                                                                                                    |
| `sequence_next`    | The sequence after the last line that reached a chunk (`durableNextSequence`), never advanced to cover envelopes still buffered: a rotation happens mid-batch, so a record ahead of the chunks would leave a hole after a crash that the CLI reader refuses (`spool_corrupt`). A value already on disk that is ahead of the chunks is kept as a floor on resume and is never raised further (D-S56-Q9) |
| `last_restart_at?` | Set on **any** restart that finds prior durable state — a chunk, `shards.json` or a legacy `checkpoint.json` — not only when a dead lock was taken over                                                                                                                                                                                                                                                |

### Merge mode (AC3)

`playwright merge-reports` replays every blob through **one** reporter instance, with a
merged config whose `shard` is `null`, and gives the custom reporter no per-event shard
identity (the per-blob config reaches only built-in reporters, see the table above).
Re-emitting there would have to guess an id and would duplicate or renumber sequences, so
the reporter **emits nothing** in a merge replay and says so once on stderr (D-S56-Q11).
Each shard's originals are already correct, which is exactly "keep each event's original
shard identity".

Detection is the CLI argument list (`isMergeReplay`, `lib/program.js:106-119`): the first
token that is not a flag is the subcommand. That is the only valid form — `-c/--config`,
`--reporter` and `--output` are options of the subcommands themselves, not of the root
program — so no option-value skipping is needed, and a value that happens to be named
`merge-reports` cannot hide a real replay. It is deliberately argv- and not status-based: an ordinary run may also have
`config.shard === null` (that is the unsharded `shard-1` case above) and must still emit.
The replay _does_ carry each blob's original config with `shard` serialised
(`lib/runner/index.js:2931`) and dispatches it per blob through `onReportConfigure`
(`:8011-8015`), but that hook only reaches a reporter that declares the **v2** protocol:
`wrapReporterAsV2` returns such a reporter unchanged, and `ReporterV2Wrapper` — the wrapper
every v1 reporter gets — has no `onReportConfigure` method at all (`:1699-1774`). This
adapter is a v1 reporter (`onBegin(config, suite)`), so the per-blob shard config is not
reachable and the refusal stands. A v2 reporter built later could read it there instead.

#### Known limitation (D-S56-Q11)

The refusal above is a limitation, not a feature: **the reporter cannot attribute merged
events to shards**, so a `playwright merge-reports` replay produces no event evidence at
all (one stderr line only). A v1 reporter never sees `onReportConfigure`, because
`ReporterV2Wrapper` has no such method (`lib/runner/index.js:1705-1774`), and guessing a
per-blob id would renumber or duplicate sequences. Customers ingest **per-shard** runs,
which preserves each shard's original `producer_id`/`sequence`; the merged-report path is
unusable for event production until a v2 reporter exists. Receiving task:
`04.6.1-T2` (dogfood fixture "one shard-merged set",
`plans/04-ingestion-adapters.md:241`), which must ingest the merged set per shard or drive
a v2 reporter — the note also sits in
`plans/features/04-ingestion-adapters/04.6.1-per-producer-golden-set.md`.

### Sidecar lookup root

The sidecar root is pinned to `FullConfig.rootDir` (04.3.3-T1, Note from #32), so every
shard searches the same set of `evidgate.cases.yaml` files and resolves the same
`case_id` for a test whatever test-file subset it runs. Without the pin, the root was the
common ancestor of the run's own test files, which differs per shard. A test file outside
the root contributes only its own directory; nothing above the root is read, and the
traversal/symlink hardening is unchanged. Without a Playwright config (tools, tests) the
historical common-ancestor derivation still applies.
