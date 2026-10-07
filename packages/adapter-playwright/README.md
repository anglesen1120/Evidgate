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

| Option          | Type                          | Default  | Meaning                                                                     |
| --------------- | ----------------------------- | -------- | --------------------------------------------------------------------------- |
| `spool_dir`     | `string`                      | —        | Spool root. Falls back to `EVIDGATE_SPOOL_DIR`.                             |
| `run_id`        | `string`                      | —        | Run id. Falls back to `EVIDGATE_RUN_ID`.                                    |
| `producer_id`   | `string`                      | —        | Producer id. **Required** (see below).                                      |
| `steps`         | `boolean`                     | `true`   | Emit `test.step` envelopes for `result.steps`; sets `supports.step_events`. |
| `mode`          | `'full' \| 'ingest_only'`     | `'full'` | Capability `mode`.                                                          |
| `identity`      | `(test) => { execution_key }` | —        | Execution-identity resolver (see below).                                    |
| `engineVersion` | `string`                      | —        | Test-only seam: overrides the detected `@playwright/test` version.          |

### Construction is fail-fast

Construction throws (before any callback runs) for:

- a missing `spool_dir`, `run_id` or `producer_id` option;
- an invalid `run_id`/`producer_id` (`EvidgateInvalidSpoolIdError`);
- an unsupported `@playwright/test` version (`EvidgateUnsupportedEngineError`, AC5);
- **spool initialisation**: `mkdir` or opening the chunk/checkpoint under `spool_dir`
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

| Variable             | Used for             |
| -------------------- | -------------------- |
| `EVIDGATE_SPOOL_DIR` | `spool_dir` fallback |
| `EVIDGATE_RUN_ID`    | `run_id` fallback    |

## Spool layout

```
<spool_dir>/<run_id>/<producer_id>/000001.jsonl   # 6-digit zero-padded chunks
<spool_dir>/<run_id>/<producer_id>/checkpoint.json # { producer_id, next_sequence }
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
- **Symlink- and mode-hardened files.** The checkpoint temp file is unlinked if present
  and re-created with `O_WRONLY|O_CREAT|O_EXCL|O_NOFOLLOW` `0600`; chunks open with
  `O_WRONLY|O_APPEND|O_CREAT|O_NOFOLLOW`; torn-tail truncation opens with `O_NOFOLLOW`.
  After every open the writer `fstat`s the descriptor and rejects non-regular files or
  files not owned by the current uid, then `fchmod`s it to `0600` — a pre-existing `0644`
  chunk becomes `0600`, and a planted symlink is refused instead of followed.
- Envelopes are appended per callback (O(1), no whole-suite buffering). A flush writes
  the pending batch, fsyncs the chunk, then rewrites the checkpoint atomically
  (`checkpoint.json.tmp` → fsync → rename → directory fsync).
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
- A checkpoint `next_sequence` is only honoured when it is a safe integer ≥ 1; the highest
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
| `onEnd`       | `run.log` `run_end` (`status` = run status) + flush + checkpoint                                                    |

`attempt_no = result.retry + 1`. Callbacks never throw: a spool write failure is buffered
and re-thrown from `onEnd`, so the Playwright run reports the reporter failure instead of
losing evidence silently.

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

| Key          | Source                                                                              |
| ------------ | ----------------------------------------------------------------------------------- |
| `schema`     | always present; required by the envelope schema                                     |
| `title`      | `test.title`; `step.title` only for user-authored step categories (see Step titles) |
| `file`       | `test.location.file`                                                                |
| `location`   | `test.location` / `step.location` `{file, line, column}`                            |
| `project`    | `test.parent.project()?.name`                                                       |
| `engine`     | the `@playwright/test` version                                                      |
| `marker`     | `run.log` marker: `run_begin` \| `run_end`                                          |
| `capability` | the capability document carrier                                                     |
| `category`   | `step.category`                                                                     |

The reporter builds the candidate from the raw `test`/`result`/`step` objects and runs it
through the filter, so an unknown field (including a future Playwright field) can never
reach the envelope.

### `execution_key` placeholder (hand-over to 04.3.2-T1)

The server computes `execution_key` from the manifest (00 §7); the reporter must not
derive it. Until 04.3.2-T1 supplies the real identity mapping, the reporter emits
`PLACEHOLDER_EXECUTION_KEY = '0'.repeat(64)` (valid per the schema pattern
`^[0-9a-f]{64}$`), used for test envelopes and for `run.log` envelopes. A caller may
inject an `identity` resolver; 04.3.2-T1 replaces the default with the real mapping.

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

| Fact                     | Verified shape                                                                  |
| ------------------------ | ------------------------------------------------------------------------------- |
| `TestResult.retry`       | `retry: number` — 0-based; `attempt_no = retry + 1`                             |
| `TestResult.status`      | `"passed" \| "failed" \| "timedOut" \| "skipped" \| "interrupted"` (raw string) |
| `TestResult.duration`    | `duration: number` (milliseconds)                                               |
| `TestResult.steps`       | `steps: Array<TestStep>` — **nested**; `TestStep.steps` is `Array<TestStep>`    |
| `TestResult.attachments` | `Array<{ name: string; contentType: string; path?: string; body?: Buffer }>`    |
| `TestStep`               | `title`, `category`, `duration`, `location?`, `steps`, `params?`, `subtitle?`   |
| `TestCase`               | `title`, `location: {file, line, column}`, `parent: Suite`, `id`, `tags`, …     |
| `Suite.project()`        | `FullProject \| undefined`; `FullProject.name` is the project name              |

`result.steps` is walked recursively (depth-first, parent before child) and lazily, so
every nested step is emitted without materialising the whole tree; the decision is
recorded here because the 1.63 type is nested.

## Tests

`npm test -w @evidgate/playwright-reporter` runs the Vitest suite (Node only, no network):
`sequence`, `spool`, `spool-boundary`, `spool-hardening`, `callbacks`, `ext-filter`,
`capability` (Ajv 8 against the `contracts/` schemas), `guard`, `guard-boundary`,
`unicode`, `unknown-fields`, `identity`, `index`, `memory`, `reporter-boundary`,
`reporter-hardening` (the review fixes: `printsToStdio`, flush-per-callback, step-title
and `duration_ms` policy) and `reporter.e2e` (the real `playwright test` CLI on
`test-fixtures/`).
