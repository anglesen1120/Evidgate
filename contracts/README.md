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
- `events/ordering.md` — the event ordering, restart and sequence-gap contract of
  `POST /v1/runs/{r}/events` (04.1.1-T4): arrival order is storage order, readers order by
  `(producer_id, sequence)`, gap detection is derived from `producer_slot.last_sequence`, and the
  first-event `queued → running` lock order. The envelope schema itself lives in
  `schemas/event-envelope.v1.json`.
- `evidence/artifact-kind-defaults.v1.json` — the static per-kind artifact
  defaults (media types + `max_bytes`, capture enabled) that apply only when a
  project has no `artifact_kind_policy` row, plus the 8 KiB cap on the
  `POST /v1/artifacts/upload-url` request body (00 §9.3, 06.1.2-T3, D-A9 /
  D-S46-Q1). A data file, not a schema: it sits outside `schemas/`,
  `adapter/` and `manifest/`, so neither runtime loader compiles it.
- `cli.md` — the `evidgate` CLI contract (04.2.1-T2, OI-04-06): exit-code
  registry, `EVIDGATE_*` configuration and precedence, token sources, stderr
  error codes and redaction rules. Owned across the 04.2.x stories.
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
  reserved for the routes that reach no tenant permission at all:
  `POST /v1/ci/token` and `POST /v1/webhooks/github`, which must set `x-auth`
  naming the credential (`github_oidc_jwt_or_ingest_token`,
  `github_webhook_signature`), and the pre-tenant browser routes
  `GET /v1/auth/oidc/{provider}/start` and `/callback` (00 §5.4.8, 02 §4.2),
  which take no credential at all and set `x-auth: anonymous` with
  `security: []`. 02.3.1-T2 extends the same pre-tenant browser surface with
  `POST /v1/auth/invite-token` (invite staging, `x-auth: anonymous`,
  `security: []`) and the explicit second-provider link
  (`POST /v1/auth/link/{provider}`, `GET`/`DELETE /v1/auth/link/pending`,
  `POST /v1/auth/link/confirm`): those four act only on the caller's own
  `__Host-evidgate_session` cookie, hold no catalogue permission and reach no
  tenant, so they are `none` with `x-auth: user_session` and
  `security: - sessionCookie: []` — which is also why the viewer and
  `ci_producer` deny matrices derive them as unpermissioned. The literal
  `authenticated` is reserved for `POST /v1/orgs` (02.1.1 §5): any signed-in
  user may create an org, because no tenant permission can exist before the
  tenant does; it sets `x-auth: user_session`. The server side is the
  allowlisted `[AuthenticatedMember]` handler gate (`Architecture.Tests`).
- `x-version` — `v1` or `v2`, matching the 00 §9.3 V2 tag.

Shared `components/parameters` carry the 00 §9.1 conventions: `cursor` and
`limit` (default 50, max 200), `Idempotency-Key` (≤ 128 chars, 00 §7),
`If-Match` and `X-XSRF-TOKEN`. A route whose ordering or bound differs declares
its own parameter component and names it in its 00 §9.3 row: `RunAttemptCursor`,
`RunEvidenceRevisionCursor`, `PolicyRevisionCursor`, and for
`GET /v1/suite-revisions/{r}/execution-plan` `ExecutionPlanCursor` with
`ExecutionPlanLimit` (default 200, max 500, 04.2.2-T3); the shared `Limit`
stays 50/200 and `check-contract-conventions` pins that. Mutations must reference `Idempotency-Key`
unless listed in the exemption list (below); every cookie-authenticated
mutation references `X-XSRF-TOKEN`, while bearer-only routes (e.g. the V2
agent job routes) must not (T38). The server enforces that header host-wide
(02.3.2-T1: middleware in `ApiHost`, so a new mutating route is covered the
moment it is served): a cookie-authenticated `POST`/`PUT`/`PATCH`/`DELETE`
without a validating pair answers 403 `csrf_failed`, the `POST`/`DELETE
/v1/auth/link/*` routes included. `POST /v1/auth/invite-token` is the one
exemption, because it _sets_ a cookie rather than authenticating with one (its
mandatory `application/json` body is the defence). All
error responses reference
`components/schemas/ErrorEnvelope`; a 429 and a 503 always carry `Retry-After` — the route's own
value where it has one (`EVIDGATE__LOGIN__RETRYAFTERSECONDS` on the pre-tenant auth routes),
otherwise the envelope filter's default `60` (00 §9.2).

