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

| Option          | Type                      | Default  | Meaning                                                                     |
| --------------- | ------------------------- | -------- | --------------------------------------------------------------------------- |
| `spool_dir`     | `string`                  | —        | Spool root. Falls back to `EVIDGATE_SPOOL_DIR`.                             |
| `run_id`        | `string`                  | —        | Run id. Falls back to `EVIDGATE_RUN_ID`.                                    |
| `producer_id`   | `string`                  | —        | Producer id. **Required** (see below).                                      |
| `steps`         | `boolean`                 | `true`   | Emit `test.step` envelopes for `result.steps`; sets `supports.step_events`. |
| `mode`          | `'full' \| 'ingest_only'` | `'full'` | Capability `mode`.                                                          |
| `identity`      | `(test) => Identity`      | —        | Execution-identity resolver; overrides the built-in mapping (see below).    |
| `sidecarFs`     | `SidecarFs`               | —        | Test-only seam: injects the sidecar filesystem reader.                      |
| `engineVersion` | `string`                  | —        | Test-only seam: overrides the detected `@playwright/test` version.          |

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

| Variable                  | Used for                                                                    |
| ------------------------- | --------------------------------------------------------------------------- |
| `EVIDGATE_SPOOL_DIR`      | `spool_dir` fallback                                                        |
| `EVIDGATE_RUN_ID`         | `run_id` fallback                                                           |
| `EVIDGATE_TARGET_ENV`     | `ext.target_env`; validated in the ctor, else the Playwright project name   |
| `EVIDGATE_ALLOW_UNMAPPED` | `'1'` keeps a run without a complete identity from failing the CI job (AC2) |

Every variable is read **once in the constructor**; no callback reads `process.env`.

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
  writer is read as a fallback. Restart resumes at `max(shards.json.sequence_next,
  last chunk sequence + 1)`. `EVIDGATE_SPOOL_MAX_BYTES` caps the run directory at
  write time. Contract: `contracts/adapter/playwright-mapping.md` "## shards".
- **One durable flush per callback batch.** `onBegin` and `onTestEnd` each flush at the
  end of their batch, so a killed run loses at most the current callback's envelopes
  instead of the whole suite; `onEnd` flushes the final `run_end` marker and closes. The
  64-envelope `flushThreshold` remains an upper bound on how many envelopes a single
  flush carries. On restart the writer reconciles `next_sequence` against the highest
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
    "supports": { "discovery": false, "step_events": true, "retry": true, "cancel": false },
    "mode": "full"
  }
}
```

`ext` is an open object that requires only `schema`, so this is allowed. The document
validates against `contracts/adapter/capability.v1.json`; `engine.version` is the
installed `@playwright/test` version read at runtime (D8). `platforms` is the platform
this reporter is running on (`<process.platform>-<process.arch>`).

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
  `unmapped`, `warnings_truncated`. At most 100 warning envelopes per run
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
