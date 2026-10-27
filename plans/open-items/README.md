# Open-items register

> Built 2026-10-02 from every `[OPEN]` and ⚠️ in `DECISIONS.md`, `RECOMMENDATIONS.md`, `plans/00`–`12`, `docs/decisions/` and all feature plans. Each row gives a recommended default (`[INFERENCE]`), the information needed and where to get it, and the task that must not start before the row is resolved. Nothing in this register is a decision until the owner accepts it; no external or legal fact here is verified unless it carries ✅ with a source.

| File | Areas | Rows | Stage A rows (open / total) |
|---|---|---|---|
| [`01-core-identity.md`](01-core-identity.md) | DECISIONS, RECOMMENDATIONS, 00, 01, 02 | 60 | 3 / 7 |
| [`02-catalog-ingest-runs-evidence.md`](02-catalog-ingest-runs-evidence.md) | 03, 04, 05, 06 | 60 | 6 / 16 |
| [`03-policy-healing-web.md`](03-policy-healing-web.md) | 07, 08, 09 | 58 | 1 / 2 |
| [`04-ops-security-commercial.md`](04-ops-security-commercial.md) | 10, 11, 12, O1 pilot profile (includes the **counsel brief**) | 51 | 2 / 3 |
| [`05-product-phases-g1.md`](05-product-phases-g1.md) | 13.1.1, 13.1.2, 13.2.1 (CR-D-2 G1 authoring, S52, 2026-10-23), 13.2.2 (role profiles, S92, 2026-10-26), 13.3.1 + 13.4.1 (S112, 2026-10-27) | 62 | 0 / 0 |
| **Total** | | **291** | **12 / 28** |

Rows counted 2026-10-27 over every `| OI-` row of the five files; the Stage A column counts only the §2 rows, and a Stage A row is open until it carries a closing marker (`Closed`, `DECIDED`, `Resolved`, `delivered`) or is a `hand-over`. No folder-wide open/handed/closed totals are published here: per-row state is recorded in the row itself, and a row whose blocking task has landed but which records no decision stays open (the owner decides it, the merge does not — e.g. OI-02-08, OI-02-09, OI-05-15, OI-13-132, and the role-profile rows OI-13-301…308, whose plan calls their defaults `[INFERENCE]` and not owner decisions). Each row's own `Resolve before` and source cite stay authoritative over this summary.

## 1. How to use it

1. Before a task starts, search this folder for its id in the `Resolve before` column. If a row is still open, resolve it first.
2. Resolving a row means: accept the recommendation or write a different answer, then edit the **source** file the row names (area plan, feature plan or 00; a contract change goes to 00 first and gets a 00 §15 row), then mark the row `Closed (date, where)`.
3. A ⚠️ row is closed only by the check it names (official doc, command output or experiment), with the link or output recorded in the source file. Never close a ⚠️ by assumption.
4. A reviewer working from the spec package (`plans/features/SOLO-PATH.md` §3) treats an open row that blocks the reviewed task as a failed review.

## 2. Stage A blockers in build order (28)

Stage A order from `plans/features/SOLO-PATH.md` §2. "Type" tells you what closing the row needs.

