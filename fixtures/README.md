# fixtures/

Golden reports and expected normalised output per producer (00 §5.2). Populated by the ingestion adapter stories in plan 04 and used by contract/normalisation tests.

Three directories hold vectors asserted by more than one runtime, which is why they live here rather than inside a single test project:

- `canonical/` — the Evidgate golden documents (manifest, policy, candidate, evidence) with their canonical text and `sha256:<hex>` digest inline (01.3.2-T4.2, AC5);
- `rfc8785/` — the RFC 8785 reference input/output pairs published with cyberphone/json-canonicalization (Apache-2.0, see its README) (01.3.2-T4.1, AC5). Both `tests/Canonicalization.UnitTests` and `packages/cli/src/canonical.test.ts` read them; `.prettierignore` keeps the pairs byte-exact.
- `contracts/` — the Corvus↔Ajv validator parity corpus (01.3.2-T2): `vectors/<schema-key>/` with `*.verdict.json` sidecars plus `divergence/` regressions. It has its own format (see `fixtures/contracts/README.md`) and is _not_ a `<domain>/<name>/` fixture corpus — both loaders skip it because those directories carry neither `meta.json` nor an `input.*` file.

## `<domain>/<name>/` fixture corpus (01.6.1-T3)

One corpus, loaded identically from .NET and TypeScript (AC6):

- `tests/Shared/FixtureLoader.cs` — linked shared source; adopting test projects add a `Compile` link the same way they pick up `ContainerImages.cs`. `FixtureLoaderTests` lives in `tests/Foundation.IntegrationTests`.
- `packages/test-fixtures` — `loadFixture` + `loadAllFixtures` (same contract, Node side) and `goldenFile` helpers asserting RFC 8785 canonical equality.

### Layout

```
fixtures/<domain>/<name>/          # <name> may nest deeper, e.g. reports/junit/sample
  input.*                          # exactly one file named input.<ext>; ext is the kind's natural format
  expected.json                    # optional; RFC 8785 canonical bytes (no trailing newline)
  meta.json                        # required
  README.md                        # optional provenance note
```

`meta.json` is a JSON object with **exactly** four fields — no missing, no extra, no non-strings, no empties:

| field      | rule                                                                                                                                 |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| `domain`   | names the directory under `fixtures/` (`reports` for `reports/junit/sample`)                                                         |
| `kind`     | the producer/format the fixture pins (`junit`)                                                                                       |
| `source`   | provenance — `handcrafted`, a generator name, or a licensed upstream URL (00 §5.7 S3: synthetic data only, never real customer rows) |
| `added_by` | the plan task that added the fixture (`01.6.1-T3.2`)                                                                                 |

### Loaders refuse unknown shapes

`Load`/`loadFixture` throw (`FixtureShapeException` / `FixtureShapeError`) on: an empty or whitespace-only path (reported as `<empty>: fixture directory is empty` / `<empty>: fixtures root is empty`); a missing directory; no `meta.json`; a `meta.json` that is not an object, has a missing/extra key, a duplicated member name, or a non-string/empty/null value (a present-but-null field is a value refusal — "must be a non-empty string" — not a key-set refusal, on both runtimes); no `input.*` file; more than one `input.*` file; an `input` file with no extension; any other file or directory in the fixture directory (a fixture's own `README.md` is the one allowed extra); malformed `expected.json`; UTF-8 BOM in any corpus file (`meta.json`, `expected.json`, or the `input.*` payload — both loaders check the raw bytes, so a BOM is refused loudly on both runtimes even though `File.ReadAllText` alone would strip it and `readFileSync(…, 'utf8')` alone would keep the U+FEFF). `LoadAll`/`loadAllFixtures` walks a corpus root recursively and treats a directory as a fixture only when it contains `meta.json` or an `input.*` file, so byte-exact vector directories that are not fixtures (`rfc8785/input`, `contracts/vectors/*`) are skipped, while a malformed fixture — detected as a candidate — always fails loudly. Entries are returned sorted by corpus-relative path in ordinal (code-unit) order in both runtimes.

`expected.json`, when present, must be the **canonical byte sequence** of its content (RFC 8785, like `rfc8785/output/*`): the TypeScript `goldenFile` helpers enforce this byte-for-byte. The .NET loader validates that it parses as JSON and is BOM-free; it deliberately does not re-canonicalize (no JCS implementation is linked into test projects — canonical byte-exactness is asserted where `canonicalize` 5.1.0 lives). `.prettierignore` excludes the canonical `expected.json` files.

Duplicate member names are refused by both loaders, in either JSON file, so a document like `{"domain":"a","domain":"b",…}` never loads on one runtime and loads on the other. The two detect it differently — .NET forces the check while parsing (`System.Text.Json` defers it to the first access of the object) and TypeScript scans the raw text, because `JSON.parse` is last-wins and a reviver only ever sees the collapsed object — but the outcome is identical, including for an escaped spelling (`"dom\u0061in"`), which decodes to the same member name on both sides.

### Sample

| fixture                 | proves                                                                                                                                                                                                     |
| ----------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `reports/junit/sample/` | JUnit XML → normalised events; loaded by `FixtureLoaderTests` (.NET) and `loadFixture.test.ts` (TS); both pin `expected.json` at `sha256:722cd0645ed9bc4ee524514cf4856c8e718c21beb23a2941393efacd7ee2090d` |

Fixture review checklist (per 00 §5.7 S3): synthetic ids/emails only; provenance recorded in `meta.source`; `expected.json` regenerated through the canonicalizer, never hand-prettified.
