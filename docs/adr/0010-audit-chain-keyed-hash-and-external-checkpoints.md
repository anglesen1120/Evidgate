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

- The digest is unkeyed: `AuditHashing.EntryHash(prevHash, row)` returns `SHA256.HashData(prevHash || canonical)` where `canonical` is RFC 8785 JSON of the row fields; the canonical fields do not include any key id (`src/BuildingBlocks/Persistence/Audit/AuditHashing.cs:25-41`, `:49-59`).
- The shipped table is `audit_log` with `seq`, `prev_hash` and `entry_hash` (`migrations/0015_identity_audit_log.sql:12-15`, `:39-40`); `seq` is dense per org and the first row has `prev_hash` of 32 zero bytes. The 11.5.2 plan and the T8 ruling call the same columns `audit_event` and `row_hash`. This ADR uses the shipped names and treats the plan names as the same objects (naming reconciliation is an open item).
- `audit_log_chain_guard` (`migrations/0015_identity_audit_log.sql:89-117`) only checks that a new row extends the tip (`seq`, `prev_hash`, `created_at`); it holds no secret, so a database-privileged actor can bypass or replace it.
- The verifier recomputes the same unkeyed hash from the same database (`src/BuildingBlocks/Persistence/Audit/AuditChainVerifier.cs:65`; run by `VerifyAuditChainTaskHandler`, `VerifyAuditChainTask.cs:57`). Its three blind spots are carried row `Audit chain verifier blind spots (S105 review: G1, G2, G4)`: G1 tail truncation is undetectable without an external anchor, G2 an emptied chain is invisible to the sweep, G4 malformed metadata aborts verification.
- Key custody seam: `ISecretProvider` / `EnvironmentSecretProvider` resolve only names declared in the closed list `SecretNames` (`src/BuildingBlocks/SharedKernel/Secrets/`); in V1 the provider is an environment-variable lookup, so separation between identities is a property of what each deployment injects, not of the provider. 00 §5.7 S3 and §11 require secrets from the environment or secret store and never in logs. Object-storage seam: `IObjectStorage` / `S3ObjectStorage` over `AWSSDK.S3` (`src/BuildingBlocks/ObjectStorage/IObjectStorage.cs:18`, `S3ObjectStorage.cs:32`), which exposes no conditional put today (F19).
- Rotation runbook: `docs/runbooks/key-rotation.md` §2 lists six key classes, each with one active slot (§2.3 has no overlap and no live reload), and no row for an audit key; the two new rows are added by 11.5.2-T2.3, not by this task.

Primitive and store facts (DECISIONS §6): HMAC-SHA-256 exists in-box in .NET 10 ✅ (F15); ECDSA P-256 exists in-box ✅ (F16); S3 `If-None-Match: *` conditional create ✅ (F18) with ⚠️ residue on the pinned package and the dev store (F19, F20); SeaweedFS Object Lock is documented ✅ but not run on the dev image ⚠️ (F21); production store write-once support ⚠️ unverifiable until O3 (F22); Ed25519 absence in .NET 10 ⚠️ (F17).

## Decision

