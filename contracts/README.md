# contracts/

Source of truth for wire formats: `openapi.yaml` (OpenAPI 3.1) and JSON Schemas
(00 §5.2). Consumed by C# DTO contract tests, the generated `packages/sdk` TS
client and the server-side JSON Schema validation (01.3.2).

## Layout

- `openapi.yaml` — OpenAPI 3.1 spec. Every 00 §9.3 endpoint row (V1 and
  V2-tagged) is a stub operation; `/internal/*` routes never appear here
  (00 §9.1). Servers mount under `/v1`.
- `schemas/*.vN.json` — versioned JSON Schema 2020-12 documents
  (`event-envelope.v1.json` per 00 §9.4).
- `adapter/*.vN.json` — adapter protocol v1 schemas (00 §10, 04 D6):
  `capability.v1.json` and `config.v1.json` (the run-create configuration
  document, T97).
- `adapter/operations/*.vN.json` — one schema per protocol operation
  (`discover`, `prepare`, `execute`, `collect`, `cleanup`); `execute` and
  `collect` reference the 00 §9.4 envelope by `$id` instead of copying it
  (04.7.1-T1.2). Negotiation rules: `adapter/README.md`.
- `manifest/*.vN.json` — `manifest.v1.json` is the
  `POST /v1/projects/{p}/manifests` request body (05.1.1-T1.1), referenced from
  `openapi.yaml` so the spec body and the schema file cannot drift.
- `examples/` — documents used by the conventions gate.
- `permissions.json` — the single source of every `resource:action` string
  (00 §2, 02 D2): the platform permission catalogue plus the nine built-in roles
  and their grants. Migration 0010 seeds it into `public.permission` /
  `public.role` / `public.role_permission`, and
  `scripts/generate-permissions.py` renders the C# `Permissions` constants from
  it (`scripts/check-permissions-drift.sh` fails when either side lags).
- `permissions.schema.json` — the JSON Schema 2020-12 shape of that file.
  Deliberately NOT under `schemas/`, `adapter/` or `manifest/`: both runtime
  loaders compile every `*.json` under those three roots, and this is a build-time catalogue, not a
  request body. Validated by `tests/Contract.Tests/PermissionCatalogueTests.cs`.
- `rls-exceptions.txt` — the RLS exception list (02.6.1-T1.4): one
  `public.<table> <platform|catalogue|mechanism>` entry per table that is not
  org-scoped, with a `#` reason. The single source both the CI lint (02.6.1-T2)
  and the boot-time isolation self-check read — the latter embeds this file into
  `Evidgate.BuildingBlocks.Persistence` at build time, so a published process
  cannot start without it.
- `.spectral.yaml` (repo root) — lint ruleset; custom rules live in
  `scripts/spectral/`. Enforced by `scripts/check-contract-conventions.sh`.

## Addressing a schema from another schema

Every `*.json` under `schemas/`, `adapter/` (recursively) and `manifest/` is
loaded by both validators — server (`ContractSchemaSet`, Corvus) and CLI
(`loadContractSchemas`, Ajv 8) — and keyed by file name without `.json`
(`event-envelope.v1`, `execute.v1`, `manifest.v1`). A schema references a sibling
by its canonical URI — `urn:evidgate:contract:<key>` — never by a relative path:
`execute.v1.json` uses
`{"$ref": "urn:evidgate:contract:event-envelope.v1#/$defs/event"}`. Both runtimes
resolve those references from the loaded set only (file-system and HTTP
resolution are off), and two files that would share a key fail the load.

## Operation conventions

Every operation declares:

- `x-permission` — its 00 §9.3 permission (T51). The literal `none` is
  reserved for the two non-permission routes, `POST /v1/ci/token` and
  `POST /v1/webhooks/github`; those must set `x-auth` naming the credential
  (`github_oidc_jwt_or_ingest_token`, `github_webhook_signature`). The literal
  `authenticated` is reserved for `POST /v1/orgs` (02.1.1 §5): any signed-in
  user may create an org, because no tenant permission can exist before the
  tenant does; it sets `x-auth: user_session`. The server side is the
  allowlisted `[AuthenticatedMember]` handler gate (`Architecture.Tests`).
- `x-version` — `v1` or `v2`, matching the 00 §9.3 V2 tag.

Shared `components/parameters` carry the 00 §9.1 conventions: `cursor` and
`limit` (default 50, max 200), `Idempotency-Key` (≤ 128 chars, 00 §7),
`If-Match` and `X-XSRF-TOKEN`. Mutations must reference `Idempotency-Key`
unless listed in the exemption list (below); every cookie-authenticated
mutation references `X-XSRF-TOKEN`, while bearer-only routes (e.g. the V2
agent job routes) must not (T38). All error responses reference
`components/schemas/ErrorEnvelope`; 429/503 carry `Retry-After`.

`Idempotency-Key` exemptions (00-grounded, enforced by the spectral
`evg-mutation-idempotency` rule — keep this list, the function's
`IDEMPOTENCY_EXEMPT` map and the helper's `IDEM_EXEMPT` in sync):

- `POST /v1/ci/token` — `Idempotency-Key` not required: no side effect beyond
  the `ci_token_issue` row; `jti` single-use already rejects replays
  (02 §4 endpoint table, FR-02-6/02.4.1).
- `POST /v1/projects/{p}/ci-bindings`, `POST /v1/projects/{p}/ingest-tokens`,
  `POST /v1/ingest-tokens/{t}/rotate` — the response carries a one-time secret
  (a binding challenge or an ingest token). The idempotency store persists
  response bodies, and a secret must never be stored in the clear (00 §5.7 S3), so
  these responses are never replayed and no key is honoured; a retried create makes
  a second secret the admin can revoke (02.4.1, 02.4.2).
- `POST /v1/webhooks/github` — replay-safe by provider `delivery_id` dedupe
  (00 §9.3).
- `POST /v1/runs/{r}/events` — per-event `event_id` dedupe is the arbiter; a
  batch-level key cannot cover per-event acks (00 §9.4).
- `POST /v1/drafts/{d}/pull-request` — idempotent by `(branch, commit)`
  (00 §9.3).
- `POST /v1/jobs/{j}/heartbeat` — naturally idempotent: the server extends
  only the current epoch (00 §9.3).

## Validation corpus (`fixtures/contracts/`, 01.3.2-T2)

The accept/reject vectors both runtime validators must agree on live in
`fixtures/contracts/` — see `fixtures/contracts/README.md` for the vector and
verdict-sidecar format. Parity is a gate: a vector the Corvus (xUnit) and Ajv
(Vitest) suites disagree on fails both builds.

## Versioning and deprecation (AC5)

- **Breaking change ⇒ new major**: a new `/vN` server block in
  `openapi.yaml` (paths stay unprefixed) or a new `*.vN.json` schema file.
  Additive changes (new optional fields, new operations) are minor and stay
  in the current version.
- JSON Schema files are immutable once referenced: bump the `*.vN.json`
  suffix and the `$id`, never edit a shipped version in place.
- An adapter `protocol.major` the server does not support is rejected (00
  §10); capability schema bumps therefore follow the protocol major.
- Deprecation is announced in-field before removal: mark the operation
  `deprecated: true` in the spec (or document the sunset note in its
  `description`) and announce the removal version; only then may it be
  removed in a later major.