`Idempotency-Key` exemptions (00-grounded, enforced by the spectral
`evg-mutation-idempotency` rule — keep this list, the function's
`IDEMPOTENCY_EXEMPT` map and the helper's `IDEM_EXEMPT` in sync):

- `POST /v1/ci/token` — `Idempotency-Key` not required: no side effect beyond
  the `ci_token_issue` row; `jti` single-use already rejects replays
  (02 §4 endpoint table, FR-02-6/02.4.1).
- `POST /v1/projects/{p}/ci-bindings`, `POST /v1/projects/{p}/ingest-tokens`,
  `POST /v1/ingest-tokens/{t}/rotate`, `POST /v1/projects/{p}/api-tokens` — the response carries a one-time
  secret (a binding challenge, an ingest token or an API token). The idempotency store persists
  response bodies, and a secret must never be stored in the clear (00 §5.7 S3), so
  these responses are never replayed and no key is honoured; a retried create makes
  a second secret the admin can revoke (02.4.1, 02.4.2, 02.4.4).
- `POST /v1/dev/session` — Development-only route: it only sets the
  session cookies of one fixed development user, so a repeat replaces the caller's own session.
- `POST /v1/webhooks/github` — replay-safe by provider `delivery_id` dedupe
  (00 §9.3).
- `POST /v1/runs/{r}/events` — per-event `event_id` dedupe is the arbiter; a
  batch-level key cannot cover per-event acks (00 §9.4).
- `POST /v1/drafts/{d}/pull-request` — idempotent by `(branch, commit)`
  (00 §9.3).
- `POST /v1/jobs/{j}/heartbeat` — naturally idempotent: the server extends
  only the current epoch (00 §9.3).
- `POST /v1/projects/{p}/policies` — the RFC 8785 content hash is the
  idempotency key: an identical body returns the stored revision (`200`), a
  different body is a new revision, never a conflict (07.1.1 §5).
- `PUT /v1/projects/{p}/retention` — naturally idempotent: the same body
  upserts the same two windows on `(org_id, project_id)`, so a replay creates
  no second row and cannot change the answer (06.4.1 §5).
- `PUT /v1/projects/{p}/artifact-kind-policy/{kind}` — the row is upserted on
  `(org_id, project_id, kind)`, so a replay stores the same five knobs and
  changes no answer; it is not a candidate for `Idempotency-Key` because the
  write is audited on every successful call — an identical repeated `PUT`
  writes another `artifact_kind_policy.set` row, recording the previous
  default it found. The same holds for the redaction rules the body may carry
  (06.2.2): a list replaces the kind's declared set, `[]` clears it and an
  omitted member leaves it untouched, so a replay stores the same set and the
  rules never accumulate.
- `POST /v1/github/installations/start` — no resource is created: the route
  draws a one-time `state` nonce (only its SHA-256 is stored, bound to the
  caller's org and user for ten minutes) and answers the install URL; nothing
  is bound until the callback consumes the state, so a retry mints a fresh
  state instead of replaying a response (02.4.3 AC1).
- `POST /v1/auth/invite-token`, `POST /v1/auth/link/{provider}` — no resource
  is created: the first validates the presented token and stages it in the
  short-lived `__Host-evidgate_invite` cookie, the second answers a state cookie
  and the provider's authorization URL (02.3.1-T2). A repeat rewrites the
  browser's own cookie and nothing else, so there is no body to replay and no
  key that could change the answer.
- `DELETE /v1/auth/link/pending` — `DELETE` of the browser's own staging is
  idempotent by contract: `204` whether or not a staged link existed
  (02.3.1-T2 Q4).
- `POST /v1/auth/link/confirm` — one `__Host-evidgate_link` staging buys one
  confirmation and is deleted on every terminal outcome, so a replay cannot
  write a second identity; the store itself converges on `already_linked`
  (02.3.1-T2 Q4, AC6).
- `DELETE /v1/auth/sessions/{s}`, `POST /v1/auth/sessions/revoke-all`,
  `POST /v1/auth/users/{u}/sessions/revoke-all`, `POST /v1/auth/logout`,
  `DELETE /v1/api-tokens/{t}` — revocation is a row state: the first call sets
  `revoked_at`, a repeat of the delete answers the same `404 not_found` on the
  dead row (the API token's answers the same `204`), a repeat of either
  `revoke-all` the same `204` — and no second audit row, because nothing was
  revoked — and a replay of a logged-out cookie is `401 session_revoked` — so a
  key could change nothing the second time (02.3.2-T2.1 §5, AC4/AC8; 02.3.2-T3).
- `PATCH /v1/me` — the body sets one column (`preferred_locale`) to one of two
  values, so a replay would write the same state, and the `If-Match` version makes
  a retry that re-sends the already-applied version answer `412
precondition_failed` instead of applying twice (02.3.1-T3).

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
