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
- `{"status":"measured", ...}` — `measured_on` names `date` (`YYYY-MM-DD`), `host` and `sha` (8+ hex digits of the measured commit); `headroom` holds the `wall` (5), `memory` (2, the parse child's peak resident set) and `retained` (1.05, the parent's in-flight retained bytes) factors; `measured` and `limits` hold exactly five integer metrics: `parse_wall_ms`, `reconcile_wall_ms`, `walk_wall_ms`, `child_peak_working_set_bytes`, `parent_inflight_retained_bytes`.

Every limit must be at least its measured value and at least `ceil(measured x headroom)`, taking `wall` for the three `*_wall_ms` metrics, `retained` for `parent_inflight_retained_bytes` and `memory` for `child_peak_working_set_bytes`; the factors are fixed at `wall` 5, `memory` 2 and `retained` 1.05. Both loaders (`largeFixture.test.ts`, `LargeFixtureLoaderTests.cs`) reject an `unset` budget that carries any member besides `status`, a measured budget with a missing member or metric, a different headroom factor (or a missing `retained`), a limit below its measured value or below `measured x headroom`, the dropped `parent_peak_working_set_bytes` metric, or an unknown status; both accept `{"status":"unset"}` and a complete measurement.

`Tenancy.IntegrationTests.LargeReportBudgetTests.LargeReport_100k_ParseReconcileCompare_WithinBudget` (trait `Category=Budget`, a label only: it runs in the default test stage) generates the 100 000-test report and measures three phases — parse, reconcile and the attempts page walk — then fails with the measured numbers when one is over its limit. The parent process's peak working set is printed beside the numbers for diagnosis but is not asserted; the asserted memory metrics are the child's peak working set and the bytes the parent retains in flight.

To re-measure, run that test on a quiet host and copy the printed `measured` numbers into `expected.json` `budget`: `status: measured`, `measured_on` naming the run, and `limits` at `measured x headroom`, rounded up. While `budget` is `unset` the test still measures, prints the numbers and fails with "budget unset; record these numbers", so one run yields the values to record. Measurements are on demand only: no CI nightly workflow runs this test yet.

### Recorded measurement (2026-11-20, host `taocon-computer`, sha `b8a5328c`)

Two granted timing runs, host alone, same sha; `expected.json` records the larger value of each metric.

| Metric                                                  | Run 1 (load_max 16.54) | Run 2 (load_max 12.38) | Limit             |
| ------------------------------------------------------- | ---------------------- | ---------------------- | ----------------- |
| `parse_wall_ms`                                         | 21319                  | 21481                  | 107405 (x5)       |
| `reconcile_wall_ms`                                     | 233                    | 232                    | 1165 (x5)         |
| `walk_wall_ms` (500 pages, 100 000 rows)                | 6313                   | 6257                   | 31565 (x5)        |
| `child_peak_working_set_bytes` (`/proc` VmHWM, a floor) | 72122368               | 71966720               | 144244736 (x2)    |
| `parent_inflight_retained_bytes`                        | 620598896              | 621848408              | 652940829 (x1.05) |

Why `retained` is 1.05 and not 2: the parent keeps every mapped case by design (about 620 MB, roughly 6 KB per case, the carried write-half finding), so a x2 limit would be 1.24 GB and could never see a return to buffering records. 1.05 leaves 31 MB over the measured value; the regression it exists to catch (the parent keeping the text, a char array and the UTF-8 bytes of every record, about +57 MB) exceeds that headroom. The retained figure is taken after `GC.WaitForPendingFinalizers()` and one `GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true)` per sample, and the two runs differ by 1.2 MB. The test prints the baseline, the measured values and the limits on every run.

If the retained assertion goes red without a code change, that is a defect of this test (a measurement that moved), not a host signature: do not rerun it away; find what the parent now keeps.
