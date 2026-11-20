# large/100k-tests

The 100 000-test report of 04.1.3 (AC8). The repository stores the generator and the small files here, never the report: `scripts/dev/gen-large-report/gen-large-report.mjs` writes it from a seed in about a tenth of a second.

- `meta.json`, `input.json`, `expected.json` — a loader-shaped fixture (01.6.1-T3): `input.json` holds the generator parameters (seed, test counts), `expected.json` the pinned `sha256:` and size of the full 100 000-test report and of a 1 000-test report, plus the `budget` member described below. The loaders allow no other file here, so there is no separate `budget.json` or checksum file.

```text
node scripts/dev/gen-large-report/gen-large-report.mjs --out-dir <dir> [--tests 100000] [--seed 1]
```

It writes `<dir>/input.xml` (pytest `junitxml`, accepted by the `junit-pytest` parser) and prints `{tests, seed, bytes, sha256}`. The output depends on nothing but `--tests` and `--seed`: an integer-only PRNG, no clock or locale, `\n` line endings.

The loaders have no checksum rule, so the tests load this directory, generate the report from `input.json` and compare the bytes with `expected.json` themselves (`largeFixture.test.ts`, `LargeFixtureLoaderTests.cs`).

## Budget

`expected.json` `budget` has two states:

- `{"status":"unset"}` — no timing run has recorded numbers yet. Both loaders accept it, so the generator and checksum tests do not wait on a measurement.
- `{"status":"measured", ...}` — `measured_on` names `date` (`YYYY-MM-DD`), `host` and `sha` (8+ hex digits of the measured commit); `headroom` holds the `wall` (5) and `memory` (2) factors; `measured` and `limits` hold exactly five integer metrics: `parse_wall_ms`, `reconcile_wall_ms`, `walk_wall_ms`, `child_peak_working_set_bytes`, `parent_inflight_retained_bytes`.

Every limit must be at least its measured value and at least `ceil(measured x headroom)`, taking `wall` for the three `*_wall_ms` metrics and `memory` for the two byte metrics; the factors are fixed at `wall` 5 and `memory` 2. Both loaders (`largeFixture.test.ts`, `LargeFixtureLoaderTests.cs`) reject an `unset` budget that carries any member besides `status`, a measured budget with a missing member or metric, a different headroom factor, a limit below its measured value or below `measured x headroom`, the dropped `parent_peak_working_set_bytes` metric, or an unknown status; both accept `{"status":"unset"}` and a complete measurement.

`Tenancy.IntegrationTests.LargeReportBudgetTests.LargeReport_100k_ParseReconcileCompare_WithinBudget` (trait `Category=Budget`, a label only: it runs in the default test stage) generates the 100 000-test report and measures three phases — parse, reconcile and the attempts page walk — then fails with the measured numbers when one is over its limit. The parent process's peak working set is printed beside the numbers for diagnosis but is not asserted; the asserted memory metrics are the child's peak working set and the bytes the parent retains in flight.

To re-measure, run that test on a quiet host and copy the printed `measured` numbers into `expected.json` `budget`: `status: measured`, `measured_on` naming the run, and `limits` at `measured x headroom`, rounded up. While `budget` is `unset` the test still measures, prints the numbers and fails with "budget unset; record these numbers", so one run yields the values to record. Measurements are on demand only: no CI nightly workflow runs this test yet.
