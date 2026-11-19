# large/100k-tests

The 100 000-test report of 04.1.3 (AC8). The repository stores the generator and the small files here, never the report: `scripts/dev/gen-large-report/gen-large-report.mjs` writes it from a seed in about a tenth of a second.

- `meta.json`, `input.json`, `expected.json` — a loader-shaped fixture (01.6.1-T3): `input.json` holds the generator parameters (seed, test counts), `expected.json` the pinned `sha256:` and size of the full 100 000-test report and of a 1 000-test report, plus `budget: {status: unset}` until the budget test (04.1.3-T3.2) records numbers; nothing asserts the budget yet. The loaders allow no other file here, so there is no separate `budget.json` or checksum file.

```text
node scripts/dev/gen-large-report/gen-large-report.mjs --out-dir <dir> [--tests 100000] [--seed 1]
```

It writes `<dir>/input.xml` (pytest `junitxml`, accepted by the `junit-pytest` parser) and prints `{tests, seed, bytes, sha256}`. The output depends on nothing but `--tests` and `--seed`: an integer-only PRNG, no clock or locale, `\n` line endings.

The loaders have no checksum rule, so the tests load this directory, generate the report from `input.json` and compare the bytes with `expected.json` themselves (`largeFixture.test.ts`, `LargeFixtureLoaderTests.cs`).
