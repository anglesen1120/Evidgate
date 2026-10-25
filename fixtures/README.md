# fixtures/

Golden reports and expected normalised output per producer (00 §5.2). Populated by the ingestion adapter stories in plan 04 and used by contract/normalisation tests.

Four directories hold vectors asserted by more than one runtime, which is why they live here rather than inside a single test project:

- `canonical/` — the Evidgate golden documents (manifest, policy, candidate, evidence) with their canonical text and `sha256:<hex>` digest inline (01.3.2-T4.2, AC5);
- `rfc8785/` — the RFC 8785 reference input/output pairs published with cyberphone/json-canonicalization (Apache-2.0, see its README) (01.3.2-T4.1, AC5). Both `tests/Canonicalization.UnitTests` and `packages/cli/src/canonical.test.ts` read them; `.prettierignore` keeps the pairs byte-exact.
- `variant-hash/` — the variant identity corpus (04.3.2-T1.3, AC3): one `vectors.json` of parameter sets with their expected `sha256:<hex>` digest or refusal, read by `packages/adapter-playwright/src/variant.test.ts` (the reporter) and `tests/Tenancy.IntegrationTests/VariantGoldenVectorTests.cs` (the server). Format and provenance: `fixtures/variant-hash/README.md`.
- `contracts/` — the Corvus↔Ajv validator parity corpus (01.3.2-T2): `vectors/<schema-key>/` with `*.verdict.json` sidecars plus `divergence/` regressions, and `protocol-matrix/negotiation.v1.json`, the shared negotiation compatibility matrix of 04.7.1-T3 (AC6, T42). It has its own format (see `fixtures/contracts/README.md`) and is _not_ a `<domain>/<name>/` fixture corpus — both loaders skip it because those directories carry neither `meta.json` nor an `input.*` file.

One more directory is a test input for a repository script rather than a shared vector set:

- `ci-recipes/` — customer GitHub Actions workflows for the CI recipe lint (11.2.1-T1, AC1): the published fork-safe recipe `github-fork-safe.yml` plus `negative/` (one fixture per FR-11-2 rule) and `bypass/` fixtures, each naming its expected rule on an `# expect:` first line, run by `scripts/lint-ci-recipe.sh --self-test`. All synthetic; format: `fixtures/ci-recipes/README.md`. It is _not_ a `<domain>/<name>/` fixture corpus — both loaders skip it because no directory there carries `meta.json` or an `input.*` file.
- `threat-model/` — synthetic threat-model documents for `scripts/check-threat-model.sh --self-test` (11.1.1-T1.3): `ok/` must pass, each `negative/*.md` must fail with the substring in its sibling `*.expect`. Not a `<domain>/<name>/` corpus (no `meta.json`); format: `fixtures/threat-model/README.md`.
- `hostile/` — the hostile-input corpus of the 06.3.1 parser profile (06.3.2-T1.1, AC1): `xxe.xml`, `billion_laughs.xml`, a path-traversal archive, unsafe entry names (ZWJ, trailing dot), malformed archives (fake end-of-central-directory record in the comment, trailing bytes, truncation), the fail-closed media bodies and the 06.2.3 planted secret — indexed by `hostile/manifest.json`, with `zipbomb.zip`/`oversized.bin` generated at test time. Not a `<domain>/<name>/` corpus (no `meta.json`): a multi-file corpus cannot hold exactly one `input.*`. Driven by `tests/Tenancy.IntegrationTests/HostileFixtureTests`; format: `fixtures/hostile/README.md`. The same directory seeds the 06.3.2-T2.1 fuzz harness (`HostileFuzzTests`, nightly `fixtures-fuzz` stage, 10-minute budget), and `FuzzPromotion.Promote` writes a crash it finds back here as a new fixture plus manifest row — only under the explicit `EVIDGATE_FUZZ_PROMOTE=1` opt-in, which no CI job sets.

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

## `web-api/` — the dev fixture backend corpus (09.1.1-T12)

`web-api/<name>/` is an ordinary `<domain>/<name>/` corpus (exactly `input.json` + `meta.json`, `kind: api-response`, no `expected.json`), whose `input.json` is one recorded `/v1` response the web app can serve before the API exists (AC10):

- `operation_id` — an `operationId` of `contracts/openapi.yaml`. The directory name, not the operation, is the fixture's identity: a second response of the same operation is a second directory.
- `variant` — which response of that operation it is. `ok` is the answer the backend serves when no toggle is set, pinned at most once per operation; every other name is reached only through `FixtureToggles`. An operation may pin error states alone (`updateProject` has only its 412), in which case the un-pinned request answers 501 `fixture_not_found` rather than a fabricated success body.
- `status` — the status the backend answers (200, 403, 412 here), the body of every non-2xx row being the 00 §9.2 envelope.
- `headers` — the headers the backend sets before its own `Content-Type: application/json`; `X-Request-Id` only, and the error bodies echo that value as `error.request_id`.
- `latency_ms` — the delay before the answer, so loading states are reachable with no proxy and no throttle.
- `body` — the response body, and the part `apps/web/src/app/core/dev-fixtures/fixture-contract.spec.ts` validates against the response schema that `contracts/openapi.yaml` declares for (`operation_id`, `status`), so a fixture that drifts from the contract fails `npm run test -w apps/web`.

