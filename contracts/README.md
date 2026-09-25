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
  `capability.v1.json` now; operation and configuration schemas follow in
  04.7.1-T1.
- `examples/` — documents used by the conventions gate.
- `.spectral.yaml` (repo root) — lint ruleset; custom rules live in
  `scripts/spectral/`. Enforced by `scripts/check-contract-conventions.sh`.

## Operation conventions

Every operation declares:

- `x-permission` — its 00 §9.3 permission (T51). The literal `none` is
  reserved for the two non-permission routes, `POST /v1/ci/token` and
  `POST /v1/webhooks/github`; those must set `x-auth` naming the credential
  (`github_oidc_jwt_or_ingest_token`, `github_webhook_signature`).
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
- `POST /v1/webhooks/github` — replay-safe by provider `delivery_id` dedupe
  (00 §9.3).
- `POST /v1/runs/{r}/events` — per-event `event_id` dedupe is the arbiter; a
  batch-level key cannot cover per-event acks (00 §9.4).
- `POST /v1/drafts/{d}/pull-request` — idempotent by `(branch, commit)`
  (00 §9.3).
- `POST /v1/jobs/{j}/heartbeat` — naturally idempotent: the server extends
  only the current epoch (00 §9.3).

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
