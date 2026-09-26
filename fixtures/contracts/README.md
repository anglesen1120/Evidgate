# fixtures/contracts/ — validator parity corpus

01.3.2-T2.3 / AC3 (T42): the accept/reject vectors both contract validators —
`Evidgate.BuildingBlocks.Contracts.Validation` (Corvus.Json.Validator, xUnit
suite `tests/Contract.Tests/ParityCorpusTests.cs`) and `packages/cli`'s Ajv 8
module (`packages/cli/src/validate.ts`, Vitest suite `src/parity.test.ts`) — must
produce the same verdict on every file here. A disagreement is a contract bug
and fails both builds; it is never a reason to edit a sidecar.

## Layout

```
vectors/<schema-key>/<vector>.json           # the JSON body under test
vectors/<schema-key>/<vector>.verdict.json   # its expected verdict (sidecar)
divergence/<schema-key>/<vector>.json (+ .verdict.json)   # seeded disagreement, off by default
```

`<schema-key>` is the schema file name without `.json` (`event-envelope.v1` for
`contracts/schemas/event-envelope.v1.json`, `capability.v1` for
`contracts/adapter/capability.v1.json`). Suites take it from the directory name
and cross-check it against the sidecar's `schema` field.

## Verdict sidecar

```json
{
  "schema": "event-envelope.v1",
  "valid": false,
  "errors": [{ "index": 3, "path": "/events/3/sequence", "reason": "minimum" }],
  "comment": "optional, human note"
}
```

- `valid` — expected verdict (required).
- `errors` — required when `valid` is false: the exact expected `details[]`
  multiset of 00 §9.2 rows. Comparison is over `(index, path, reason)` triples,
  order-insensitive; both runtimes must produce exactly this set — no extras.
  - `index` — 0-based position in `events[]` for item-level errors, `null` for
    document-level errors.
  - `path` — absolute JSON pointer to the offending member; `""` is the document
    itself (`body_too_large`, `invalid_json`). `required` and
    `additional_properties` paths end in the member name.
  - `reason` — the failed constraint as a lower_snake token: the schema keyword
    in snake_case (`maxItems` → `max_items`), or the synthesized tokens
    `invalid_json` (unparseable body), `body_too_large` (`x-max-bytes`
    exceeded, checked before parsing), and `truncated` (see below).
- `comment` — free text, ignored by both suites.

## Detail cap (`truncated`)

Each runtime collects at most **100** distinct mapped `details[]` rows. When a
further distinct row would exceed the cap, collection stops and a single
terminal marker `{ "index": null, "path": "", "reason": "truncated" }` is
appended as the **last** element — the verdict is still `valid: false`. The
cap applies _after_ the multiset described above: exactly 100 real rows means
no marker. Both runtimes implement the identical cap, marker and position so
they cannot drift; no corpus vector exceeds 100 errors, so the rule never
changes an existing sidecar.

`acc-NN-*` = accept vectors, `rej-NN-*` = reject vectors, `dv-NN-*` = divergence.

## Batch semantics (event-envelope.v1)

A document is the 00 §9.4 batch form iff it is an object carrying an `events`
member. A batch validates as envelope first (shape, `minItems`/`maxItems`,
`additionalProperties` — event members unchecked); item errors are reported per
event only when the envelope itself is valid. A document without `events`
validates as a single event (00 §9.4's single-event form).

## Seeded divergence (`divergence/`)

`divergence/event-envelope.v1/dv-001-seeded-disagreement.json` deliberately declares a wrong verdict
(`valid: true` for a document whose `schema_version` is 2). The `divergence/` tree is
excluded unless `EVIDGATE_PARITY_INCLUDE_DIVERGENCE=1` — under that flag both
suites pick it up and go red, which is how QA proves the corpus comparison is
armed (AC3's "a deliberately divergent vector fails both builds"):

```
EVIDGATE_PARITY_INCLUDE_DIVERGENCE=1 dotnet run --project tests/Contract.Tests -c Release
(cd packages/cli && EVIDGATE_PARITY_INCLUDE_DIVERGENCE=1 npx vitest run)
```

(without the flag, each suite still proves the trigger works by asserting that
validating the fixture disagrees with its sidecar.)