| # | Row | Resolve before | Type | Recommended default (short) |
|---|---|---|---|---|
| 1 | OI-01-01 transactional DDL rule | 02.4.1-T2 and every later migration | verify (PostgreSQL 18 docs) + small lint | Migrator refuses statements that cannot run in a transaction block unless the file is marked non-transactional |
| 2 | OI-02-01 slug grammar | 02.1.1-T2.1 | owner decision | **Closed (D-A1)** `^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$` (no trailing hyphen, 2–63 chars) in the validator and a new migration replacing the 0004/0005 checks |
| 3 | OI-10-04 secret source on compose | 02.4.1-T3.3, 02.4.2-T1.1 | owner decision | **Closed (D-A2)** Environment injection through the one secret-provider interface (10.1.1-T2), `secret: true` + versioned names in `infra/config-matrix.json`, generated per environment, never committed |
| 4 | OI-02-06 ASVS level/version checklist | 02.4.1-T3 | verify (OWASP site) | ASVS L2 as the checklist for token and login endpoints, version pinned |
| 5 | OI-02-02 `ci_producer` TTL, no refresh | 02.4.1-T3 | owner decision | **Closed (D-A3)** 60 min (02 D7), no refresh; long jobs exchange a fresh GitHub OIDC token |
| 6 | OI-10-05 limiter classes + Kestrel/RateLimiter API | 02.4.1-T4.2 | contract edit + verify (Microsoft docs) | Per-IP for unauthenticated, per-org for reads/presign, per-project for ingest |
| 7 | OI-02-05 GitHub OIDC claim watch | 02.4.1-T4 | verify (GitHub docs, changelog) | Accept both `sub` formats; a test fixture per format |
| 8 | OI-02-03 pepper location, custodian, rotation | 02.4.2-T1.1 | owner decision | **Closed (D-A4)** Pepper in the secret store, owner is custodian, versioned `pepper_vN`, rotation per 10.7.1-T4 |
| 9 | OI-02-04 rotation overlap window | 02.4.2-T1.2 | owner decision | **Closed (D-A5)** 24 h |
| 10 | OI-03-01 sidecar question (closed by T55) | 03.1.1-T1 | paperwork | Mark closed in 03 §11 |
| 11 | OI-03-02 `requirement_ref` keep/drop + annotation key | 03.1.1-T1 | owner decision | **Closed (D-A6)** Keep, optional, ≤ 128 chars `[A-Za-z0-9._:/#-]`, metadata only, never gating; no requirement table in V1–V2 |
| 12 | OI-03-03 taxonomy enum values | 03.2.1-T1 | owner decision | **Closed (D-A7)** Accept the 03.1.4 lists as a closed V1 catalogue in `contracts/schemas/taxonomy.v1.json`; columns land in 03.2.1-T1 so the revision hash never changes later |
| 13 | OI-06-11 partition key `run_created_at` + composite FK | 04.1.1-T1, 05.2.1-T1, 05.3.3-T1 | TL decision + 00 edit | Adopt 06.4.6 default now (changes the 04.1.1-T2 `ON CONFLICT` target); a later change needs a data migration |
| 14 | OI-04-04 gzip expanded-size/ratio cap | 04.1.1-T3 | owner decision | **Closed (D-A8)** Expanded ≤ 8 MiB per batch, ratio ≤ 20:1, streaming abort; relax after the 10.6.1 load test |
| 15 | OI-05-03 run identity fields in 00 | 05.2.2-T1 | contract edit | **Closed 2026-10-13 (05.2.2-T1, S45)** 00 §7/§8.1/§15 carry the eight fields (`ref_kind`, `ref_name`, `pr_number`, `head_sha`, `base_sha`, `trigger_event`, `is_fork`, `unverified_ref`), the verified-claim trust rules and the wire `ref` block |
| 16 | OI-05-04 OIDC claim and payload names, fork token | 05.2.2-T1 | verify (GitHub OIDC docs) | **Resolved for the T1 field set 2026-10-13 (05.2.2-T1, S45)** verified claims are `ref` and `sha` only: the PR number is parsed from `refs/pull/<N>/merge` and that `sha` is GitHub's test-merge commit (never `head_sha`); `event_name`/`ref_type`/base sha/fork flag are not in the token, so `trigger_event`/`base_sha` are body-only; a fork PR exchange is refused 403 (02.4.1 AC3). Fork-token doc ⚠️ → 05.2.2-T3 |
| 17 | OI-04-05 OIDC token request from the CLI | 04.2.1-T1 | verify (GitHub Actions docs) | **Closed 2026-10-26 — delivered:** the CLI reads `ACTIONS_ID_TOKEN_REQUEST_URL` and requests a token with `ACTIONS_ID_TOKEN_REQUEST_TOKEN` at `audience=evidgate` (`packages/cli/src/auth/exchange.ts:216`, `exchange.test.ts:157-158`), which are the documented Actions variables; delivered by 04.2.1-T1 (#23, `8d89194`) |
| 18 | OI-04-06 `contracts/cli.md` exit-code registry | 04.2.1-T2 | contract edit | **Closed 2026-10-07 (04.2.1-T2)** `contracts/cli.md` created with 0/1/2/3 from 00 §9.3 and FR-07-13; replay codes 10–19 stay `[OPEN]` for 04.9.1-T4 |
| 19 | OI-04-01 Playwright reporter types + browserless e2e | 04.3.1-T1 | verify (Playwright 1.63 types) | Check `TestResult.retry`, steps, attachments in the pinned version |
| 20 | OI-04-08 bounded `GET /v1/runs/{r}` shape | 05.3.1-T1 | contract edit | Paged from day one, never an unbounded array; error codes logged in 00 §9.2/§15 |
| 21 | OI-03-08 `evidence_revision` outbox event name | 05.3.2-T5 | contract edit | `evidence_revision.created` `{run_id, revision_no, completeness, event_watermark}`, same transaction as the row |
| 22 | OI-06-02 `pending/` lifecycle expiry | 06.1.2-T1 | design | Correctness never depends on lifecycle: the 06.4.3 sweeper (running under the retention credential, row 23) removes `pending/` objects older than 24 h; lifecycle is a second layer |
| 23 | OI-10-07 storage credential split, one config-key family | 06.1.2-T1.1 | TL decision | Run 10.5.2-T1.1 (XS) first; app credential put/get/head/copy, no delete; deletes only by the retention credential (sweeper, retention job); one config-key family in `infra/config-matrix.json` |
| 24 | OI-06-03 per-kind artifact size caps | 06.1.2-T3 | owner decision | **Closed (D-A9)** screenshot 10 MiB, log 20 MiB, trace 100 MiB, html_report 100 MiB, video 200 MiB, raw_report 256 MiB |
| 25 | OI-07-02 07 dependency-id note | 07.1.1-T1 | paperwork | **Closed 2026-10-02** (sentence replaced in 07 §10) |
| 26 | OI-07-15 reserve `promotion.inconclusive_as` | 07.1.1-T1 | contract edit | Reserve the optional field, default `blocked`, in 00 §8.6 |
| 27 | OI-05-01 branch-scoped policy mode | 07.1.1-T1 | owner decision | **Closed (D-A10)** One policy per project with a `gating_refs` rule: default branch (and an optional release-branch glob) gate; other refs are evaluated as advisory |
| 28 | OI-05-02 `unverified_ref`/fork runs advisory only | 07.2.1-T1 | owner decision | **Closed (D-A11)** Yes: `unverified_ref` or fork ⇒ decision recorded `advisory = true`, never gating, no override in V1 |

Exact wording, sources and the information needed for each row are in the area files. Sizes and numbers in the defaults are `[INFERENCE]`.

## 3. Owner decisions taken

The eleven Stage A owner-decision rows (2, 3, 5, 8, 9, 11, 12, 14, 24, 27, 28) were accepted with their recommended defaults on 2026-10-02 and recorded as DECISIONS §8.1 D-A1…D-A11. Stage A now has 12 open rows: contract edits (13, 20, 21, 23, 26), done by the developer at the story's DoR; doc checks (1, 4, 6, 7, 19), each a short check at the start of the task; and paperwork or design (10, 22). Row 25 was closed earlier; row 18 was closed 2026-10-07 by 04.2.1-T2, row 15 on 2026-10-13 by 05.2.2-T1 and row 17 on 2026-10-26 by 04.2.1-T1, so none of them counts as open; row 16 is answered for the 05.2.2-T1 field set, with its fork-token ⚠️ moved to 05.2.2-T3, so it does not block Stage A either.

Outside Stage A, the items with the longest lead time are:

- **Counsel** (DPA, privacy notice, cross-border transfer, data-subject request deadlines, controller/processor). Brief in `04-ops-security-commercial.md`. Start during Stage B, before pilot one uploads anything.
- **Pilot one names** (OI-00-10, OI-12-01). Shortlist three teams during Stage A so the 4-week clock can start at the Stage B decision point.
- **O3 provider and region** (OI-00-01, OI-10-01). After the Stage B decision point, before any real customer data.
- **External reviewer** (SOLO-PATH §6). Every Stage A review needs one. Recommendation: one AI review session with the spec package for every story, plus one person for the security-sensitive ones (tenant isolation, auth, healing safety).

## 4. Source corrections already made from this register (2026-10-02)

`02.10.1` permission `org:write` → `organization:write`; RECOMMENDATIONS 02 Q4 `scrypt` → HMAC pepper; 01 §3 licence tool name; 07.1.1 AC1 now matches its command table (identical body returns the existing revision); 07.6.3 and 07.8.1 deletion now point to 06.4.4-T1; 09.1.1 spike ADR renumbered to `0007`; 03.9.1 reads `ref_kind`/`ref_name`; 06.3.4 trace-viewer licence marked ⚠️ Apache-2.0; 06.4.6 drop rule respects the largest project window; 07 §10 dependency note; README index links now point to the existing feature files.
