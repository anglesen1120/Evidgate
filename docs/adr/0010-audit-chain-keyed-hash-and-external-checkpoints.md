# ADR-0010 — Audit chain: HMAC-keyed hash, key custody and rotation, signed external checkpoints

Legend: ✅ verified with link · ⚠️ unverified · `[INFERENCE]` — mark every external claim with one of these.

## Status

proposed

Owner confirmation pending (O-11.5.2-1: checkpoint bucket residency and provider with O3/O4; O-11.5.2-2: whether a restore may rewind the chain; the `[OPEN]` change request to 00 §5.4/T8 and §15, queued as text in the 11.5.2 plan Notes; the `audit_log.chain_key_id` gap filed on 02.7.1). The decision is irreversible once keyed rows exist, so nothing here is accepted until the owner confirms. This ADR is task 11.5.2-T1 only; it owns story 11.5.2 AC1. AC2-AC11 belong to 11.5.2-T2 (exporter) and 11.5.2-T3 (verifier) and are not claimed here.

## Date

2026-10-28

## Context

Review finding S1 (`plans/REVIEW-2026-10-02.md` §2.4) and ASVS-GAP-24 (`docs/security/asvs-l2-mapping.md` §4, carried row `ASVS L2 gaps: 11.5.2-T1`) record that the audit hash chain lives inside the PostgreSQL database it protects and uses no secret, so anyone able to rewrite the audit rows can recompute every hash and nothing would notice.

Facts about the code on `develop` at da6a5001 (read from the files, not from plan text):

- The digest is unkeyed: `AuditHashing.EntryHash(prevHash, row)` returns `SHA256.HashData(prevHash || canonical)` where `canonical` is RFC 8785 JSON of the row fields (`src/BuildingBlocks/Persistence/Audit/AuditHashing.cs:49-59`).
- The shipped table is `audit_log` with `seq`, `prev_hash` and `entry_hash` (`migrations/0015_identity_audit_log.sql:12-15`, `:39-40`); `seq` is dense per org and the first row has `prev_hash` of 32 zero bytes. The 11.5.2 plan and the T8 ruling call the same columns `audit_event` and `row_hash`. This ADR uses the shipped names and treats the plan names as the same objects (naming reconciliation is recorded as an open item).
- `audit_log_chain_guard` (`migrations/0015_identity_audit_log.sql:89-117`) only checks that a new row extends the tip (`seq`, `prev_hash`, `created_at`); it holds no secret, so a database-privileged actor can bypass or replace it.
- The verifier `VerifyAuditChainTask` (`src/BuildingBlocks/Persistence/Audit/VerifyAuditChainTask.cs:57`) recomputes the same unkeyed hash from the same database. Its three blind spots are carried row `Audit chain verifier blind spots (S105 review: G1, G2, G4)`: G1 tail truncation is undetectable without an external anchor, G2 an emptied chain is invisible to the sweep, G4 malformed metadata aborts verification.
- Key custody seam: `ISecretProvider` and `EnvironmentSecretProvider` resolve only names declared in `SecretNames` (`src/BuildingBlocks/SharedKernel/Secrets/`); 00 §11 forbids secrets in images, config or logs. Object-storage seam: `IObjectStorage` / `S3ObjectStorage` over `AWSSDK.S3` (`src/BuildingBlocks/ObjectStorage/IObjectStorage.cs:18`, `S3ObjectStorage.cs:32`), which exposes no conditional put today (F19).
- Rotation runbook: `docs/runbooks/key-rotation.md` §2 lists six key classes and has no row for an audit key; the two new rows are added by 11.5.2-T2.3, not by this task.

Primitive and store facts (DECISIONS §6): HMAC-SHA-256 exists in-box in .NET 10 ✅ (F15); ECDSA P-256 exists in-box ✅ (F16); S3 `If-None-Match: *` conditional create ✅ (F18) but its pinned-package and dev-store behaviour are ⚠️ (F19, F20); SeaweedFS Object Lock is documented ✅ but not run on the dev image ⚠️ (F21); production store write-once support ⚠️ unverifiable until O3 (F22); Ed25519 absence in .NET 10 ⚠️ (F17).

## Decision

