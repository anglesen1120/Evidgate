# Playwright adapter mapping — identity (04.3.2-T1)

How `@evidgate/playwright-reporter` (04.3.1-T1) maps a Playwright run to the
identity fields of the 00 §9.4 event envelope. This file covers **identity only**
(`ext.case_id`, `ext.variant_hash`, `ext.target_env`, `ext.identity_conflict` and
the `ext.warn` channel); the status mapping (retries, `timedOut`, interrupted,
`skip` vs `fixme`) is 04.3.2-T2 and lands in the `## status` section of this file.

Sources: 00 §7 (identifier contract), 00 §9.4 (envelope), 03.1.1-T1 (annotation
and sidecar), 03.2.1-T2.1 (`VariantNormalizer`), 04.3.2 §3/§5/§6, 05.2.1-T3
(`execution_key`). Legend: ✅ verified with a link · ⚠️ unverified ·
`[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## Case identity

`ext.case_id` is the durable catalog case id (a UUID) the test belongs to. It is
resolved per test, in this order:

1. **Annotation.** A `test.annotations` entry with `type === 'evidgate.id'` and a
   `description` that parses as a UUID (any version, per 00 §7). The resolved id
   is **lower-cased** before use. An invalid description is reported once as
   `invalid_annotation` and treated as **absent** (never as an id).
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
derive the real key from them. `[DECISION]` coordinator 2026-10-07, owner to
confirm (04.3.2 §3). `[OPEN]` until 05.2.1-T3 lands, an adapter batch cannot match
a planned execution (T10 `unknown_execution_key`).

## `ext` keys this adapter emits

Only allow-listed keys reach `ext` (04 §6); everything else is dropped, and a key
whose name matches `/token|secret|password/i` is dropped and counted
(`reporter.secretKeysDropped`). The identity keys:

| Key                 | Type     | When                                                                    |
| ------------------- | -------- | ----------------------------------------------------------------------- |
| `case_id`           | `string` | the resolved case id (lower-case UUID); absent when unmapped/conflicted |
| `variant_hash`      | `string` | `sha256:<64 hex>`; absent when the params set is refused                |
| `target_env`        | `string` | the validated target env; absent when neither source is valid           |
| `identity_conflict` | `true`   | annotation and sidecar disagree; never `false`, never with `case_id`    |
| `warn`              | `object` | a warning payload on a `run.log` envelope (see below)                   |

`case_id`, `variant_hash`, `target_env` and `identity_conflict` are set on
`test.started`, `test.finished` and `test.step` envelopes; `warn` is set on
`run.log` envelopes. `ext` remains an open object that requires only `schema`
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
| `invalid_annotation`     | an `evidgate.id` annotation whose description is not a UUID; treated as absent                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| `invalid_variant_params` | the declared params were refused; names the problem paths and reasons only                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| `invalid_target_env`     | `EVIDGATE_TARGET_ENV` does not match the `targetEnv` shape (warned once, falls back to the project name); additionally one warning **per project** when the project name is invalid too (no value echoed)                                                                                                                                                                                                                                                                                                                                    |
| `invalid_sidecar`        | a sidecar manifest was refused (size, encoding, YAML shape, schema) or the 8 MiB aggregate budget was exhausted                                                                                                                                                                                                                                                                                                                                                                                                                              |
| `invalid_sidecar_entry`  | one sidecar entry was refused (path outside the directory, missing file, an `evidgate.id` that is a `urn:uuid:`-prefixed or nil UUID)                                                                                                                                                                                                                                                                                                                                                                                                        |
| `sidecar_conflict`       | two sidecars claim the same test file                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| `unmapped`               | `onEnd` summary: how many tests ended without a **complete identity** — no `case_id`, no `variant_hash` or no `target_env`, or a conflict (and how many conflicted)                                                                                                                                                                                                                                                                                                                                                                          |
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

    [evidgate] N test(s) without a complete identity (M conflicted); set EVIDGATE_ALLOW_UNMAPPED=1 to allow

Playwright awaits the reporter's `onEnd` result and overrides the run status with
it, so a run that silently lost identity fails the CI job instead of passing with
partial evidence (AC2). A buffered spool write failure still throws from `onEnd`,
as before.
