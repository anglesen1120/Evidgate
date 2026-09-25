# ADR-0004 — S3 emulator for local development

## Status

accepted (2026-09-25)

## Date

2026-09-25

## Context

Object storage is S3-API (`AWSSDK.S3` 4.0, presigned URLs as bearer credentials with expiry) but the provider is still open (O3). 01 FR-01-11 requires the one-command local dev stack to include an S3-compatible emulator, and presigned-URL parity — including signed `Content-Length` and the checksum header (TL T18) — must hold so the upload path the plans specify for 06 runs identically locally and against the provider. DECISIONS.md §4.1 records the emulator choice via CR-01-1 (plans/01-foundation.md §13).

## Decision

- Local dev and integration tests use **SeaweedFS** (seaweedfs/seaweedfs) as the S3-compatible object store.
- **Scality CloudServer** is the documented fallback, switched in if 01.8.1-T1 finds presigned-URL parity gaps on SeaweedFS (signed `Content-Length`, checksum headers, multipart).
- Acceptance for the emulator is the 01.8.1-T1 suite: presigned PUT/GET via `AWSSDK.S3` with SigV4, checksum header and signed `Content-Length` (oversize PUT rejected), matching the provider path used by 06; a provider smoke test is still required before the pilot (10.6.1-T2).

## Alternatives rejected

- **MinIO** — AGPL-3.0 ✅ (LICENSE read 2026-09-25; `minio/minio` repo also archived as of 2026-04-24 ✅): fails the DECISIONS §4 licence rule, so it is rejected even though the emulator is dev-only — the rule applies to shipped code **and hosted services**, and a dev tool that trains the team on a different S3 dialect is a liability we need not take.
- **LocalStack Community** — the Community repository is archived (last push 2026-03-23 ✅) and the free replacement licence is non-commercial ✅ (DECISIONS §4.1, CR-01-1): fails the licence rule and is no longer maintained in its open form.
- **Running the dev stack against a real provider bucket** — puts a paid, network-dependent service inside every developer loop and risks customer-adjacent credentials on laptops; contradicts "no production data" in FR-01-11.
- **`localstack` paid tier / S3 mock libraries (e.g. in-process fakes)** — a fake cannot validate SigV4 presigning and checksum behaviour; parity requires a real S3 endpoint (plan 01 §5 row "Emulator behaves unlike the provider").

## Consequences

- `infra/` compose files (plan 10) run SeaweedFS; the endpoint is configured through `EVIDGATE__Storage__*` options so switching emulator or provider is configuration, not code.
- SeaweedFS is a dev/test dependency only: it never ships in a customer artifact; production object storage remains S3-API via `AWSSDK.S3`.
- If 01.8.1-T1 acceptance fails on SeaweedFS parity, the documented move is to Scality CloudServer — no new ADR is needed for that switch; picking a *different* emulator would need one.

## Evidence

- ✅ SeaweedFS licence: Apache-2.0 — https://github.com/seaweedfs/seaweedfs/blob/master/LICENSE (api.github.com `/repos/seaweedfs/seaweedfs/license`, fetched 2026-09-25; repo active, pushed 2026-09-25).
- ✅ Scality CloudServer licence: Apache-2.0 — https://github.com/scality/cloudserver/blob/development/9.5/LICENSE (api.github.com `/repos/scality/cloudserver/license`, fetched 2026-09-25; repo active).
- ✅ MinIO licence: AGPL-3.0 — https://github.com/minio/minio/blob/master/LICENSE (api.github.com `/repos/minio/minio/license`, fetched 2026-09-25); `archived: true` per api.github.com `/repos/minio/minio` (fetched 2026-09-25).
- ✅ LocalStack: `archived: true`, licence reported as `NOASSERTION`/`Other` — api.github.com `/repos/localstack/localstack` + `/license` (fetched 2026-09-25); DECISIONS §4.1 records "free tier non-commercial".
- ✅ DECISIONS.md §4.1 object-storage row; plans/01-foundation.md §3 emulator decision, §5 failure-mode row, §13 CR-01-1; TL T18 — repo, read 2026-09-25.

## Revisit trigger

- Provider choice lands (O3) and lacks parity with SeaweedFS on presigned-URL behaviour, OR SeaweedFS relicenses/abandons the S3 API → re-open; the fallback path is already documented.
