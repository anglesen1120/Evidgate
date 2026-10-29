# Audit and deletion rule: what the chain may carry, and what erasure leaves behind

Owner: 11.9.2-T2.1 (this delivery, S147). Scope: `audit_log` and its per-org hash chain
([02.7.1](../../plans/features/02-identity-tenancy/02.7.1-audit-trail.md), T8, [11.5.1](../../plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md)),
the payload contract the writer enforces (`src/BuildingBlocks/Persistence/Audit/`), and the erasure path owned by
11.9.1-T1 / 11.9.2-T1.4. Status: **the rule is written and linted here; the writer half is enforced in code, the
erasure half is a rule whose executor is another task, and one legal question stays `[OPEN]` for counsel.**

This page is the AC9 deliverable of [11.9.2](../../plans/features/11-security-compliance/11.9.2-data-subject-requests-pdpl.md):
`AuditRule_Doc_RequiredSections` (`tests/Architecture.Tests/AuditRuleDocumentTests.cs`) fails when any of the nine
required sections of this page is missing or empty, so the rule cannot be shortened away silently. AC10's enforcement
(`AuditPayload_ForbiddenKeys_Rejected`, `Erasure_ThenChainVerify_Ok_CheckpointsUnchanged`) is 11.9.2-T2.2.