1. **Keyed chain.** New audit rows store `entry_hash = HMAC-SHA-256(chain_key[key_id], key_id || prev_hash || canonical)`, where `key_id` is its 2-byte big-endian value and `canonical` is the RFC 8785 input the chain uses today (00 §7); binding the id into the MAC means an attacker without the key cannot relabel a row. The writer calls `System.Security.Cryptography.HMACSHA256` (F15) through an `IAuditChainSigner` port that returns the digest and the `key_id`; the key never reaches SQL. Rows get an additive nullable `chain_key_id smallint`; the app role cannot update it. A signer failure fails the request closed: no unsigned audit row, HTTP 503.
2. **Key custody.** The chain key and the checkpoint signing key are secret-store references only (00 §5.7 S3, §11): neither is stored in, derivable from or readable by the application database or the app DB role, and neither is in an image, a config file or a log. The two key classes are `audit_chain_key` and `audit_checkpoint_signing_key`, 32 random bytes for the HMAC key and a P-256 private key for the signer, generated outside the database. In V1 the secret seam delivers by environment injection per workload, so the separation below is a deployment property, proved by a `check-env-matrix` entry and a role-grant test in T2 rather than by the provider. Human access goes through 10.7.1-T4 break-glass.
3. **Identities.** Three workload identities, each in its own process and environment: the **writer** (any host that flushes audit rows through `AuditTrail`) holds the HMAC key only; the **exporter** holds the checkpoint signing key only, reads the chains through its own DB role with `SELECT` on `audit_log` and the org list (named in T2, not the app role) and writes to the bucket with `PutObject` only on `audit-checkpoints/v1/`; the **verifier** holds the HMAC keys read-only plus the checkpoint public keys and a read-only DB and bucket credential. The exporter must not share a process or environment with any host that writes audit rows, because one process holding both keys could forge and hide; T2 acceptance names the host for each identity.
4. **Key id on every row.** Every keyed row carries `chain_key_id` and every checkpoint carries `key_id` for its signature. `chain_key_id` null is valid only at `seq` up to the org's `legacy_unkeyed_through_seq` recorded in the first checkpoint; a null id after it, or an id the verifier does not know, is a verification failure and is never skipped.
5. **Rotation without rewriting history.** Rotation adds a new key id; the old key becomes verify-only and stays with the verifier until its last row is outside audit retention. Order: provision the new key to the verifier first, then switch the writer, so a new row never carries an id the verifier lacks. No existing row or checkpoint is changed. Because `SecretNames` is a closed list and runbook §2.3 documents one slot per class, this needs a key-set mechanism the repo does not have (a keyring secret or pre-registered versioned names) and does not follow the single-slot pattern; T2.3 chooses it and writes the runbook rows and drill.
6. **Compromise.** HMAC key: rotate immediately and record the first suspect `seq` per org in the next checkpoint; rows before it stay covered by checkpoints cut before the compromise and rows after it under the compromised id are unproven. Checkpoint signing key: revoke the key id, re-anchor the public-key list out of band, and treat checkpoints signed after the suspect time as unproven. The public-key list (`key_id` to public key, with the algorithm pinned) is held by the verifier outside the database and the bucket.
7. **Signed external checkpoints.** A scheduled exporter cuts a checkpoint from a repeatable-read snapshot without the writer lock: RFC 8785 JSON of `{checkpoint_seq, cut_at, key_id, prev_checkpoint_hash, orgs[{org_id, last_seq, last_row_hash}]}`, the first one also recording `legacy_unkeyed_through_seq` per org. It is a new object `audit-checkpoints/v1/<checkpoint_seq zero-padded>.json` plus a detached `.sig` over exactly those RFC 8785 bytes, in a bucket the application database role and the application's storage credential cannot delete from or overwrite. `prev_checkpoint_hash` hashes the previous checkpoint document only (not its signature, which is not canonical). Checkpoints hold ids and hashes only, no payload and no personal data.
8. **Anti-rollback rules the verifier enforces.** Per org, `last_seq` never decreases from one checkpoint to the next; an org present in an earlier checkpoint is present again or carries a deletion tombstone; the verifier keeps the highest `checkpoint_seq` it has seen in its own store, outside the database and the bucket, and a lower or reused number is a failure. A restore that shortens the chain therefore alerts by default (O-11.5.2-2); a sanctioned rewind would need an owner-authorised, separately audited `restore_epoch` act, which is the one way history can be shortened on purpose.
9. **Signature algorithm: ECDSA P-256 with SHA-256**, the algorithm the verifier pins. It is the asymmetric signature this repo can use with no package (F16); Ed25519 is not chosen because in-box ECDSA suffices and a library would need the ADR-0002 matrix path (its absence from .NET 10 is unverified, F17). The signature encoding (IEEE P1363 or DER) is decided in 11.5.2-T2.1, and verification from Node is a blocking check for accepting this ADR (the plan requires it, F17).
10. **Cadence: every 15 minutes** `[INFERENCE]`, matching the 15 min RPO in 00 §11 (the plan's default); it is the longest window in which a tail truncation can go unproven. The alarm `audit_chain_verified_through_age_seconds` fires when the newest verified checkpoint is older than 2 cadences (30 min). The verifier runs hourly, verifying every checkpoint signature and link and recomputing from the last verified checkpoint, with a weekly full walk `[INFERENCE]`.
11. **Write-once.** Conditional create (`If-None-Match: *`, F18) protects against an accidental second write by a well-behaved client; it does not stop a credential that omits the header, a later delete or a versioned shadow. Preventing overwrite and delete needs Object Lock or a deny-overwrite bucket policy.
    - Dev: SeaweedFS with a deny-delete policy, Object Lock documented (F21) and proved only by the T2.1 negative test, which must cover overwrite as well as delete; an existing dev bucket may need recreating because Object Lock can be enabled only at creation. Production is undecided with O3.
    - **Fallback** (F22): a separate account whose policy grants the platform `PutObject` only, plus versioning; this is detection-only, since a bad overwrite is caught by the signature and link checks rather than blocked.
12. **Rollback.** The exporter and the verifier are disabled independently; checkpoint objects stay; the `chain_key_id` column stays. Reverting to an unkeyed chain is not supported once keyed rows exist.
13. **What T2 and T3 will and will not detect.** Detected: an edited row, a recomputed unkeyed hash, a relabelled key id, tail truncation (S105 G1) after the next checkpoint, an interior gap, a modified or replaced checkpoint, a deleted interior checkpoint, an org dropped without a tombstone (G2), and malformed metadata reported as a mismatch at its `seq` (G4).
    - Detected only by the staleness alarm: deletion of the newest checkpoint.
    - Latency: new rows and tail truncation within one checkpoint plus one verifier run (about 75 min worst case); an interior edit before the last checkpoint may wait for the weekly full walk (up to 7 days) unless T3 re-checks pinned hashes each run.
    - Not detected: tampering before the first checkpoint. Retention of the chain and of the bucket (S105 G5) is the 11.5.1/11.8.1 decision, not made here (`[OPEN]`, no legal minimum asserted).

## Alternatives rejected

- **Unkeyed chain only (today).** Rejected: the attacker needs only database write access to recompute every hash, so the chain proves nothing against the actor it exists for (S1, ASVS-GAP-24).
- **DB-side trigger only.** Rejected: the guard in migration 0015 runs inside the same database; a privileged actor can disable or replace the trigger, and a trigger can neither hold a secret outside the database nor detect truncation.
- **External anchoring service (public transparency log or third-party timestamping).** Rejected for V1: it adds a hosted dependency and a data-residency question for no gain over a write-once bucket we already need, and it publishes org ids and hashes outside our control. `[INFERENCE]` it may return in V2 as an extra anchor on top of these checkpoints.

## Consequences

- Needs 02.7.1 to add `chain_key_id` and the `IAuditChainSigner` writer path (gap note appended to the 02.7.1 plan; T2.3 defines the contract, 02.7.1 implements it), the exporter and verifier (11.5.2-T2, T3), a key-set mechanism and two key rows in the key-rotation runbook (11.5.2-T2.3) and a contract change request to 00 §5.4/T8 and §15 (queued, not applied).
- A new secret to provision per environment and a new compromise class. Losing an HMAC key makes the rows under that id unverifiable, so key backup follows 10.7.1-T4.
- The shipped chain and `docs/security/audit-taxonomy.md` stay true (unkeyed) until 02.7.1 ships the writer change; ASVS-GAP-24 stays open until T2 and T3 ship.
- **Residual risk.** By attacker capability (the first column is what the attacker has; every row assumes the 15 min cadence):

| Attacker holds | Outcome |
|---|---|
| Database write only | Cannot forge a keyed row; edits and truncation are detected (Decision 13) |
| HMAC key and database write | Can forge rows written after the newest checkpoint; undetected until the next checkpoint pins them, up to about 15 min or an exporter outage |
| Database admin and bucket admin, no keys | Can roll back to an older checkpoint; caught only by the anti-rollback rules and the staleness alarm (Decision 8) |
| HMAC key and checkpoint signing key and bucket write | Can forge rows and later checkpoints undetected |
| A compromised verifier | Holds every HMAC key for the ids it verifies, so it can forge them |
| A compromised writer process | Appends false rows with valid HMACs; checkpoints prove only that earlier history was not rewritten |

- The first checkpoint signs the database's view without independent proof, so history before it (rows kept unkeyed) is protected only from that checkpoint forward. On a one-person team (O2) the owner holds every key, so separation of duties is technical only. Out of scope: tamper prevention against a fully compromised platform operator (11.5.2 §1 non-goals).

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
