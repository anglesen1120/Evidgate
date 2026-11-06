# pytest `junitxml` mapping — element, identity, status and declared-vs-observed

How the server maps one stored pytest `junit.xml` report to `test.finished`
events, which identity hints it accepts, how it normalises the producer's values,
which caps bound the parse, and how the declared counters of the document are
compared with what the mapper actually observed. This is the server-side
counterpart of the producer mapping in [`playwright-mapping.md`](./playwright-mapping.md):
an implementer can rebuild the mapper from this document alone, and the
[`## Symbol index`](#symbol-index) table is checked against the mapper's constants
by `PytestJUnitMappingDocumentTests`, so a constant without a row (or a stale row)
fails the test stage.

Sources: 00 §5.7 (parser caps S4/S5, P5), 00 §8.3 (normalised status), 00 §8.5–§8.6
(completeness, never a silent pass), 00 §9.4 (event envelope), 04 §2 FR-04-6/FR-04-7,
04.4.1 §2 AC1–AC3 and AC7–AC8, the 04.4.1 task notes (T1 S255, T4.1 S258, T4.2 S261,
T5 S263), `migrations/0075_ingestion_report_parser.sql` and
`migrations/0078_evidence_raw_report.sql`, and the mapper sources
`PytestJUnitMapper`, `PytestStatus`, `JUnitDuration`, `PlannedExecutionIndex`,
`ParsingLimits`, `JUnitDeclaredObservedCheck`.
Legend: ✅ verified in the sources above · `[DECISION]` a recorded ruling ·
`[ENFORCED]` a database `CHECK` or trigger, so no code path can bypass it.

## Scope

In scope: the `junit-pytest` dialect (`PytestJUnitMapper.PytestParserId` /
`PytestJUnitMapper.PytestVersion`), one testcase → one event, the case identity
hint, value normalisation, the profile caps the report is parsed under, and the
declared-vs-observed comparison stored on `raw_report`.

Out of scope: upload mechanics, the reprocess flow (a new release adds a row), the
`GET .../comparison` HTTP read, and every other JUnit dialect (a dialect is a new
`report_parser` row, never a branch in this mapper).

## Document → event

The mapper walks the document once, streaming, through the parser profile (no DOM,
no second copy of any guard). Names are matched by XML **local name**, ordinals
compared exactly; namespaces are ignored. Only the elements and attributes below
are read; nothing else in the document changes an event.

`testsuites` and `testsuite` are only ever read for their counters (next section).
Neither is required: a root element that is neither declares no counters, and the
`testcase` elements the document carries still map. The mapping does not refuse a
document for its root element's name.

### Element and attribute table (AC1)

| Where                               | Value        | Result                                                                                                                        |
| ----------------------------------- | ------------ | ----------------------------------------------------------------------------------------------------------------------------- |
| any `testcase` element, any depth   | —            | one **candidate case**; the element's subtree is consumed so a status word inside a failure body is never read as a sibling's |
| `testcase` position in the document | —            | `sequence` = 1-based position among all `testcase` elements, counted whether or not the case resolves                         |
| `testcase/@classname`               | raw text     | identity hint 1; empty string when the attribute is absent                                                                    |
| `testcase/@name`                    | raw text     | identity hint 2; empty string when absent                                                                                     |
| `testcase/@time`                    | seconds      | `duration_ms` per Normalisation; `null` when absent or unusable                                                               |
| direct child `failure`              | element name | raw status `failure`, normalised `failed`                                                                                     |
| direct child `error`                | element name | raw status `error`, normalised `error`                                                                                        |
| direct child `skipped`              | element name | raw status `skipped`, normalised `skipped`                                                                                    |
| none of the three                   | —            | raw status `passed`, normalised `passed`                                                                                      |

Only the testcase's **direct** children count (`depth + 1`), and an empty
`<testcase … />` is a pass. The status is an element name, never element text:
`<failure message="…">assert error</failure>` says `failure` and nothing in its
text is parsed.

Several outcome elements on one testcase resolve by severity: `error` >
`failed` > `skipped` > `passed`. The pair kept is the winner's own pair, so an
`error` beside a `failure` yields raw `error`, normalised `error`;
`error` beside `skipped` yields raw `error`, normalised `error`; `failure` beside
`skipped` yields raw `failure`, normalised `failed`. An unknown normalised value
ranks with `passed`, so it can never outrank a reported result.

### One event per mapped case

A case whose execution key resolved (Identity hints) becomes one event row:

| Field                            | Value                                                                                                                                                                                               |
| -------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `type`                           | `test.finished`                                                                                                                                                                                     |
| `producer_id`                    | `junit-<raw report artifact id>` (see Replay)                                                                                                                                                       |
| `sequence`                       | the testcase's 1-based document position                                                                                                                                                            |
| `event_id`                       | first 16 bytes of `sha256(producer_id + ":" + sequence)` as a GUID, so a replay derives the same identity                                                                                           |
| `execution_key`                  | the resolved planned execution key; never guessed                                                                                                                                                   |
| `attempt_no`                     | 1                                                                                                                                                                                                   |
| `raw_status`                     | the producer's own word, verbatim (`failure`, `error`, `skipped`, `passed`)                                                                                                                         |
| `normalized_status`              | `passed`, `failed`, `error` or `skipped`                                                                                                                                                            |
| `duration_ms`                    | whole milliseconds, or `null`                                                                                                                                                                       |
| `payload`                        | `{"ext": …}` only                                                                                                                                                                                   |
| `payload_hash`                   | canonical (RFC 8785) hash over `execution_key`, `raw_status`, `normalized_status`, `duration_ms` and `ext` — the substantive fields, so a replay that changes one is not read as identical evidence |
| `artifact_ids`                   | the raw report's artifact id                                                                                                                                                                        |
| `job_id`, `epoch`, `occurred_at` | `null`: this producer has no job and no live clock                                                                                                                                                  |
| `after_cancel`, `late`           | from the run's admission, not from the mapper                                                                                                                                                       |

`ext` is `{"schema": "1", "parser": "<parser_id>/<version>", "normalized": …,
"classname": …, "name": …}`: the parser stamp is `junit-pytest/1.0.0`, and
`classname`/`name` are the bounded echoes (Normalisation), never the resolver's
input.

## Declared counters (AC2)

The declared numbers are the producer's claim, stored as `declared_tests`,
`declared_failures`, `declared_errors` and `declared_skipped`. They are read once
from the root element:

- A `testsuite` root declares its own four attributes.
- A `testsuites` root declares its own four attributes. A counter the root does
  not declare falls back, per attribute independently, to the **sum of the
  `testsuites` root's direct `testsuite` children** (depth 1) — pytest's sharded
  output lists several suites under one root.
- A counter is an invariant integer. Absent, negative or non-numeric → `null`,
  never `0`, because "not declared" and "declared zero" are different claims.
  A sum saturates at `int.MaxValue` instead of overflowing.
- A fresh read of a `testsuite` root's attributes is not repeated for deeper
  suites: only the root and, for a `testsuites` root, its direct children declare.

The mapper compares a **non-null** `declared_tests` with the number of `testcase`
elements the document carries — every one, mapped or not — and reports
`count_mismatch` when the two differ. The other three declared counters are
compared by the engine below, never by the mapper.

## Identity hints

The mapper asks the caller to resolve a `(classname, name)` pair against the run's
planned executions. The caller's index (`PlannedExecutionIndex`) answers one key
or `null`; the mapper never invents a key, because an event under an invented key
would claim evidence for a case nobody planned. An unresolved testcase emits **no
event** and records `unmapped_case`; it still occupies its document position, so an
unmapped testcase between two mapped ones leaves a sequence gap.

**Only a unique match resolves.** More than one planned execution matching within
one level is ambiguous evidence: resolution answers `null` and does not fall
through to a coarser shape.

The levels, most specific first; the first level that matches anything decides:

1. The `classname` as a planned file path, verbatim, plus `::name`.
2. The full `classname`: `classname::name` as a planned case name, and the file
   path the whole classname spells (`tests.test_payments` →
   `tests/test_payments.py`) plus `::name`.
3. Each shorter module prefix, with the file path it spells plus `::name`, beside
   the class path that follows that prefix plus `::name`
   (`tests.test_payments.TestPayments` → `tests/test_payments.py` and
   `TestPayments`).
4. The bare `name` — only when the report carries **no** `classname` at all.

An explicit `classname` that matches no planned shape stays unresolved; it never
borrows an unrelated case that happens to share the name. The hint is hostile
input, so it is bounded before any string is expanded:

| Bound                       | Value            | Effect above it                               |
| --------------------------- | ---------------- | --------------------------------------------- |
| `classname` length          | `512` characters | unresolved, never an exception                |
| `name` length               | `512` characters | unresolved; an empty `name` is unresolved too |
| dotted `classname` segments | `32`             | unresolved (dotted fallback expansion only)   |

Lengths are in UTF-16 `char`s; matching is ordinal. The segment bound governs the
dotted fallback expansion only: `ResolveExplicit` looks the whole `classname` up as
a planned file path plus `::name` first, so an exact verbatim planned-file-path
match resolves before any split, however many segments the string carries. The
length bounds above still apply to it.

## Normalisation

- **Duration.** `@time` is seconds, parsed as an invariant floating-point number.
  Missing, blank, negative, non-finite (`NaN`/`Infinity`) or a millisecond value
  that cannot be represented → `null`, because a duration we cannot trust is
  absent rather than a zero that would read as "instant". Otherwise the value is
  multiplied by 1000 and rounded **half away from zero** to whole milliseconds, so
  every platform produces the same integer for the same bytes (`0.0105` s →
  `11` ms; `0.000` s → `0` ms). The event row keeps the exact value; the
  `case_attempt` row stores it as an `int` and drops a value above `int.MaxValue`
  rather than truncating it.
- **Echoed identity.** `classname` and `name` are echoed into `ext` as the producer
  wrote them, bounded to `256` characters: a value longer than that keeps its first
  `256 − len("[truncated]") = 245` characters plus `[truncated]`. If the cut would
  split a surrogate pair, one more character is dropped, so the marker never
  begins inside a pair and the echoed text is always encodable. Resolution always
  reads the attribute as written; only the echo is bounded.
- **Counters.** As in Declared counters: absent, negative or non-numeric → `null`.
- **Text.** Nothing is trimmed, lower-cased or re-encoded. The parser profile
  handles UTF-8/UTF-16 with or without a BOM and rejects a DTD before any element
  is read.

## Caps

The mapper's walk enforces no cap of its own; the parser profile does, in-process
or in the resource-limited parse child, and a breach is a refusal (`JUnitParseRefusedException`),
never a partial event set.

| Guard                              | Value                                                                                               | Refusal reason                                                       |
| ---------------------------------- | --------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| Element nesting depth              | `64` (`ParsingLimits.MaxDepth`)                                                                     | `depth_exceeded`                                                     |
| Wall clock for one parse           | `30` s (`ParsingLimits.ParseTimeout`)                                                               | `timeout` from the profile; `parse_timeout` from the runner's limits |
| Report bytes, profile ceiling      | `67108864` (`ParsingLimits.MaxReportBytes`, 64 MiB)                                                 | `size_exceeded`, carried as `payload_too_large` (413)                |
| Report bytes, per artifact kind    | `artifact_kind_policy.max_bytes`; the stricter of it and the ceiling is the mapper's byte cap       | `size_exceeded` as above; an over-cap stored object is `too_large`   |
| `testcase` entries                 | configured `IngestionLimits.MaxReportCases`, published default `150000` (allowed range 1 … 1000000) | `cap_exceeded` with path `report_cases`, limit and actual count      |
| Document type declaration          | prohibited; no resolver, entity expansion capped                                                    | `dtd_prohibited`                                                     |
| Distinct discrepancy codes per row | `16`                                                                                                | the row cannot be written (shape `CHECK`)                            |

Two timeout paths are distinct, and only one of them is a fact about the report.
A parse that exceeds `ParsingLimits.ParseTimeout` inside the profile — in-process,
or in the child's own profile run — is the profile's refusal and keeps the reason
`timeout`. The runner's limits are separate: when its wall clock
(`ParseRunnerOptions.WallClockSeconds`, bound from `EVIDGATE:PARSE`, 30 s by
default) expires it kills the child tree, and the CPU limit the child applies to
itself from `--cpu-seconds` arrives as `SIGXCPU` (exit 152) from the kernel. Either
way the verdict is the host's `parse_timeout`, and a non-zero exit decides before
any record the child wrote.

An oversized stored object is refused before any child starts. The capped read
never truncates a report — `Get` throws for an object over the cap — so the attempt
ends as outcome `malformed` with reason `too_large`: this task's verdict, not the
profile's. The profile's own size refusal stays `size_exceeded`, carried as
`payload_too_large` (413), and is reached only for bytes that were read.

A child run that ends without a verdict about the document — a heap-limit death, an
unattributable signal, or a record stream that breaks the protocol — is reported as
`parse_oom` or `parse_crashed`; those are host verdicts, not facts about the
report, and reach the row as the refusal's reason all the same.

`DocumentSlackBytes` (`1048576`) is not a report cap. It applies only to the
record-list entry point (`Map(IReadOnlyList<ParseRecord>, …)`), where the mapper
rebuilds a document from the parse child's records: the rebuild may be slightly
larger than the source (attribute quoting, element spelling), so it is permitted
1 MiB over the report cap before the reader refuses it. The byte entry point
(`Map(Stream, …)`) uses the cap itself, unchanged.

## Declared vs observed

### Observed counters

`observed_*` describes what the mapper mapped — the events the attempt could
write — never the document's claim:

- `observed_tests` = the mapped cases (resolved execution keys).
- `observed_failures` / `observed_errors` / `observed_skipped` = the mapped cases
  whose normalised status is `failed` / `error` / `skipped`.

A row's `CHECK` also holds `observed_failures + observed_errors + observed_skipped
<= observed_tests`. `[ENFORCED]`

### The engine

`JUnitDeclaredObservedCheck.Compare(JUnitDeclaredCounters declared,
ObservedCounters observed, IReadOnlyList<string> mapperCodes)` returns the final,
ordered code list, **keeping multiplicity** (one entry per occurrence). It adds
`status_mismatch` and the canonical order and nothing else: `malformed` never
enters a parsed report's list (it is derived from a refused row's outcome, below),
so the function has no `malformed` branch at all.

1. `count_mismatch`, `unmapped_case` and `duplicate_case` are computed by the
   mapper and arrive in `mapperCodes`. They are the mapper's whole vocabulary:
   - `count_mismatch` — declared `tests` differs from the `testcase` elements the
     document carries, whether or not they resolved.
   - `unmapped_case` — one entry per testcase whose identity hint resolved to no
     planned execution; no event was emitted for it.
   - `duplicate_case` — one entry per testcase whose resolved key was already
     mapped. One execution owns one event: the kept case keeps its position,
     sequence and echo and takes the repeat's raw/normalised pair only when the
     repeat is more severe.
2. `status_mismatch` — for each of `failures`, `errors` and `skipped` whose
   declared value is **non-null** and differs from the observed count, one entry.
   Declared `tests` is never compared here (`count_mismatch` already covers it).
   `status_mismatch` is compared **only** when `mapperCodes` holds none of
   `count_mismatch`, `unmapped_case` or `duplicate_case`: with any of those present
   the observed cases do not cover the document, and that code already explains
   the difference.
3. Canonical order of the result: `malformed`, `count_mismatch`, `unmapped_case`,
   `duplicate_case`, `status_mismatch`; each code's entries stay together. A code
   outside that list (a future one) goes last, in first-seen order. `malformed`
   leads the order so a derived `malformed` and the stored codes can never
   disagree about position; `Compare` itself never returns it.

A declared-0 empty suite (`declared tests` = `0`, zero `testcase` elements) yields
no code at all: outcome `mapped` with zero events. That is a mapped report, not
evidence of a passing run — completeness reads the events, and no event exists.

### Storage and outcome

- The stored array is `[{code, count}]`, aggregated from the engine's list in the
  order the codes first appear (canonical, so grouped), capped to 16 distinct
  codes, with codes and counts only — never a case name or a message body.
  `[ENFORCED]` (element shape and the 16 ceiling).
- `mapped` iff the final code list is empty, `partial` iff it is not. `[ENFORCED]`
  (`raw_report_partial_check`: `partial` implies a non-empty array).
- `partial` means "parsed, and at least one discrepancy explains the difference";
  it never means the run failed or passed. The engine only reports codes; it never
  decides an outcome and never turns missing evidence into a pass.
- The row is written in the events' transaction, after the events, so a rollback
  leaves no row.

Stored codes are therefore exactly `count_mismatch`, `unmapped_case`,
`duplicate_case` (`duplicate_case` predates this contract) and `status_mismatch`.

### `malformed` is derived, never stored

`malformed` is not a stored code. A refused document writes its own row with
outcome `malformed` and a `reason` word, and `raw_report_refused_empty_check`
requires such a row to carry `discrepancies = '[]'`, no declared counter and
`observed_tests = 0`. `[ENFORCED]` The same holds for `unsupported` (no parser for
the producer kind).

A reader therefore derives the effective code list with
`JUnitDeclaredObservedCheck.EffectiveCodes(outcome, reason, storedCodes)`:

- outcome `malformed` → `["malformed"]`;
- any other outcome → the stored codes in canonical order (empty for `unsupported`,
  which explains itself through `reason`).

The `reason` on a refusal row is the parse's own code word when it has the
column's shape (`^[a-z][a-z0-9_]{0,63}$`), and otherwise the outcome's own word, so
a hostile or unknown string cannot fail the row and hide the refusal.

## Replay determinism (AC7)

- The producer id is synthetic and per report: `junit-<artifact id>`. (The plan's
  `junit:<raw_report_id>` spelling is impossible: both `event` and `producer_slot`
  pin `producer_id` to `^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$`.) The slot is created
  already closed and declared, because no producer ever finalizes a report.
- The sequence is the testcase's document position, so the same bytes and the same
  parser version always produce the same `(producer_id, sequence)` identities,
  the same event ids, the same order and the same payload hashes.
- A second parse's insert conflicts on `(org_id, run_id, producer_id, sequence)`
  (`on conflict do nothing`). Each conflicting row's stored `payload_hash` is
  compared with the one this attempt computed: identical hashes make the whole
  attempt a no-op (zero events); a different hash refuses the attempt
  transactionally instead of overwriting immutable evidence.
- The `raw_report` key is `(artifact, parser release)`, so a new
  `parser_version` writes its own row beside the old one, leaving the old row
  untouched. The event set stays the first accepted parse's: a changed payload
  under a new release refuses the second attempt rather than replacing evidence.
- A testcase the mapper left unmapped still occupies its document position, but the
  declared pair is the written set's own: `declared_last_sequence` is the highest
  sequence written and `declared_count` the events written. An unmapped testcase
  **between two mapped ones** therefore leaves an internal hole, which the
  reconciler reads as its `count_mismatch` (fail closed). A trailing unmapped
  testcase leaves none — one mapped testcase at position 1 stores sequence 1 and
  declares `(1, 1)` — so only the report records it, as `unmapped_case`.
  The two vocabularies stay distinct: the report's discrepancy codes
  (`count_mismatch`, `unmapped_case`, `duplicate_case`, `status_mismatch`) are the
  mapper's own, while the reconciler's reasons (`count_mismatch`, `unknown_status`,
  `artifact_missing`, `setup_error`, `missing_shard`, `missing_result`) are the
  run-level verdict, and only those decide completeness.

## Symbol index

One row per mapper symbol; `Value` is the constant's literal value, as the
doc-drift test compares it. Every row is a real symbol of the mapper assembly and
every constant of the listed types has a row.

| Symbol                                       | Value             | Meaning                                                              |
| -------------------------------------------- | ----------------- | -------------------------------------------------------------------- |
| `PytestJUnitMapper.PytestParserId`           | `junit-pytest`    | the `report_parser.parser_id` of the dialect                         |
| `PytestJUnitMapper.PytestVersion`            | `1.0.0`           | the dialect release; a behaviour change is a new version             |
| `PytestJUnitMapper.MaxEchoedIdentityLength`  | `256`             | the most characters of `classname`/`name` echoed into a case         |
| `PytestJUnitMapper.TruncationMarker`         | `[truncated]`     | what replaces the tail of an echoed value over the cap               |
| `PytestJUnitMapper.TestCaseElement`          | `testcase`        | the element one case is read from                                    |
| `PytestJUnitMapper.TestSuiteElement`         | `testsuite`       | a declaring element (root, or a child of a `testsuites` root)        |
| `PytestJUnitMapper.TestSuitesElement`        | `testsuites`      | the root whose undeclared counters are summed from its direct suites |
| `PytestJUnitMapper.ClassnameAttribute`       | `classname`       | identity hint 1                                                      |
| `PytestJUnitMapper.NameAttribute`            | `name`            | identity hint 2                                                      |
| `PytestJUnitMapper.TimeAttribute`            | `time`            | the seconds attribute behind `duration_ms`                           |
| `PytestJUnitMapper.DocumentSlackBytes`       | `1048576`         | extra room for a document rebuilt from the parse child's records     |
| `PytestStatus.Failure`                       | `failure`         | the raw and element word of a failed assertion                       |
| `PytestStatus.Error`                         | `error`           | the raw, element and normalised word of an unexpected exception      |
| `PytestStatus.Skipped`                       | `skipped`         | the raw, element and normalised word of a skip                       |
| `PytestStatus.Passed`                        | `passed`          | the raw word of a testcase with no outcome element                   |
| `PytestStatus.Failed`                        | `failed`          | the normalised value of `failure`                                    |
| `JUnitDiscrepancies.UnmappedCase`            | `unmapped_case`   | the hint resolved to no planned execution; no event                  |
| `JUnitDiscrepancies.DuplicateCase`           | `duplicate_case`  | a repeat resolved to an already-mapped execution                     |
| `JUnitDiscrepancies.CountMismatch`           | `count_mismatch`  | declared `tests` differs from the document's testcases               |
| `JUnitDiscrepancies.Malformed`               | `malformed`       | the derived code of a refused document; never stored                 |
| `JUnitDiscrepancies.StatusMismatch`          | `status_mismatch` | a declared status counter differs from the observed count            |
| `PlannedExecutionIndex.MaxClassnameLength`   | `512`             | the longest `classname` inspected before it is unresolved            |
| `PlannedExecutionIndex.MaxNameLength`        | `512`             | the longest `name` inspected before it is unresolved                 |
| `PlannedExecutionIndex.MaxClassnameSegments` | `32`              | the most dotted segments a `classname` may carry                     |
| `ParsingLimits.MaxDepth`                     | `64`              | the deepest element nesting the profile accepts                      |
| `ParsingLimits.MaxReportBytes`               | `67108864`        | the profile's hard report ceiling, in bytes (64 MiB)                 |
| `ParsingLimits.ParseTimeout`                 | `30`              | the wall-clock seconds one parse may take                            |