Legend (AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. No legal fact
is asserted on this page; every statutory question is ⚠️ or `[OPEN]` pending counsel (11.8.1-T1/T2).

## 1. Scope and status

The rule exists because the chain is immutable: an audit row can never be updated or deleted, so a personal value
written into one stays for the life of the database, and the verifier — which only re-derives hashes — cannot detect
it. The only moment a payload can be refused is before it is queued, hashed and inserted.

| Claim | State | Evidence |
|---|---|---|
| A chain payload carries no personal data (identifying fields, free text) | ✅ for the C# writer; ⚠️ for SQL literals (§8) | §3, §4, §9 |
| The actor is an opaque `subject_id`, not an address or a name | ✅ | §2, `AuditActorResolver.cs:13` |
| Erasure tombstones the person and leaves the chain valid | ⚠️ rule only — executor is 11.9.2-T1.4 (11.9.1-T1 path) | §5 |
| Free text is forbidden in audit payloads | ⚠️ one allowlisted key is still free text (`reason`, gap G7) | §6 |
| Whether a pseudonymous id is still personal data | `[OPEN]` counsel | §7 |
| SQL-side definer writers reviewed; platform-scope events go to a separate table | ✅ reviewed / `[DECISION]` decided, table not built | §8 |

What this page does not do: it does not delete, null or rewrite any existing row (§5), it ships no migration, and it
does not decide the retention rule for `audit_log` (still `[OPEN]`, OI-11-11,
[audit-taxonomy](./audit-taxonomy.md) §5).

## 2. The actor is a pseudonymous `subject_id`

`audit_log.actor_id` is `text` (`migrations/0015_identity_audit_log.sql:30`) and the user actor is the user's UUID in
`"D"` form — no email, no display name, no prefix (`src/BuildingBlocks/Pipeline/Authorization/AuditActorResolver.cs:13`).
The other actor kinds carry an id too: a credential id or `project:<uuid>`, and the constant `operator`
(`AuditActorResolver.cs:14-17`); the closed set is `user`, `ci_producer`, `agent`, `platform_operator`, `system`
(`migrations/0015_identity_audit_log.sql:47`).

The id is a pseudonymous `subject_id` in the sense of [11.9.2](../../plans/features/11-security-compliance/11.9.2-data-subject-requests-pdpl.md)
§4 (`dsr_request.subject_id uuid`, UUIDv7): it does not name a person, and turning it back into a person needs the
`"user"` row — the very link erasure removes (§5), which is why the row can stay. The id is stable, so one person's
rows in two orgs are two separate per-org chains that share an actor id; an access request therefore reads only the
orgs it was scoped to (AC5). `[INFERENCE]`: an id that has been tombstoned keeps its rows attributable to each other
(the chain's `seq` links them) even after the link to a person is gone; that linkage is exactly the `[OPEN]` question
of §7.

## 3. Chain payloads carry no personal data

The hash input is fixed: `entry_hash = sha256(prev_hash || RFC 8785 canonical(id, org_id, seq, actor, action, object,
hashes, request_id, trace_id, metadata, created_at))`, one implementation (`AuditHashing`) for writer and verifier
([02.7.1](../../plans/features/02-identity-tenancy/02.7.1-audit-trail.md) §3, [audit-taxonomy](./audit-taxonomy.md) §6.1).
Every field in that tuple is therefore permanent the moment the row commits.

What each payload field may hold, and what it may not:

| Field | Allowed | Never |
|---|---|---|
| `actor_type` / `actor_id` | the closed actor set and an opaque id (§2) | an email, a name, a user agent |
| `action` | `object.verb` from the catalogue (`audit-taxonomy.md` §3) | prose |
| `object_type` / `object_id` | an id (`project:<uuid>`, an artifact, a request id) | a name, an address, a title a person wrote |
| `before_hash` / `after_hash` | a content address (RFC 8785 + SHA-256 of the object) | the object itself, or a personal field in clear |
| `request_id` / `trace_id` | an opaque request/trace identifier | a session cookie, a bearer token |
| `metadata` | an allowlisted key with a short enumerated value (`AuditMetadata.cs:35-57`) | any key on the forbidden list (§4), free text (§6) |

Two consequences a reader should not miss:

1. **The rule is "never write it", not "remove it later".** No row can be erased: `revoke update, delete, truncate …
   from evidgate_app` (`migrations/0015_identity_audit_log.sql:124`), there is no cascade, and the org itself is only
   soft-deleted ([audit-taxonomy](./audit-taxonomy.md) §5, `migrations/0004_identity_organization.sql:119-127`).
2. **The verifier cannot help.** `AuditChainVerifier` re-derives hashes and reports a `HashMismatch`; it has no notion
   of a personal value ([audit-taxonomy](./audit-taxonomy.md) §6.5). A personal datum that passes the writer stays
   undetected and unremovable — hence a write-time deny list rather than a scan.

## 4. Forbidden keys and how they are matched

The list lives once, as `AuditMetadata.ForbiddenKeys` (`src/BuildingBlocks/Persistence/Audit/AuditMetadata.cs:66-87`),
and this section mirrors it. A key is refused when it means one of:

`email`, `name`, `ip`, `user_agent`, `message`, `reason_text`, `comment`, `note`, `description`, `free_text`, and the
identifying field names `display_name`, `email_address`, `ip_address`, `full_name`, `phone`, `address`, `username`.

Matching is on the **whole key**, never a substring: the comparison form is the key in Unicode compatibility form
(KC), then its letters and digits only, upper-cased (`AuditMetadata.NormalizeKey`, `AuditMetadata.cs:102-118`), so
`Email`, `e-mail`, `E_MAIL`, `USER-AGENT`, `userAgent` and `USER AGENT` are all the same forbidden key, while
`email_domain` survives as `EMAILDOMAIN`, is not equal to `EMAIL`, and stays allowlisted (`AuditMetadata.cs:37`) — a
domain is not a person. A key that still holds a non-ASCII letter or digit after form KC is refused as forbidden
without consulting the list (fail closed): `еmail` with a Cyrillic `е` normalises to `ЕMAIL`, not `EMAIL`, while a
fullwidth `ｅｍａｉｌ` normalises to `EMAIL` and matches the list normally.

The walk is recursive through dictionaries and `KeyValuePair<TKey, TValue>` pairs (any key type, the key rendered as
its name), object properties, public instance fields (which is how a `ValueTuple` item is reached), sequences, and
`JsonElement`/`JsonNode`, depth-capped at `MaxPayloadDepth = 16` (`AuditMetadata.cs:93`; `EnsureNoForbiddenKeys`,
`AuditMetadata.cs:133`). A reference-type container already open on the current path is a cycle and is refused instead
of recursing without bound; the same instance reached twice on different paths is not a cycle. A refusal names the
offending key, its normalised form and the depth only — never an ancestor key and never a value, because either may be
the personal data that must not reach a log line. The exception's parameter name is the caller's fixed name for the
payload (`metadata`, `audit entry`, or `payload`), never a caller-expression capture: capturing the call site would copy
an inline dictionary literal — its values included — into the message.

Enforcement entry points:

- `AuditMetadata.Validate` runs the deny list **before** the allowlist, so `Email` is reported as a forbidden
  personal-data key rather than as "not on the allowlist" (`AuditMetadata.cs:145-160`); the bounded null/count checks
  run first because they need no traversal.
- The writer runs it twice, and both gates are before the chain. `AuditTrail.Describe`
  (`src/BuildingBlocks/Persistence/Audit/AuditTrail.cs:65-74`) validates the payload when the entry is described, so a
  forbidden key throws before it is queued; `AuditTrail.FlushAsync` (`AuditTrail.cs:124-132`) walks the metadata of
  every queued entry again as the last gate before `lock_timeout`, the advisory lock, the hash and the inserts, so a
  payload mutated after `Describe` (the same dictionary instance, changed in place) still cannot enter the permanent
  record (11.9.2-T2.2). `AuditPayload_MutatedAfterDescribe_RefusedAtFlush` proves that second gate.
- The guard is a C# guard: a payload built as a JSON literal inside SQL is invisible to it (§8).

Proven by `AuditPayload_ForbiddenKeys_Rejected` and `AuditPayload_MutatedAfterDescribe_RefusedAtFlush` (AC10), with the
pure walker cases in `AuditUnitTests`. Adding a key to the list is a review decision, like adding one to the allowlist
(`AuditMetadata.cs:34`), and the list and this section move together.

## 5. Erasure is a tombstone, not a row deletion

A data-subject erasure never touches `audit_log`. What it changes is the person's own record: the personal fields of
the `"user"` row — `email`, `display_name`, `last_login_at` (`migrations/0041_identity_user.sql:71-82`) — plus the
person's data in the stores the 11.9.1-T1 verified-deletion path owns (artifacts and their metadata, 11.9.2 AC8).

The rest of the person belongs to the executor of 11.9.2-T1.4 on that path: the provider-reported address on each
identity row (`user_identity.email_at_provider`, `migrations/0041_identity_user.sql:109`) and the address on a pending
invite (`membership.invited_email`, `migrations/0011_identity_membership.sql:60`). The AC10 test
`Erasure_ThenChainVerify_Ok_CheckpointsUnchanged` erases only the three `"user"` columns and then asserts the chain, so
it does not erase those two columns — the executor does.

What stays behind, and must:

- **The id.** The `"user"` row's `id` survives as a tombstone, so the row still exists for referential purposes while
  carrying no personal field. `user.email` is `text not null` with a 3–320 character check
  (`migrations/0041_identity_user.sql:74,85`), so "null every personal field" is not literally possible for the
  address: the erasure executor writes a non-personal tombstone value in its place. The concrete value and its mapping
  belong to the executor (11.9.2-T1.4 on the 11.9.1-T1 path), not to this rule.
- **Every audit row.** Identical `seq`, `prev_hash`, `entry_hash`, `created_at`, `metadata`. Because no personal field
  was ever in the chain (§3), erasure has nothing to rewrite: no re-hash, no re-link, no checkpoint delta.
- **Every checkpoint.** A checkpoint is a signed statement about a chain prefix (11.5.2; ADR-0010 `proposed`, so no
  checkpoint exists in code today — `[INFERENCE]` that the first checkpoint implementation will sign `(org_id, seq,
  entry_hash)` and nothing personal). Erasure therefore must not and cannot change one.

**Scope of this section.** It covers a *data-subject* erasure — one person's record inside a tenant that keeps
running. Whole-org (tenant) deletion is a different path, owned by 02 §5 and 11.9.1, and it is **not** settled here:
the plan sentences that read "tenant deletion keeps the minimum (actor, action, object hash) and strips other
fields" (02.7.1 `:89`) and "tenant deletion removes org rows per deletion SLA" (11.5.1 `:108`) state an intended
outcome, while the app role holds no UPDATE or DELETE on `audit_log`
(`migrations/0015_identity_audit_log.sql:124`) and no purge path exists today (`[audit-taxonomy](./audit-taxonomy.md)`
§5, `:189`) — so, read against these rows, those two sentences are intent, not an implementation this page endorses.
That gap is tracked separately (carried row "Audit retention and erasure rule missing (S105 review: G5)") and
nothing in this section authorises rewriting a chain row for either path.

The property the rule buys: resolution from an audit row to a person requires the `"user"` row, and after erasure that
row no longer holds a person — while the chain stays verifiable end to end. Proven by
`Erasure_ThenChainVerify_Ok_CheckpointsUnchanged` (AC10): erase, then run the 11.5.1-T1.2 / 11.5.2-T3 chain
verification, which must answer `ok`, and no checkpoint must change.

## 6. Free text is forbidden in audit payloads

A free-text key is an unbounded write into an immutable table: a handler can put a name, an address, a complaint
narrative or a support thread into it, nobody can review that content at the call site, and it can never be removed.
The deny list therefore carries the free-text spellings `message`, `reason_text`, `comment`, `note`, `description`
and `free_text` (§4) — those six, and no compound of them, because matching is whole-key (§4) — and the metadata
allowlist is meant to admit enumerated values only ([audit-taxonomy](./audit-taxonomy.md) §8 item 5).

One allowlisted key is still free text today, and this page does not pretend otherwise: `reason`, up to 256 characters
with only the credential-shape regex applied (`AuditMetadata.cs:47,32`; gap **G7** in
[audit-taxonomy](./audit-taxonomy.md) §7.2). Receiver: 02.7.1-T2.2 (drop the key or close its vocabulary to an
enumerated set). Until that lands, an emitter that writes `reason` must pass a code, never prose — and a review that
sees prose in a `reason` value treats it as the defect it is, not as a grey area.

## 7. Does a pseudonymous id stay personal data? `[OPEN]`

`[OPEN]` — counsel (11.9.2 §7 and §11, O-11.9.2 list; 11.8.1-T1/T2). Evidgate is the processor and the controller is
the customer, so the classification question is not ours to settle `[INFERENCE]`, and this page therefore does not
state either answer as verified. Both readings are real:

- **Not personal data once the link is gone.** The id is a UUIDv7 with no name attached, and after erasure (§5) no row
  in Evidgate maps it to a person; pseudonymisation of this kind is the usual example of data that a deletion request
  does not reach.
- **Still personal data.** The id singles a person out and stays linkable across rows and orgs, and other stores may
  still hold the link (memberships, usage ledger), so the id remains personal data in the controller's hands.

The rule is written to hold under both readings, which is why the legal answer is not on the critical path: no
personal field enters a chain payload (§3), the id stays so the chain stays valid (§5), and the access path already
returns the rows where the subject is the actor (11.9.2 AC5), so an access request is answered under either
classification. What a "still personal data" answer would add is a stated legal basis and retention for the
pseudonymous rows — a decision for 11.8.1/11.9.1, not a change to the chain or to this rule.

## 8. SQL-side definer writers and the platform table

Two SQL-side paths reach `audit_log` and neither takes a caller-supplied payload, so the C# deny list (§4) never sees
them; their fixed key sets are reviewed here instead:

| Path | What it writes | Review |
|---|---|---|
| `evidgate_resolve.session_revoke_one` — the admin session revoke, `migrations/0050_identity_session_revoke_audit.sql`, re-created with ids by `0051_identity_session_revoke_audit_ids.sql` | a complete canonical row built in SQL, including `metadata = {"target_user_id": "<the member>"}` (`0050:257-279`, `0051:265-290`) | ✅ ids only: no identifying field, no free text, satisfies §3/§4 by construction (`0050:21` states the same) |
| the pre-tenant login/link definers of `0042_identity_login_resolvers.sql` and `0044_identity_login_linking.sql` | **no audit row at all** — `audit_log` is org-scoped and these run before a tenant scope exists; each carries the explicit marker `-- lint: audit-deferred 02.7.1-T2.2` (`0042:65`, `0044:144`) | ✅ a deferral, not an omission; adding a row there is 02.7.1-T2.2's change and must follow §3/§4 |

The control that remains is review: `scripts/check-rls.py` lints the definer's hardening (search path, grants, RLS),
not the payload keys, so a change to those JSON literals must be read against §3/§4 in review. `[DECISION]` keeping
the key sets literal and fixed inside the definers, rather than passing metadata in from the caller, is what makes that
review tractable.

The `platform_security_event` table is **not** part of this rule and not part of the chain. `security_event.org_id` is
`NOT NULL`, so an org-less (platform-scope) security/healing event cannot go there; the owner decided on 2026-10-29
(D-S137-OWNER, `DECISIONS.md` §8.2) that such an event goes to a separate `platform_security_event` table, never to a
nullable `org_id`. That table is a separate carried item (carried row "Platform security event table (D-S137-OWNER)"),
it holds no chain rows, and it is not built by this PR.

## 9. Enforcement map and claim summary

| Rule | Enforced by | Where |
|---|---|---|
| No identifying key or free-text key in a payload | `AuditMetadata.ForbiddenKeys` + recursive walk, called from `AuditTrail.Describe` and again on the metadata in `FlushAsync` before the chain | `src/BuildingBlocks/Persistence/Audit/AuditMetadata.cs:66-87,133`; tests `AuditPayload_ForbiddenKeys_Rejected`, `AuditPayload_MutatedAfterDescribe_RefusedAtFlush` |
| Enumerated metadata only, short values, no credential-shaped value | allowlist, `MaxKeys`, `MaxValueLength`, credential regex | `AuditMetadata.cs:35-57,145-181` |
| The actor is an opaque id | `AuditActorResolver` | `AuditActorResolver.cs:13-17`; column check `migrations/0015_identity_audit_log.sql:47` |
| Erasure leaves the chain and checkpoints unchanged | rule (§5); the executor must not touch `audit_log` | test `Erasure_ThenChainVerify_Ok_CheckpointsUnchanged` |
| Immutability of written rows | `revoke update, delete, truncate` from the app role; owner has SELECT/INSERT policies only | `migrations/0015_identity_audit_log.sql:124` and its policy block |
| This page keeps all nine sections | `AuditRule_Doc_RequiredSections` | `tests/Architecture.Tests/AuditRuleDocumentTests.cs` |
| SQL definer payloads | review of the fixed key sets (§8) | `migrations/0050`, `0051`, `0042:65`, `0044:144` |

Claim summary:

- **Met:** a chain payload cannot carry a forbidden key through the C# writer — the deny list is recursive,
  whole-key, case/separator-insensitive in Unicode form KC and refuses a non-ASCII homoglyph outright, and it is
  checked before the entry is queued and again on the metadata at flush; the actor is an opaque id;
  audit rows cannot be updated or deleted; the fixed SQL key set is ids only; the platform-scope shape is decided.
- **Met as a rule, not as code:** erasure tombstones the person and leaves the chain and any checkpoint unchanged —
  the executor is 11.9.2-T1.4 / 11.9.1-T1.
- **Open:** `reason` is still free text (gap G7, receiver 02.7.1-T2.2); whether the pseudonymous id remains personal
  data is `[OPEN]` for counsel (§7); the `platform_security_event` table is carried, not built; the `audit_log`
  retention rule stays `[OPEN]` (OI-11-11).

How the claims were checked: the writer, the hashing, the actor resolver, the metadata guard and the verifier were
located with codebase-memory (`search_graph` on `AuditMetadata`, `AuditActorResolver`, the audit writer path) and read
at the cited lines in the S147 worktree; the migration cites were read from `migrations/0015`, `0011`, `0041`, `0042`,
`0044`, `0050` and `0051`. No build, test, lint or formatter was run for this page — it is documentation; the one code
claim this page makes about enforcement is the test named in AC10, which runs in the `test` stage.