The client side of the corpus lives in `apps/web/src/app/core/dev-fixtures/`: `FIXTURE_ROUTES` maps method + path template to an `operationId`, `FIXTURE_CATALOG` imports every `input.json` by directory name, and `provideFixtureBackend()` answers SDK calls behind `provideApi()`'s interceptors in the `fixtures` build configuration only (`dev-backend.fixtures.ts` replaces `dev-backend.ts` there). A request that matches no route, or a variant with no document, answers 501 `fixture_not_found`. An injectable `FixtureToggles` (seeded from the `fixture.variant.<operationId>=<variant>` and `fixture.latency=<ms>` query parameters) overrides the variant and the per-document latency at runtime.

Synthetic data only (00 §5.7 S3): ids are fixed UUIDv7-shaped values (`018f0000-0000-7000-8000-0000000000NN`), timestamps are RFC 3339 UTC, and no value is JWT-like, key-like or credential-shaped (`scripts/check-no-secret-literals.sh`). No body value may exist as a string literal in the app or the generated SDK either, because `apps/web/scripts/check-no-fixture-code.mjs` greps the production bundle for every string value of 12+ characters outside the protocol slots (`headers`, `operation_id`, `variant`, `code`).

Two shapes are deliberate:

- `permissions-list/` is a 16-row catalogue page ordered by `code`, so its `next_cursor` is the last code of the page (the schema reserves `null` for the last page). `roles-list/`, `projects-list/` and `orgs-list/` are complete pages and carry `null`; `orgs-list/` has no cursor member at all. Role rows mirror `RoleView` and therefore carry no permission grants: `GET /v1/roles` never serves them, and the bundle probe above would otherwise grep every grant code.
- `runs-list/` is the canonical "missing shard + failure" UI fixture set of `plans/09-web-ui.md` §7: a `complete` + `PASS` run, the failing run as `incomplete` + `FAIL` (collection completeness and decision are independent fields, so a run with missing evidence is never `PASS`), a `collecting` run with a `pending` decision, a `canceled` run that never becomes complete, a `failed_subset` rerun of the failing run that is `INCONCLUSIVE`, and a `queued` fork PR run. Its `next_cursor` uses the `<utc ticks in hex>-<uuid>` form `KeysetCursor` issues, so the cursor pager has a second page to fetch: the fixture backend answers the document whatever `limit` the caller sends.

`permissions-list-settings-admin/`, `permissions-list-settings-audit-only/` and `permissions-list-settings-no-tab/` (09.7.1-T1) are the three grant sets the Settings tab strip is proved with, selected by `?fixture.variant.listPermissions=<variant>`: `settings-admin` holds every tab code of the strip (G1 and later phases) plus `membership:write`, `settings-audit-only` leaves `audit` as the one G1 tab and is the set the index redirect lands on, and `settings-no-tab` holds `organization:read` alone so the shell renders its empty state. All three are complete pages (`next_cursor: null`), and every row is copied verbatim from `contracts/permissions.json`.

`memberships-list/`, `memberships-list-empty/`, `membership-create-ok/`, `membership-create-invite-exists/`, `membership-remove-ok/`, `membership-remove-last-admin/`, `membership-remove-invalid-transition/` and `permissions-list-settings-members-readonly/` (09.7.1-T2) are the Members and roles tab's states. `memberships-list` is the page the tab opens with — one sole active `tenant_admin`, two other active memberships (one holding two roles, so the matrix has a multi-role row), one `pending` invite and one `removed` row — and `memberships-list-empty` (`?fixture.variant.listMemberships=empty`) its empty page. `membership-create-ok` is the invite's 201 and `membership-create-invite-exists` (`?fixture.variant.createMembership=invite-exists`) its 409 `invite_exists`. `membership-remove-ok` is the first bodyless document of the corpus (204, no `body`), `membership-remove-last-admin` (`?fixture.variant.removeMember=last-admin`) the 409 `last_admin` AC2 shows verbatim and `membership-remove-invalid-transition` (`?fixture.variant.removeMember=invalid-transition`) the 409 `invalid_transition` a `pending` invite answers. `permissions-list-settings-members-readonly` (`?fixture.variant.listPermissions=settings-members-readonly`) holds `membership:read`, `organization:read` and `role:read` but not `membership:write`, so the suite can prove the invite and remove controls are absent from the DOM. The three membership error variants are named after the state (`invite-exists`, `last-admin`, `invalid-transition`), like the `settings-*` rows, while their bodies carry the wire `code`.
