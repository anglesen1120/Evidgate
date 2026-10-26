# @evidgate/playwright-reporter

The Evidgate Playwright reporter (04.3.1-T1). It implements the documented Playwright
reporter API (`onBegin`, `onTestBegin`, `onTestEnd`, `onEnd`), writes one 00 §9.4
event-envelope v1 line per callback into the CLI spool, declares its capability document
as the first spooled envelope, and refuses to run against an unsupported
`@playwright/test` version.

## Usage

```ts
// playwright.config.ts
import { defineConfig } from '@playwright/test';

export default defineConfig({
  reporter: [
    [
      '@evidgate/playwright-reporter',
      {
        spool_dir: process.env.EVIDGATE_SPOOL_DIR,
        run_id: process.env.EVIDGATE_RUN_ID,
        producer_id: 'pw-shard-0',
      },
    ],
  ],
});
```

`spool_dir`, `run_id` and `producer_id` are all required: the example reads `run_id`
from `EVIDGATE_RUN_ID` and sets `producer_id` explicitly. AC1's literal
`{ spool_dir }` is not enough on its own — `run_id` (or `EVIDGATE_RUN_ID`) and
`producer_id` must be supplied too, because `producer_id` has no env fallback (decision 6).

The reporter returns `printsToStdio() === false`, so Playwright still adds its default
console reporter: a run that lists only this reporter is not silent (it prints the
line/dot progress and the run summary).

Playwright loads a reporter module and uses its **default export** as the reporter class
(`node_modules/playwright/lib/runner/index.js` → `requireOrImportDefaultFunction`), so
`EvidgateReporter` is the package's default export.

## Options

| Option                         | Type                       | Default                 | Meaning                                                                                                        |
| ------------------------------ | -------------------------- | ----------------------- | -------------------------------------------------------------------------------------------------------------- |
| `spool_dir`                    | `string`                   | —                       | Spool root. Falls back to `EVIDGATE_SPOOL_DIR`.                                                                |
| `run_id`                       | `string`                   | —                       | Run id. Falls back to `EVIDGATE_RUN_ID`.                                                                       |
| `producer_id`                  | `string`                   | —                       | Producer id. **Required** (see below).                                                                         |
| `steps`                        | `boolean`                  | `true`                  | Emit `test.step` envelopes for `result.steps`; sets `supports.step_events`.                                    |
| `mode`                         | `'full' \| 'ingest_only'`  | `'full'`                | Capability `mode`.                                                                                             |
| `identity`                     | `(test) => Identity`       | —                       | Execution-identity resolver; overrides the built-in mapping (see below).                                       |
| `sidecarFs`                    | `SidecarFs`                | —                       | Test-only seam: injects the sidecar filesystem reader.                                                         |
| `engineVersion`                | `string`                   | —                       | Test-only seam: overrides the detected `@playwright/test` version.                                             |
| `attachment_dirs`              | `string[]`                 | `[]`                    | Extra allow-listed attachment directories (see Attachments). Falls back to `EVIDGATE_ATTACHMENT_DIRS`.         |
| `attachment_drain_deadline_ms` | `number`                   | `120000`                | How long `onEnd` waits for attachment uploads before it abandons them (see Attachments).                       |
| `importCli`                    | `() => Promise<CliModule>` | the real dynamic import | Test-only seam: injects the CLI module loader `attach.ts` uses (`CliModule = typeof import('@evidgate/cli')`). |

### Construction is fail-fast

Construction throws (before any callback runs) for:

- a missing `spool_dir`, `run_id` or `producer_id` option;
- an invalid `run_id`/`producer_id` (`EvidgateInvalidSpoolIdError`);
- an unsupported `@playwright/test` version (`EvidgateUnsupportedEngineError`, AC5);
- **spool initialisation**: `mkdir` or opening the chunk or `shards.json` under `spool_dir`
  fails (unwritable, read-only or unmountable `spool_dir`, `EACCES`, `ENOSPC`). The
  reporter fails the Playwright startup rather than running with no spool.

After construction the callbacks never throw: a spool **write** failure is buffered and
re-thrown from `onEnd`, so the Playwright run reports the reporter failure instead of
losing evidence silently.

### `producer_id` has no env fallback

`producer_id` is a required option. `EVIDGATE_PRODUCER_ID` is only floated in
`plans/open-items/02-catalog-ingest-runs-evidence.md` (OI-04-02, for 04.3.3) and is not
defined in `contracts/` or the plans, so the reporter does not read it. 04.3.3-T1 decides
the value (one producer per shard).

## Environment

