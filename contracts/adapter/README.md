# contracts/adapter/ — adapter protocol v1

00 §10 defines protocol v1; this directory is its machine-readable form. An
adapter author implements against these JSON Schema (2020-12) files alone — no
TypeScript or C# source is needed.

| File                          | Direction        | What it is                                                                                                                                                                                                                           |
| ----------------------------- | ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `capability.v1.json`          | adapter → server | The capability document: `protocol {major, minor}`, `engine {family, version}`, `platforms[]`, `supports {…}`, `mode`. Registered on the adapter's first send (04.7.1-T2.3) and re-checked at job claim (05.7.x).                    |
| `config.v1.json`              | server → adapter | The **run-create configuration document** (T97): per-artifact-kind `capture_enabled`, `max_bytes` and allowlisted `redaction_rules`, decided server-side by 06.2.2 and delivered by 05.2.1. The adapter applies it _before_ capture. |
| `operations/discover.v1.json` | adapter → server | Result of the `discover` phase: the case ids, variant hashes and shard ids the adapter can see.                                                                                                                                      |
| `operations/prepare.v1.json`  | adapter → server | Result of the `prepare` phase: the workspace it provisioned, the engine it will run, the secret references it resolved.                                                                                                              |
| `operations/execute.v1.json`  | adapter → server | Result of the `execute` phase: the per-attempt events, each an 00 §9.4 envelope event.                                                                                                                                               |
| `operations/collect.v1.json`  | adapter → server | Result of the `collect` phase: the artifact inventory, reported as 00 §9.4 envelope events whose `artifact_ids` list the collected artifacts.                                                                                        |
| `operations/cleanup.v1.json`  | adapter → server | Result of the `cleanup` phase: the namespace, the resources removed and the ones it could not remove.                                                                                                                                |

An operation result document carries `error {code, message}` when the phase
failed. A partial failure may report the payload _and_ the error; a phase that
produced nothing (a failed `prepare`, an aborted `collect`) omits the payload
entirely — each of the five schemas therefore requires at least one of its phase
payload and `error` (`anyOf`). A phase failure is never a silent success (source
design §4: "reports setup errors separately", "reports separately if cleanup
fails").

## Emitting the capability document

The Playwright adapter (`@evidgate/playwright-reporter`, 04.3.1-T1) emits its capability
document as the **first spooled envelope** of a run: a `run.log` envelope at sequence 1
whose `ext` carries the document under `ext.capability` (`ext.schema` stays
`playwright/1`, the `<adapter-name>/<protocol-major>` value of 00 §9.4). `ext` is an open
object that requires only `schema`, so the carrier needs no new envelope member. The
document is validated against `capability.v1.json` in that package's tests (Ajv 8) and by
the server (Corvus) on registration (04.7.1-T2.3).

## Negotiation rules

Protocol v1 — the only version defined today — has major **1**: the schema
requires `protocol.major >= 1`, and the server's supported major set is the
constant 04.7.1-T2.1's `IProtocolNegotiator` carries. That negotiator is the
single implementation of the rules below — the registration path and (V2) the
`POST /v1/agents/jobs/claim` path both call it, so a version skew cannot be
caught on one path and missed on the other (04.7.1-T2.1/T2.3).

| Server major set       | Adapter major | Outcome                                                                                                                                      |
| ---------------------- | ------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| supports the adapter's | equal         | accepted, subject to the minor rules below                                                                                                   |
| does not support it    | different     | **rejected** — 422 `unsupported_protocol_major`, `retryable: false`; `details` names the adapter's major and the server's supported major(s) |

Within one major:

- **Adapter minor higher than the server's.** Registration succeeds. Support
  flags introduced by that newer minor are _unknown_ to this server: they are
  ignored, never evaluated, and recorded verbatim in `adapter_capability.supports`
  (jsonb), so a later server release can use them. A new root member is not a
  minor change (see **Unknown fields** below).
- **Adapter minor lower than the server's.** Accepted for the operations that
  minor declares. The server must not require a field introduced after the
  adapter's minor, which is why `supports.capture_control` is optional rather
  than required: an adapter that predates it simply omits it.
- **Unknown fields.** `supports` is extensible — an unknown flag is accepted as
  long as it is a boolean. The document root is closed (`additionalProperties:
false`): a member the protocol does not define is rejected 422
  `capability_invalid` with `details[].path` pointing at it.
- **`mode`** is `full` or `ingest_only` and immutable per capability row.
  `ingest_only` results are stored and viewable but can never produce a gating
  PASS unless the manifest is proven complete (00 §10, 04.7.1 AC4/AC7).
- **`supports.capture_control: false`** declares that the adapter cannot honour
  `config.v1.json`; the runs it produces carry the `capture_uncontrolled` label
  (set at run create by 05.2.1, T97). An absent or `true` value means the
  configuration document is delivered and must be honoured.
- **Engine version.** The adapter reports the exact engine version in
  `engine.version`; from V2 that version is pinned in the immutable JobSpec
  together with the engine image digest. Runners never resolve `latest` at run
  time (D8).

Errors use the 00 §9.2 envelope with `retryable: false`:

| Code                         | Status | When                                                                                              |
| ---------------------------- | ------ | ------------------------------------------------------------------------------------------------- |
| `capability_invalid`         | 422    | the document fails the schema; `details[].path` points at the offending field                     |
| `unsupported_protocol_major` | 422    | the adapter's `protocol.major` is not the server's; `details` names both majors                   |
| `capability_conflict`        | 409    | the natural key tuple conflicts with a stored row that declares a different `mode`/`supports` set |

The executable form of the table above is 04.7.1-T3 (the compatibility matrix
test); a behaviour change here fails that test, not this document.

## Addressing another schema file (`$ref`)

A schema addresses a sibling by its **canonical URI**, `urn:evidgate:contract:<key>`,
where `<key>` is the file name without `.json`. `operations/execute.v1.json`
therefore references the 00 §9.4 envelope instead of copying it:

```json
{ "$ref": "urn:evidgate:contract:event-envelope.v1#/$defs/event" }
```

Both runtimes load the same set and register it in memory under those URIs
(`ContractSchemaSet` on the server, `loadContractSchemas` in `@evidgate/cli`) and
both keep file-system and HTTP resolution **off**: a `$ref` to anything outside
the loaded set fails at load, never at first use and never by fetching. The
loaded set is every `*.json` under `contracts/schemas`, `contracts/adapter`
(recursively, so this directory's `operations/` is included) and
`contracts/manifest`, keyed by file name without extension; two files that would
share a key are a load error, because the key — not the path — is the address.

## Corpus and versioning

The accept/reject vectors both validators must agree on live in
`fixtures/contracts/vectors/<schema-key>/` (`capability.v1`, `config.v1`,
`discover.v1`, `prepare.v1`, `execute.v1`, `collect.v1`, `cleanup.v1`; the same
harness also carries `event-envelope.v1` from 01.3.2 and `manifest.v1` from
05.1.1) — see
`fixtures/contracts/README.md` for the vector and verdict-sidecar format. Parity
is a gate: a vector the Corvus (xUnit) and Ajv (Vitest) suites disagree on fails
both builds (T42, 04.7.1 AC5).

Files follow the `*.vN.json` rule of `contracts/README.md`: an additive change
stays in the current version, a breaking change is a new file — and for the
protocol itself a new `protocol.major`, since a capability document is rejected
by major.