1. **Keyed chain.** New audit rows store `entry_hash = HMAC-SHA-256(chain_key[key_id], prev_hash || canonical)`, with the same RFC 8785 canonical input as today (00 §7). The writer calls `System.Security.Cryptography.HMACSHA256` (F15) through an `IAuditChainSigner` port that returns the digest and the `key_id`; the key never reaches SQL. Rows get an additive nullable `chain_key_id smallint` (null means a legacy unkeyed row); the app role cannot update it. A signer failure fails the request closed: no unsigned audit row, HTTP 503.
2. **Key custody.** The chain key and the checkpoint signing key are secret-store references only (00 §11, `ISecretProvider` and a new `SecretNames` entry per key class). Neither is stored in, derivable from or readable by the application database, the app DB role, an image, a config file or a log. The writer identity holds only the HMAC key, the exporter identity only the checkpoint signing key, the verifier identity only the HMAC key read-only plus the checkpoint public key. Human access goes through 10.7.1-T4 break-glass. The two key classes are `audit_chain_key` and `audit_checkpoint_signing_key`.
3. **Key id on every row.** Every keyed row carries `chain_key_id`; every checkpoint carries `key_id` for its signature. A row or checkpoint with an unknown id is a verification failure, never skipped.
4. **Rotation without rewriting history.** Rotation adds a new key id; the old key becomes verify-only and stays available to the verifier for as long as rows under it exist. New rows use the new id from the first write after the switch; no existing row or checkpoint is changed. Overlap: the writer switches first, the verifier gets the new key before the first row under it. Compromise: rotate immediately, record the first suspect `seq` per org in the next checkpoint, and treat rows written under the compromised id after that point as unproven (they are still covered by the checkpoints cut before it). The runbook rows and the staging drill land with 11.5.2-T2.3 and the 10.7.1 drill.
5. **Signed external checkpoints.** A scheduled exporter cuts a checkpoint from a repeatable-read snapshot without the writer lock: RFC 8785 JSON of `{checkpoint_seq, cut_at, key_id, prev_checkpoint_hash, orgs[{org_id, last_seq, last_row_hash}]}`, with the first checkpoint recording `legacy_unkeyed_through_seq` per org. It is written as a new object `audit-checkpoints/v1/<checkpoint_seq zero-padded>.json` plus a detached `.sig` to a bucket the application database role and the application's storage credential cannot delete from or overwrite. `prev_checkpoint_hash` links each checkpoint to the previous one. Checkpoints hold ids and hashes only, no payload and no personal data.
6. **Signature algorithm: ECDSA P-256 with SHA-256** (private key in the secret store, public key distributed to the verifier), chosen because it is the asymmetric signature this repo can use with no package (F16). Ed25519 is not used: its absence from .NET 10 is unverified (F17), and a library would need the ADR-0002 matrix path. `[INFERENCE]` The signature format (IEEE P1363 or DER) and the Node-side check are decided in 11.5.2-T2.1.
7. **Cadence: every 15 minutes** `[INFERENCE]`, matching the 15 min RPO in 00 §11 (the plan's default). This is the longest window in which a tail truncation can go unproven. The alarm `audit_chain_verified_through_age_seconds` fires when the newest verified checkpoint is older than 2 cadences (30 min). The verifier runs hourly with a weekly full walk `[INFERENCE]`.
8. **Write-once.** The bucket is enforced write-once by Object Lock where the store supports it and by conditional create (`If-None-Match: *`, F18) for the second-write refusal. Dev: SeaweedFS with a deny-delete policy; its Object Lock is documented (F21) but proved only by the T2.1 negative test. Production: undecided with O3; **fallback** (F22): a separate account whose policy grants the platform `PutObject` only, plus versioning.
9. **Rollback.** The exporter and the verifier are disabled independently; checkpoint objects stay; the `chain_key_id` column stays. Reverting to an unkeyed chain is not supported once keyed rows exist.
10. **What T2 and T3 will and will not detect.** Will detect: an edited row, a recomputed unkeyed hash, tail truncation (S105 G1), an interior gap, a deleted, modified or replaced checkpoint, an org emptied without a tombstone (G2), and malformed metadata reported as a mismatch at its `seq` (G4). Will not detect: tampering before the first checkpoint, because that history is protected only from the first checkpoint forward; a legitimate org deletion is expected to end in an `org_deleted` tombstone. Retention of the chain and of the bucket (the S105 G5 row) is the 11.5.1/11.8.1 audit-retention decision, not made here (`[OPEN]`, no legal minimum asserted).

## Alternatives rejected

- **Unkeyed chain only (today).** Rejected: the attacker needs only database write access to recompute every hash, so the chain proves nothing against the actor it exists for (S1, ASVS-GAP-24).
- **DB-side trigger only.** Rejected: the guard in migration 0015 runs inside the same database; a privileged actor can disable or replace the trigger, and a trigger can neither hold a secret outside the database nor detect truncation.
- **External anchoring service (public transparency log or third-party timestamping).** Rejected for V1: it adds a hosted dependency and a data-residency question for no gain over a write-once bucket we already need, and it publishes org ids and hashes outside our control. `[INFERENCE]` it may return in V2 as an extra anchor on top of these checkpoints.

## Consequences

- Needs 02.7.1 to add `chain_key_id` and the `IAuditChainSigner` writer path (gap note appended to the 02.7.1 plan), the exporter and verifier (11.5.2-T2, T3), two key rows in the key-rotation runbook (11.5.2-T2.3) and a contract change request to 00 §5.4/T8 and §15 (queued, not applied).
- A new secret to provision per environment and a new compromise class (HMAC key). Losing the HMAC key makes old keyed rows unverifiable, so the key backup follows 10.7.1-T4.
- The shipped chain and `docs/security/audit-taxonomy.md` stay true (unkeyed) until 02.7.1 ships the writer change; ASVS-GAP-24 stays open until T2 and T3 ship.
- **Residual risk.** An actor who holds both the HMAC key and the checkpoint signing key or the bucket's administrator rights can forge rows and checkpoints undetected. A compromised writer process can append false rows with valid HMACs; checkpoints only prove that earlier history was not rewritten. Out of scope: tamper prevention against a fully compromised platform operator (11.5.2 §1 non-goals).
- Rows from before the first checkpoint stay unkeyed: protected only from that checkpoint forward.

## Evidence

- Code facts: see Context (file and line each).
- Primitive and store facts: DECISIONS §6 F15-F22. Of the four required facts: (a) HMAC-SHA-256 ✅ F15; (b) asymmetric signature ✅ F16 (Ed25519 absence ⚠️ F17); (c) write-once ✅ documented for SeaweedFS but not run F21, ⚠️ for production F22; (d) conditional create ✅ in S3 and documented for SeaweedFS (F18, F20), ⚠️ on the pinned package F19.
- Plan: `plans/features/11-security-compliance/11.5.2-audit-chain-integrity-outside-its-own-database.md` §1-§3, §7, §8, §10; review `plans/REVIEW-2026-10-02.md` §2.4 and §5.4.
- **Prior art** (design shape taken, no text copied): the C2SP transparency-log checkpoint format, a signed text note over `origin / size / root hash` with a key name per signature ([c2sp tlog-checkpoint](https://github.com/C2SP/C2SP/blob/main/tlog-checkpoint.md), read 2026-10-28) → taken: a signed checkpoint over a canonical document with a key name/id.
- Prior art, second source: RFC 9162 Certificate Transparency v2 signed tree heads ([RFC 9162](https://datatracker.ietf.org/doc/html/rfc9162), page reachable 2026-10-28, not read in full ⚠️) → taken: a periodic externally published head.
- Prior art not followed: Merkle trees and inclusion proofs (they serve third-party auditors of a public log; here one verifier and per-org linear chains are enough and the existing T8 chain stays), and any public log or witness network (see rejected alternative 3).

## Revisit trigger

- O3 names the production store, or its write-once behaviour is verified (F22).
- The pinned `AWSSDK.S3` or the dev store fails the conditional-create check in 11.5.2-T2.1 (F19, F20).
- The owner rejects or amends the 00 §5.4/T8 change request, or decides O-11.5.2-2 so that a restore may rewind the chain.
- A third party needs to verify the chain without the HMAC key (then a Merkle form with inclusion proofs becomes relevant).
- .NET adds a native Ed25519 type and the owner wants it for checkpoints.