| Variable                   | Used for                                                                                |
| -------------------------- | --------------------------------------------------------------------------------------- |
| `EVIDGATE_SPOOL_DIR`       | `spool_dir` fallback                                                                    |
| `EVIDGATE_RUN_ID`          | `run_id` fallback                                                                       |
| `EVIDGATE_TARGET_ENV`      | `ext.target_env`; validated in the ctor, else the Playwright project name               |
| `EVIDGATE_ALLOW_UNMAPPED`  | `'1'` keeps a run without a complete identity from failing the CI job (AC2)             |
| `EVIDGATE_ATTACHMENT_DIRS` | extra allow-listed attachment directories, path-delimiter separated (`attachment_dirs`) |

Every variable above is read **once in the constructor**; no callback reads `process.env`.

Attachment uploads additionally use the **CLI's own** environment surface — the one
`evidgate artifacts upload` resolves — because the upload is the CLI's: `EVIDGATE_API_URL`,
`EVIDGATE_PROJECT` and one token source (`EVIDGATE_OIDC_TOKEN`, the GitHub Actions pair
`ACTIONS_ID_TOKEN_REQUEST_URL` + `ACTIONS_ID_TOKEN_REQUEST_TOKEN`, `EVIDGATE_INGEST_TOKEN`
or `EVIDGATE_DEV_TOKEN`), plus `EVIDGATE_SPOOL_DIR` when you want the uploads to have a
resume journal. All three of the url, the project and a token source are required: a missing
one is the CLI's own `missing_config`, reported as `attachments_unconfigured`. That surface
is resolved when the uploader is built, i.e. at the first attachment that is actually
uploadable — not in the constructor (see
[Attachments](#attachments-0433-t2)).

## Spool layout

```
<spool_dir>/<run_id>/<producer_id>/000001.jsonl   # 6-digit zero-padded chunks
<spool_dir>/<run_id>/<producer_id>/shards.json     # { producer_id, sequence_next, ... }, a floor
<spool_dir>/<run_id>/<producer_id>/writer.lock     # single-writer lock, removed on close
```

- `run_id` and `producer_id` are joined into the path, so each must be a single path
  segment matching `^[A-Za-z0-9._-]+$`: non-empty, not `.` or `..`, no `/`, `\`, `:`,
  NUL or control character, and at most 128 characters (the envelope `producer_id`
  `maxLength`). An invalid id throws `EvidgateInvalidSpoolIdError` at construction,
  before any filesystem write.
- Directory mode `0700`, file mode `0600`. The `<run_id>` and `<producer_id>` directories
  are `lstat`ed: a symlink or a foreign-owned directory is rejected, and an owned one is
  `chmod`ed to `0700`. `spool_dir` itself is left as given (a symlinked spool root is
  allowed and keeps its own mode).
- **Symlink- and mode-hardened files.** The `shards.json` temp file is unlinked if present
  and re-created with `O_WRONLY|O_CREAT|O_EXCL|O_NOFOLLOW` `0600`; chunks open with
  `O_WRONLY|O_APPEND|O_CREAT|O_NOFOLLOW`; torn-tail truncation opens with `O_NOFOLLOW`.
  After every open the writer `fstat`s the descriptor and rejects non-regular files or
  files not owned by the current uid, then `fchmod`s it to `0600` — a pre-existing `0644`
  chunk becomes `0600`, and a planted symlink is refused instead of followed.
- Envelopes are appended per callback (O(1), no whole-suite buffering). A flush writes
  the pending batch and fsyncs the chunk. `shards.json` is rewritten atomically
  (temp file → fsync → rename → directory fsync) only at chunk rotation, at close and
  when a restart is detected, never per flush; a `checkpoint.json` left by an older
  writer is read as a fallback. Restart resumes at the larger of the
  `shards.json` `sequence_next` and the last chunk sequence plus one. `EVIDGATE_SPOOL_MAX_BYTES` caps the run directory at
  write time. Contract: `contracts/adapter/playwright-mapping.md` "## shards".
- **One durable flush per callback batch.** `onBegin` and `onTestEnd` each flush at the
  end of their batch, so a killed run loses at most the current callback's envelopes
  instead of the whole suite; `onEnd` flushes the final `run_end` marker and closes. The
  64-envelope `flushThreshold` remains an upper bound on how many envelopes a single
  flush carries. On restart the writer reconciles `sequence_next` against the highest
  sequence already on disk — the last complete parseable line of the highest chunk,
  scanning backwards, then lower chunks — so a sequence is never reused (T84). A torn
  tail (a crash mid-write leaves a partial line with no trailing newline) is truncated
  back to the last newline and fsynced before the first append, so the JSONL stays
  line-delimited for the reader.
- **Short/failed writes never leave a torn line.** `writeSync` is looped until the whole
  line is written; on any write error the chunk is `ftruncate`d back to the last complete
  line before the error is rethrown, and the unwritten envelopes stay queued for a later
  flush instead of being dropped.
- **`close()` always releases the descriptor.** The final flush runs inside `try/finally`,
  so the chunk fd is closed and the writer marked closed even when the flush throws — the
  error still propagates.
- A `shards.json` `sequence_next` (or a legacy checkpoint `next_sequence`) is only honoured as a floor when it is a safe integer ≥ 1; the highest
  chunk is read once on restart and the buffer is shared by torn-tail truncation,
  sequence recovery and chunk open.
- **Chunk rotation:** a chunk rotates when it reaches 1 MiB (`chunkMaxBytes`) or 1000
  lines (`chunkMaxLines`), whichever comes first; a batch larger than the limit is split
  across chunks.
- The reporter deletes nothing; the CLI spooler (04.2.3-T1) consumes the files.
- The writer's own once-per-run notices reach the same `run.log` warning channel:
  `shards_write_failed` (a `shards.json` write failed at rotation; the flush itself still
  succeeded, and `close()` rethrows) and `shards_floor_ahead` (a `shards.json` floor ahead of
  the chunks was read at startup, which `events send` would refuse as `spool_corrupt`).

## Envelopes

Every envelope is 00 §9.4 v1 with `ext.schema = "playwright/1"`
(`<adapter-name>/<protocol-major>`). The reporter emits only the five documented event
types; it never invents another.

| Callback      | Envelope(s)                                                                                                         |
| ------------- | ------------------------------------------------------------------------------------------------------------------- |
| `onBegin`     | `run.log` capability carrier (sequence 1) + `run.log` `run_begin`; flushes the batch                                |
| `onTestBegin` | `test.started` (no `status`)                                                                                        |
| `onTestEnd`   | `test.finished` (`status` = raw `result.status`) + one `test.step` per step when `steps` is true; flushes the batch |
| `onEnd`       | `run.log` `run_end` (`status` = run status) + flush + `shards.json`                                                 |

`attempt_no = result.retry + 1`, one `test.finished` per attempt (never a merged status).
Callbacks never throw: a spool write failure is buffered
and re-thrown from `onEnd`, so the Playwright run reports the reporter failure instead of
losing evidence silently. Every `test.started`/`test.finished`/`test.step` envelope also
carries the resolved identity (`ext.case_id`, `ext.variant_hash`, `ext.target_env`,
`ext.identity_conflict` — see Identity below); every `test.finished` also carries the
mapped status (`ext.normalized`, `ext.skip_reason`, `ext.xfail`, `ext.error_summary` — see
Status below).

A `test.finished` for a test whose attachments uploaded also carries the envelope member
`artifact_ids` (`[uuid]`, the schema's own member — not an `ext` key): the ids
`upload-url`/`complete` returned for this attempt. It is omitted when nothing uploaded —
see [Attachments](#attachments-0433-t2).

### `duration_ms`

The envelope schema requires `duration_ms` to be an integer ≥ 0. The reporter sets it
only when the value is a non-negative finite number, and rounds a non-integer with
`Math.round`; otherwise the member is omitted. Playwright initialises an unfinished
step's `duration` to `-1`, so a step still open when the test ends (timeout, worker
crash, interrupt) is emitted without `duration_ms` instead of a schema-invalid `-1`.

### Step titles

Playwright's own step titles embed call parameters — `pw:api` renders `Fill "{value}"`
and a `goto` renders the URL with its query string — so `title` is forwarded only for
user-authored step categories (`test.step`, `hook`, `fixture`). For `pw:api`, `expect`
and any unknown category the `title` is dropped; `category` and `location` still identify
the step. A filled password or a URL token therefore never reaches the spool.

### Capability carrier

The first spooled envelope (sequence 1) is a `run.log` envelope carrying the capability
document under `ext`:

```json
{
  "schema": "playwright/1",
  "capability": {
    "protocol": { "major": 1, "minor": 0 },
    "engine": { "family": "playwright", "version": "1.63.0" },
    "platforms": ["darwin-arm64"],
    "supports": {
      "discovery": false,
      "step_events": true,
      "retry": true,
      "cancel": false,
      "capture_control": false
    },
    "mode": "full"
  }
}
```

`ext` is an open object that requires only `schema`, so this is allowed. The document
validates against `contracts/adapter/capability.v1.json`; `engine.version` is the
installed `@playwright/test` version read at runtime (D8). `platforms` is the platform
this reporter is running on (`<process.platform>-<process.arch>`).

`supports.capture_control: false` (04.3.3-T2) is the honest declaration: per
`contracts/adapter/README.md`, an **absent** flag means the run-create configuration
document (`contracts/adapter/config.v1.json`, T97) is delivered and must be honoured, and
this reporter does not read capture flags, redaction patterns or per-kind size caps from it
yet. Declaring `false` makes the server label the runs it produces `capture_uncontrolled`,
so silent non-conformance is visible on the run. It is a declared boolean of the closed
`supports` object, not a new schema member.

### `ext` allow-list

Only declared keys reach `ext`; everything else is dropped, and a key whose name matches
`/token|secret|password/i` is dropped and counted (`reporter.secretKeysDropped`):

| Key                 | Source                                                                              |
| ------------------- | ----------------------------------------------------------------------------------- |
| `schema`            | always present; required by the envelope schema                                     |
| `title`             | `test.title`; `step.title` only for user-authored step categories (see Step titles) |
| `file`              | `test.location.file`                                                                |
| `location`          | `test.location` / `step.location` `{file, line, column}`                            |
| `project`           | `test.parent.project()?.name`                                                       |
| `engine`            | the `@playwright/test` version                                                      |
| `marker`            | `run.log` marker: `run_begin` \| `run_end`                                          |
| `capability`        | the capability document carrier                                                     |
| `category`          | `step.category`                                                                     |
| `case_id`           | the resolved catalog case id (04.3.2-T1.2)                                          |
| `variant_hash`      | `sha256:<hex>` over the canonical normalised params (04.3.2-T1.3)                   |
| `target_env`        | `EVIDGATE_TARGET_ENV`, else the Playwright project name (04.3.2-T1.4)               |
| `identity_conflict` | `true` when annotation and sidecar disagree (04.3.2 AC2)                            |
| `warn`              | a warning payload on a `run.log` envelope (04.3.2 §5)                               |
| `normalized`        | 00 §8.3 normalised status of a `test.finished` (04.3.2-T2, mapping doc §status)     |
| `skip_reason`       | `"fixme"` on a skipped attempt with a `fixme` annotation (04.3.2 AC6)               |
| `xfail`             | `true` when `expectedStatus` is `failed` and the outcome is `failed`/`passed` (AC6) |
| `error_summary`     | first line of the first error message, sanitised, URL query redacted, ≤ 256 units   |

The reporter builds the candidate from the raw `test`/`result`/`step` objects and runs it
through the filter, so an unknown field (including a future Playwright field) can never
reach the envelope.

### Identity (04.3.2-T1)

Every test envelope carries the resolved identity. The full contract is
`contracts/adapter/playwright-mapping.md`; the summary:

- **`ext.case_id`** — the `evidgate.id` annotation first (the strict canonical case id
  the server accepts: the 36-character hyphenated UUID in any version and any case,
  lower-cased, **never** the nil UUID), else the `evidgate.cases.yaml` sidecar manifest
  of the test file's own directory or an ancestor directory up to the suite's common
  ancestor (03.1.1-T1). Both present and **different** is a conflict:
  `ext.identity_conflict: true`, **no** `case_id`, and an `identity_conflict` warning
  naming the location — never a silent pick. An annotation that is not a canonical case
  id (including the nil UUID) is reported as `invalid_annotation` and treated as absent.
  The sidecar index is built once at `onBegin` from the suite's test files.
- **`ext.variant_hash`** — the server `VariantNormalizer` hash of the Playwright
  project's declared params (`metadata.evidgate.params`; explicit and opt-in, **never**
  derived from `use`, the project name, the title or the path): `sha256:<hex>` over the
  RFC 8785 canonical JSON of the normalised params only. Absent params → the empty set
  `{}`. Memoised per project, so retries and shards of one project share the hash. A
  refused params set emits one `invalid_variant_params` warning (paths and reasons only,
  never values) and no hash. Parity corpus: `fixtures/variant-hash/vectors.json`.
- **`ext.target_env`** — `EVIDGATE_TARGET_ENV` (trimmed, validated against the manifest
  `targetEnv` shape), else the Playwright project name validated the same way; an
  invalid env value warns `invalid_target_env` and falls back, an invalid project name is
  omitted (and warns once per project when the env value is invalid too).
- **`ext.warn`** — the warning channel: `run.log` envelopes carrying
  `{ code, message, path?, count? }`. Codes: `duplicate_case_id`, `identity_conflict`,
  `invalid_annotation`, `invalid_variant_params`, `invalid_target_env`,
  `invalid_sidecar`, `invalid_sidecar_entry`, `sidecar_conflict`, `unknown_status`,
  `unmapped`, `warnings_truncated`. The attachment path adds the `attachment_*` /
  `attachments_*` codes listed under [Attachments](#attachments-0433-t2). At most 100
  warning envelopes per run
  (`MAX_WARNING_ENVELOPES`); the overflow is summarised once at `onEnd`, and the two
  `onEnd` summaries bypass the cap. A message never echoes file content or a parameter
  value.
- **Exit code (AC2)** — `onEnd` returns `{ status: 'failed' }` when any test lacked a
  complete identity (no `case_id`, no `variant_hash` or no `target_env`), unless
  `EVIDGATE_ALLOW_UNMAPPED=1`; the override applies only to a `passed` run, and the gate
  writes one `[evidgate]` line to stderr naming the per-cause counts
  (`N conflicted, N no case_id, N no variant_hash, N no target_env`). The causes are
  counted independently, so one test can appear in several of them (a conflicted test has
  no `case_id`); the leading `N test(s)` counts each test once. Playwright awaits the
  reporter's `onEnd` result and overrides the run status with it, so a run that lost
  identity fails the CI job instead of passing with partial evidence.

`execution_key` stays server-owned (00 §7): the reporter emits
`PLACEHOLDER_EXECUTION_KEY = '0'.repeat(64)` (valid per the schema pattern
`^[0-9a-f]{64}$`) for test and `run.log` envelopes and never hashes it. 05.2.1-T3.1
defines the byte layout and owns the real wiring; until then an adapter batch cannot
match a planned execution (T10 `unknown_execution_key`). A caller may inject an
`identity` resolver, which wins over the built-in mapping. The injected resolver is held
to the **same completeness gate**: every identity it returns must carry `case_id`,
`variant_hash` and `target_env`, or the run fails at `onEnd` unless
`EVIDGATE_ALLOW_UNMAPPED=1`.

### Status (04.3.2-T2)

The full table is `contracts/adapter/playwright-mapping.md` §status; the summary: the
envelope `status` is the raw `TestResult.status` verbatim (cut only past the schema's 64
chars) and `ext.normalized` the 00 §8.3 value — `passed`, `failed`, `timedOut` →
`timed_out`, `interrupted` → `error`, `skipped` → `skipped` (with `ext.skip_reason:
"fixme"` when a `fixme` annotation is present; annotations are `test.annotations` then
`result.annotations`, so one added on a retry counts). `expectedStatus: "failed"` with a
`failed` outcome → `passed` + `ext.xfail: true`; with a `passed` outcome → `failed` +
`ext.xfail: true` (fail closed). Any other raw status → `error` plus one `unknown_status`
warning per distinct raw status. `ext.error_summary` is the first line of the first error
message (ANSI, control and BIDI characters removed, URL userinfo and query/fragment
redacted), ≤ 256 UTF-16 units, never a stack line. These four keys are set on
`test.finished` only.

## Attachments (04.3.3-T2)

**Uploads need a real execution key.** With the built-in identity resolver the
`execution_key` is the server-owned placeholder (`'0'.repeat(64)`) until the server-supplied
key lands — `identity.ts` says so, and the STATUS row "04.3.1-T1 placeholder execution_key"
tracks the hand-over. The wire member is `^[0-9a-f]{64}$`: the server's
`sha256(case_id ‖ variant_hash ‖ target_env)` (00 §7 / 05.2.1-T3). An upload under the
placeholder would be an unplanned attempt the server must refuse, so a run that keeps the
built-in resolver uploads **nothing**: every attachment is skipped with
`attachment_no_execution_key` and the test result is kept. To upload, pass the `identity`
option and return the server-computed key for the test — the reporter itself only checks that
it is not the placeholder — alongside `case_id`, `variant_hash` and `target_env` (see
[Identity](#identity-0432-t1) for the members).

Every `result.attachments` entry of a test is classified, policy-checked and — when it
passes every check — uploaded as an artifact through the CLI's `uploadFile()` facade
(`createAttachmentUploader` from `@evidgate/cli`, 04.2.4-T1). The `artifact_ids` the upload
returns are written on that attempt's `test.finished` envelope. An attachment that is
skipped or fails **never** loses the test result: the envelope is still written and a
`run.log` warning says why (AC5).

| Playwright attachment | Kind         | Media type                 |
| --------------------- | ------------ | -------------------------- |
| `trace*.zip`          | `trace`      | `application/zip`          |
| `video*.(webm\|mp4)`  | `video`      | `video/webm` / `video/mp4` |
| `*.(png\|jpeg\|jpg)`  | `screenshot` | `image/png` / `image/jpeg` |

The extension decides the kind and the media type; the attachment's own `contentType` is
only **checked** against that table, so a disagreement is a skip
(`attachment_type_mismatch`), never a re-typed upload. `trace` and `video` also require
their name prefix, so `report.zip` is unmapped rather than a trace. An extension the table
does not name is skipped (`attachment_unmapped_type`) — nothing is ever uploaded under a
guessed kind, because a guessed kind would file evidence under the wrong retention row. A
body-only attachment (no path) is skipped as `attachment_no_path`: uploading it would mean
buffering the whole attachment inside the test process.

The reporter declares `supports.capture_control: false` (see
[Capability carrier](#capability-carrier)), so a run produced by it is labelled
`capture_uncontrolled` server-side: the uploads follow the project's artifact policy, not a
run-create configuration document.

### Allowed directories (AC6)

Deny by default. The file's **real** path (symlinks resolved) must sit under one of:

- every Playwright `outputDir` of the run;
- `<rootDir>/test-results` (Playwright's default output directory);
- every directory in the `attachment_dirs` option, or in `EVIDGATE_ATTACHMENT_DIRS`
  (path-delimiter separated, read once in the constructor). Relative entries resolve
  against `rootDir`, as Playwright resolves config paths.

Anything else is refused before a byte is read (`attachment_path_refused`), and so is a
Windows drive-letter or UNC path on another platform, a NUL in the path, a directory, FIFO
or device, or a symlink planted inside a root after resolution. A path that does not exist
or cannot be inspected is `attachment_unreadable`. `..` traversal is resolved away by the
same rule as `packages/cli`'s `--file` guard, so the two cannot disagree. A refusal names
the file's base name and a filtered reason — never the full path, never a root list.

Two consequences of sharing that guard:

- **A root that is itself a symbolic link is refused.** The CLI's `openUploadFile` inspects
  the root's final component (a repo-planted `test-results -> $HOME` must not authorise a home
  directory), and it resolves **every** root before it looks at the file — so one symlinked
  `attachment_dirs` or `EVIDGATE_ATTACHMENT_DIRS` entry fails **every attachment upload of the
  run** with `attachment_upload_failed` carrying `upload_path_refused`, not only the files
  under that root. List the real directory; ancestor aliases such as macOS
  `/tmp -> /private/tmp` are fine, because only the final component is inspected.
- **A hard link is not a symbolic link and is accepted.** No code path treats one specially:
  a hard link to a file inside an allowed root uploads normally, and the guard still refuses a
  file that is not owned by the running user (`owned by another user`), so a link can never
  bring in another user's file.

### Size caps (AC6)

Checked before anything is read or hashed, against the CLI's static per-kind defaults
(`contracts/evidence/artifact-kind-defaults.v1.json`): trace 100 MiB, video 200 MiB,
screenshot 10 MiB. Exactly at the cap passes, one byte over is skipped as
`attachment_over_cap`. **The server's project policy wins**: its row may lower the cap, and
`effective_max_bytes = min(row, default)` is applied by the server, so the local check is
only an early refusal that costs no request.

`.webm` videos: the static default for the video kind accepts only `video/mp4`, so a WebM
recording (Playwright's own container) may be refused by the server with 422
`media_type_not_allowed` unless the project's artifact policy for `video` lists
`video/webm`. The refusal shows up as an `attachment_upload_failed` warning and the test
result is kept; allowing WebM is a project-policy decision (06), not a reporter one.

### Configuration, and what happens without it

Uploads are enabled only while the CLI's configuration resolves from the environment — the
same surface `evidgate artifacts upload` uses, listed under
[Environment](#environment) (`EVIDGATE_API_URL`, `EVIDGATE_PROJECT`, one token source; all
three are required). Two degradations, one warning each per run, both keeping every test
result and the spool-only offline mode intact:

- `attachments_unconfigured` — the configuration is missing or invalid, so every
  attachment is skipped and no request is made.
- `attachments_unavailable` — the lazy `import('@evidgate/cli')` failed (the CLI package is
  not installed in the customer's workspace). The warning carries the code only, never a
  stack or a path.

`attachment_no_execution_key` is the third "cannot upload yet" case: the reporter's
`execution_key` is still the server-owned placeholder (00 §7, 05.2.1-T3), and an upload
under it would be an unplanned attempt the server must refuse, so the attachment is skipped
instead of sending a request that cannot succeed.

No token, no presigned URL and no attachment byte ever reaches a log line or the spool; a
warning carries a base name, a code and a filtered reason.

### Concurrency and the drain

Uploads run off the event hot path, but the event batch does not. A test with **no**
attachments takes the 04.3.1 path unchanged: one `test.finished`, its steps, one flush, all
synchronous. A test **with** attachments holds its drafted `test.finished` and steps until
that attempt's uploads settle, then appends them in one go — so the envelope can carry their
`artifact_ids`; the callback itself returns once the uploads are queued. At most **two
uploads are in flight per shard** (a fixed cap — the reporter lives inside the test process
and must never starve the tests), in FIFO start order, and `onEnd` waits for them for at most
`attachment_drain_deadline_ms` (default **120 000 ms**, NFR-04-1).

The deadline stops the **wait**, not the uploads. An upload that has not started its PUT is
**abandoned at its next checkpoint** — before the file is opened, after the digest (before the
bearer or any request), before the journal and before the engine's first mint — and **not
cancelled mid-PUT**: the stopped call rejects with the CLI's `upload_aborted`, but the drain has
already settled that job as abandoned, so the attachment's record carries
`attachment_drain_deadline` and the run emits the single summary warning. A PUT already in
flight runs until its own deadline, `min(per-upload deadline, remaining drain time)`, and a
queued one is never started. A late rejection is swallowed so it can never fail an otherwise
green run. The deadline costs exactly **one** `attachment_drain_deadline` warning for the run,
naming how many attempts were abandoned — never one warning per attachment.

Held batches are appended in test-end order **among the attempts that carry attachments**,
through one serialiser; `sequence` stays contiguous per producer (the 00 §9.4 order rule)
because it is assigned at append time, never at draft time. An attempt **without**
attachments does not wait for anything: it appends immediately. So the spool order can differ
from the callback order, and a test without attachments may appear before an earlier test
whose uploads were still in flight — the ordering guarantee is among held batches only.

There is a hard bound on what one run considers: at most **50 attachments per test** and
**1 000 per run** (`MAX_ATTACHMENTS_PER_TEST` / `MAX_ATTACHMENTS_PER_RUN`). The overflow is
skipped, never uploaded, and summarised with **one** `attachment_limit` warning per run —
so a suite that attaches a screenshot per step cannot flood the capped warning channel or
the spool.

### Warnings

All attachment warnings are ordinary `run.log` warnings on the same `ext.warn` channel as
the identity warnings (so they obey the same 100-per-run cap):

| Code                                 | Meaning                                                                    |
| ------------------------------------ | -------------------------------------------------------------------------- |
| `attachment_unmapped_type`           | extension not in the AC4 table (or a trace/video without its prefix)       |
| `attachment_no_path`                 | a body-only attachment; it is never buffered and never uploaded            |
| `attachment_type_mismatch`           | the attachment's `contentType` disagrees with the table's media type       |
| `attachment_over_cap`                | larger than the kind's static cap                                          |
| `attachment_path_refused`            | outside the allow-listed roots, traversal, exotic path form, not a file    |
| `attachment_unreadable`              | allowed path, but the file is missing or cannot be inspected               |
| `attachment_upload_failed`           | the CLI's upload rejected (5xx, 422, network, …); the result is kept       |
| `attachment_no_execution_key`        | the reporter's `execution_key` is still the placeholder (05.2.1-T3)        |
| `attachments_unconfigured`           | the CLI configuration is missing/invalid; all attachments skipped, once    |
| `attachments_unavailable`            | the lazy `import('@evidgate/cli')` failed; all attachments skipped, once   |
| `attachment_drain_deadline`          | `onEnd`'s deadline gave up: ONE warning with the abandoned attempt count   |
| `attachment_limit`                   | the 50-per-test / 1 000-per-run attachment bound was reached, once per run |
| `attachment_sensitivity_not_carried` | an annotated attachment whose kind has no sensitive default (see below)    |

The **per-attachment** codes are the six skip codes, `attachment_upload_failed` and
`attachment_sensitivity_not_carried`: each is emitted once per attachment it describes, so a
run that skips or annotates many attachments spends the 100-warning budget fast. Past the
cap, the surplus becomes **one** `warnings_truncated` envelope at `onEnd` that names how many
warnings were lost — so on a long run the per-attachment warnings you see are the first 100,
not the whole story. The remaining codes are bounded by the number of tests and runs rather
than by the attachment count: `attachment_no_execution_key` is once per test,
`attachments_unconfigured`, `attachments_unavailable`, `attachment_drain_deadline`,
`attachment_limit` and the `shards_*` notices are once per run.

### Sensitivity (AC7) and its limit

An attachment is `sensitive` when the suite or the case opts in with the annotation
`evidgate.sensitive` (`test.annotations` or `result.annotations`; presence is the opt-in) or
when its kind is sensitive by default — `trace` is (T57); video and screenshot are not. A
more specific decision never downgrades a sensitive one.

The wire has **no** sensitivity member (`createArtifactUploadUrl` has none; D-S56-Q7), so
the attachment still uploads under the project's policy, and this is a stated limit rather
than a silent one: when an **annotated** attachment's kind has no sensitive default, the
reporter emits one `attachment_sensitivity_not_carried` warning naming only the attachment's
base name and kind, and records the opt-in on the local attachment record. Carrying
sensitivity on the wire is follow-up **F34**; until then, use a kind that is sensitive by
default (a trace) for evidence that must be treated as personal data.

## Version guard

At construction the reporter reads the installed `@playwright/test` version from its
`package.json` (`createRequire`) and checks the hand-written `^1.63` range
(`>=1.63.0 <2.0.0`). Outside the range it throws `EvidgateUnsupportedEngineError` naming
the installed and supported versions, before any spool file is created (no partial emit).
Prereleases (`1.63.0-beta.1`) are rejected, as in npm's semver. The version source is
injectable for tests via the `engineVersion` option.

## Verified Playwright 1.63 API

Confirmed against the installed package
(`node_modules/playwright/types/testReporter.d.ts`, re-exported by
`@playwright/test/reporter`), resolving OI-04-01:

| Fact                      | Verified shape                                                                     |
| ------------------------- | ---------------------------------------------------------------------------------- |
| `TestResult.retry`        | `retry: number` — 0-based; `attempt_no = retry + 1`                                |
| `TestResult.status`       | `"passed" \| "failed" \| "timedOut" \| "skipped" \| "interrupted"` (raw string)    |
| `TestResult.annotations`  | `Array<{ type: string; description?: string; … }>` — added at runtime, per attempt |
| `TestResult.errors`       | `Array<TestError>`; `TestError` has `message?`, `value?`, `stack?`                 |
| `TestCase.expectedStatus` | `"passed" \| "failed" \| "timedOut" \| "skipped" \| "interrupted"`                 |
| `TestCase.annotations`    | `Array<{ type: string; description?: string; … }>`                                 |
| `TestResult.duration`     | `duration: number` (milliseconds)                                                  |
| `TestResult.steps`        | `steps: Array<TestStep>` — **nested**; `TestStep.steps` is `Array<TestStep>`       |
| `TestResult.attachments`  | `Array<{ name: string; contentType: string; path?: string; body?: Buffer }>`       |
| `TestStep`                | `title`, `category`, `duration`, `location?`, `steps`, `params?`, `subtitle?`      |
| `TestCase`                | `title`, `location: {file, line, column}`, `parent: Suite`, `id`, `tags`, …        |
| `Suite.project()`         | `FullProject \| undefined`; `FullProject.name` is the project name                 |

`result.steps` is walked recursively (depth-first, parent before child) and lazily, so
every nested step is emitted without materialising the whole tree; the decision is
recorded here because the 1.63 type is nested.

## Tests

`npm test -w @evidgate/playwright-reporter` runs the Vitest suite (Node only, no network):
`sequence`, `spool`, `spool-boundary`, `spool-hardening`, `callbacks`, `ext-filter`,
`capability` (Ajv 8 against the `contracts/` schemas), `guard`, `guard-boundary`,
`unicode`, `unknown-fields`, `identity`, `identity-resolve` (the 04.3.2-T1 built-in
mapping: annotation, sidecar, conflict, `variant_hash`, `target_env`, the warning channel
and the `onEnd` exit code, with every emitted envelope validated against the envelope
schema), `identity-perf` (5k parametrised tests resolve in < 500 ms), `variant` (the
`fixtures/variant-hash/vectors.json` corpus), `sidecar` (the `evidgate.cases.yaml`
loader), `index`, `memory`, `reporter-boundary`, `reporter-hardening` (the review fixes:
`printsToStdio`, flush-per-callback, step-title and `duration_ms` policy), `status-map`
(the 04.3.2-T2 AC6 table, unknown statuses and `error_summary`), `retry` (attempts 1..n,
flaky and fixme-on-retry through the reporter), `status-reporter` (unknown status warning,
xfail, `interrupted` vs `timedOut`) and `reporter.e2e` (the real `playwright test` CLI on
`test-fixtures/`, including an annotated test and a sidecar-mapped test).

The 04.3.3-T2 attachment specs are `attach-map` (the AC4 kind table, the MIME check, the
size-cap boundary), `paths` (the AC6 allow-list: traversal, symlinks, exotic path forms,
non-regular files), `drain` (bounded concurrency, FIFO order, the deadline and its
swallowed late rejections), `attach` (the AC5/AC7 wiring: `artifact_ids` on
`test.finished`, a failed upload keeping the result, the sensitivity opt-in and its
warning) and `cli-boundary` (this package's only runtime `import('@evidgate/cli')` is the
one lazy load in `attach.ts`, every other file only takes types, and `packages/cli` never
imports this adapter).
