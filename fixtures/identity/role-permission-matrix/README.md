# `identity/role-permission-matrix` fixture

The 00 §2 built-in role × permission matrix as the **plans** state it, one grant row per
`(role, permission)` cell, each carrying the plan citation it was derived from. It is the
expectation `SeedMatrixTests.Every_Role_Permission_Cell_Matches_The_Overview_Fixture`
compares the seeded catalogue against, on both the database rows and the real evaluator.

## Shape

`input.json`:

```jsonc
{
  "roles": [
    {
      "code": "viewer", // a built-in role code (00 §2)
      "kind": "member", // member | service | staff (W7-SEC-03)
      "grants": [{ "permission": "case:read", "source": "03 §4.5:122", "basis": "explicit" }],
    },
  ],
  "permissions": ["…"], // every catalogue code the plans name
  "sensitive": [
    // AC5: the plan-derived sensitive set
    { "permission": "ingest_token:write", "source": "00 §9.3:356" },
  ],
}
```

`meta.json` is the 01.6.1-T3.1 four-field shape (`domain`, `kind`, `source`, `added_by`);
`source` is `handcrafted` because the matrix is transcribed from the plan text, not
generated from `contracts/permissions.json`.

## How it was derived

The matrix is read from the plans, **never** copied from `contracts/permissions.json`
(which is the seed's source, so copying it would make the test tautological). The
precedence is the one 00 §2 itself sets:

1. **00 §2** (`plans/00-overview.md:46-60`) — the nine built-in roles and their limits
   (lines 50-58), and the permission strings paragraph (line 60) that names the holder of
   `healing:*`, `project:read`/`project:write`, `policy:read`/`policy:write`,
   `quarantine:write`,
   `ingest_token:read`, `service_identity:*`, `support:read`, `membership:read`,
   `sso:*`, `agent_pool:*`, `job:cleanup`, `webhook:*`, `billing:*`, `project:create`,
   `environment:write`, `security_event:read`.
   Line 60 also carries the four authoring strings of CR-D-2 / 13 §13 CR-2 with their
   default roles, and the line itself marks that mapping `[INFERENCE]` (it mirrors
   03 §4.5): `authoring:read` for every member role, `authoring:write` and
   `authoring:generate` for `qa_engineer`, `test_maintainer` and `tenant_admin` (generation
   spends AI credits, 08.5.5), `script:generate` for `test_maintainer` and `tenant_admin`
   only (it opens a pull request against the customer's repository, 13.5.1). The matrix
   transcribes those cells as `explicit` because the line names the roles, and a plan
   that later narrows them changes this fixture first.
2. **Where 00 §2 delegates, the owning plan wins.** 00 §2 line 60 hands the catalog
   strings (`case:*`, `suite:*`, `fixture:write`, `draft:*`, `ai_draft:request`) and
   their default role mapping to **03 §4.5** (`plans/03-catalog-authoring.md:116-136`).
   This is the one place the two disagree: 00 §2 line 60 lists `case:write` for
   `qa_engineer` and `test_maintainer`, while 03 §4.5:124 grants it to `test_maintainer`
   and `tenant_admin` only, and 03 §4.5:133 says `qa_engineer` "cannot write a case,
   suite or fixture". The delegation resolves it in 03 §4.5's favour, and the fixture
   follows 03 §4.5 (`case:write` for `test_maintainer` + `tenant_admin`, not
   `qa_engineer`).
3. **Where the plans name the string but not the holder**, the endpoint/contract row that
   defines the string is cited (`00 §9.3`, `02 §4.2`, `04 §1`, `04 §4`, `06 §4`). The holder is
   then the role the surrounding plan text assigns that endpoint to (e.g. `role:read` is
   "(any member)" at 02 §4.2:80, so every member role holds it; `artifact:read_sensitive`
   is excluded for `viewer` by 00 §2:50 "No secrets" and for `tenant_admin` by 00 §2:54,
   so the other three member roles hold it). These grants are marked `basis: inferred` in
   the section below, so the cells the plans leave implicit are visible.
4. **The `permissions` axis** is every catalogue code the plans name — all 63 codes of
   `contracts/permissions.json` appear somewhere in `plans/`, so the axis equals the
   catalogue's code list. The matrix test asserts that equality, so a catalogue code the
   fixture omits fails before any cell is compared.

Citations use `NN §S:L` = `plans/NN-*.md`, section `S`, line `L` (e.g. `03 §4.5:122` is
`plans/03-catalog-authoring.md` line 122); `NN.E.S §S:L` = the feature plan
`plans/features/*/NN.E.S-*.md`; `README T<N>:L` = the ruling row `T<N>` in
`plans/features/README.md`; and `02 FR-02-7:22` names the requirement id instead of a
section. Line numbers are the plan text at the time of writing; the section is the stable
reference.

## Grant provenance (`basis`)

Every grant carries a `basis` that says how strong its citation is. The matrix itself is
the same either way — `basis` only records whether the plan text _affirms_ the holder, so
the cells the plans leave implicit are visible instead of hidden behind a citation that
merely names the permission.

- **`explicit`** (the default when the field is absent) — the cited text states the grant:
  the permission code appears together with the role, either as the role's code or as a
  phrase the plan uses for exactly that role or role set (`"every member role"` = the five
  member roles, 00 §2:60; `"operator session"` = `platform_operator`, per 00 §9.1). A row
  of a role-mapping table counts, because the roles are the table's header (03 §4.5:121),
  so `03 §4.5:122` names `case:read` for all five member roles.
- **`inferred`** — the citation names the permission code but not the holder: an endpoint
  or contract row (`00 §9.3`, `02 §4.2`, `04 §4`, `06 §4`), or a line that names a
  _different_ role (00 §2:50 names `viewer`, which is why the other three member roles'
  `artifact:read_sensitive` is inferred). The holder is derived from the surrounding plan
  text, and `source` points at the owner of the authoritative text.

87 of the 132 grants are `explicit`; the 45 `inferred` ones are listed here. The
`RoleMatrixFixture.InferredGrants` property exposes the same list.

| Role                | Permission                | Citation (owner of the authoritative text) |
| ------------------- | ------------------------- | ------------------------------------------ |
| `agent`             | `event:write`             | 04 §1:10 + 00 §2:55                        |
| `platform_operator` | `support:read`            | README T59:118                             |
| `qa_engineer`       | `artifact:read`           | 00 §9.3:346                                |
| `qa_engineer`       | `artifact:read_sensitive` | 00 §2:50 + 06 §4:71                        |
| `qa_engineer`       | `artifact:write`          | 00 §9.3:334                                |
| `qa_engineer`       | `decision:read`           | 00 §9.3:336                                |
| `qa_engineer`       | `organization:read`       | 02 §4.2:77                                 |
| `qa_engineer`       | `role:read`               | 02 §4.2:80                                 |
| `qa_engineer`       | `run:cancel`              | 00 §9.3:331                                |
| `qa_engineer`       | `run:create`              | 00 §9.3:328                                |
| `qa_engineer`       | `run:read`                | 00 §9.3:330                                |
| `release_approver`  | `artifact:read`           | 00 §9.3:346                                |
| `release_approver`  | `artifact:read_sensitive` | 00 §2:50 + 06 §4:71                        |
| `release_approver`  | `decision:read`           | 00 §9.3:336                                |
| `release_approver`  | `organization:read`       | 02 §4.2:77                                 |
| `release_approver`  | `role:read`               | 02 §4.2:80                                 |
| `release_approver`  | `run:read`                | 00 §9.3:330                                |
| `tenant_admin`      | `artifact:read`           | 00 §9.3:346                                |
| `tenant_admin`      | `artifact:write`          | 00 §9.3:334                                |
| `tenant_admin`      | `audit:read`              | 02 §4.2:83                                 |
| `tenant_admin`      | `ci_binding:write`        | 02 §4.2:81                                 |
| `tenant_admin`      | `decision:read`           | 00 §9.3:336                                |
| `tenant_admin`      | `ingest_token:write`      | 00 §9.3:356                                |
| `tenant_admin`      | `membership:write`        | 02 §4.2:79                                 |
| `tenant_admin`      | `organization:read`       | 02 §4.2:77                                 |
| `tenant_admin`      | `organization:write`      | 02 §4.2:77                                 |
| `tenant_admin`      | `role:read`               | 02 §4.2:80                                 |
| `tenant_admin`      | `role:write`              | 02 §4.2:80                                 |
| `tenant_admin`      | `run:cancel`              | 00 §9.3:331                                |
| `tenant_admin`      | `run:create`              | 00 §9.3:328                                |
| `tenant_admin`      | `run:read`                | 00 §9.3:330                                |
| `test_maintainer`   | `artifact:read`           | 00 §9.3:346                                |
| `test_maintainer`   | `artifact:read_sensitive` | 00 §2:50 + 06 §4:71                        |
| `test_maintainer`   | `artifact:write`          | 00 §9.3:334                                |
| `test_maintainer`   | `decision:read`           | 00 §9.3:336                                |
| `test_maintainer`   | `organization:read`       | 02 §4.2:77                                 |
| `test_maintainer`   | `role:read`               | 02 §4.2:80                                 |
| `test_maintainer`   | `run:cancel`              | 00 §9.3:331                                |
| `test_maintainer`   | `run:create`              | 00 §9.3:328                                |
| `test_maintainer`   | `run:read`                | 00 §9.3:330                                |
| `viewer`            | `artifact:read`           | 00 §9.3:346                                |
| `viewer`            | `decision:read`           | 00 §9.3:336                                |
| `viewer`            | `organization:read`       | 02 §4.2:77                                 |
| `viewer`            | `role:read`               | 02 §4.2:80                                 |
| `viewer`            | `run:read`                | 00 §9.3:330                                |

Three citations deserve a note because the plan text is thin there:

- `platform_operator` × `support:grant` is `explicit` on the strength of 02.8.1 §1:11
  ("Operator actions … authenticated as `platform_operator` with an audited support
  grant") plus 02.8.1 §2:22 (`support:grant, operator session`); `platform_operator` ×
  `support:read` is `inferred` because the only text that names the holder is
  `plans/features/README.md:118` (ruling T59, "operators see their own grants").
- `agent` × `event:write` is `inferred`, and its citation is the area plan, not the feature
  plan: 04 §1:10 ("get a customer's real test results from CI (and later from an agent)
  into Evidgate as verified, ordered, deduplicated events") plus 00 §2:55 ("Claimed jobs
  and uploads in its pool") support the grant, and an area plan outranks a feature plan.
  `plans/features/02-identity-tenancy/02.5.1-agent-identity-per-tenant-and-pool.md` §8:71
  states the agent's permissions are exactly `{job:claim, job:execute, artifact:write}`,
  which **conflicts** with this cell; that line is stale and its owner is 02.5.1. The
  catalogue seeds `event:write`, so the cell stays.
- `tenant_admin` × `ingest_token:read`, `service_identity:read`, `service_identity:write`
  and `support:read` cite 00 §2:60, which compresses T59's four separate
  `` `x` (`tenant_admin`) `` entries into one list closed by a single `(`tenant_admin`)`
  after `membership:read`. The holder is named for the group, so the four are `explicit`;
  `plans/features/README.md:118` (T59) states each one separately.

## Sensitive set (AC5)

`sensitive` is the plan-derived set of codes that gate secret-adjacent data or return a
plaintext secret once — the flag `permission.sensitive` carries in the seed. The matrix
test asserts the seeded flags equal this list exactly, so flipping one in
`contracts/permissions.json` fails; `RoleMatrixFixture.SensitiveCodes` exposes it.

| Permission                | Citation (why the plans mark it) |
| ------------------------- | -------------------------------- |
| `artifact:read_sensitive` | 00 §2:50 + 00 §2:54              |
| `billing:read`            | 00 §2:54 + 00 §2:58              |
| `billing:write`           | 00 §2:54 + 00 §2:60              |
| `ingest_token:write`      | 00 §9.3:356                      |
| `support:approve`         | 00 §9.3:360 + 02.8.1 §2:23       |
| `support:grant`           | 00 §9.3:360 + 02.8.1 §2:22       |

The rule, applied to each row:

- **00 §2:54** bounds `tenant_admin` by what it must not hold — "Secret values are never
  readable" — and 02.2.1 §3:57 spells the bound out as "no `artifact:read_sensitive` and
  no `billing:*`". That is the plan text that makes the `artifact:read_sensitive` and
  `billing:*` rows sensitive; 00 §2:50 ("No secrets; sensitive artifacts need
  `artifact:read_sensitive`") and 00 §2:58 ("every read audited (12)") name the two
  artifact/billing read codes directly, and 00 §2:60 puts `billing:write` on the operator
  ingress with two-person control for adjustments and statements.
- **00 §9.3:356** says `ingest_token:write` "Plaintext returned once" — the
  returns-a-secret-once case.
- **00 §9.3:360** plus 02.8.1 §2:22/§2:23 make `support:grant`/`support:approve` the pair
  that opens a tenant to staff: the operator files a scoped, expiring grant and a
  `tenant_admin` of the target org approves it (approver ≠ requester), so both gate
  tenant data the plans call secret-adjacent (00 §2:59 "No tenant data by default").

Considered and **not** included, because the plan line describes a one-time secret in the
_response_ rather than a permission that gates secret-adjacent data — and the catalogue
agrees (neither is flagged): `ci_binding:write` (02.4.1 §42, the one-time binding
challenge), `webhook:write` (07.6.3 §60/§62, "secret once"), `service_identity:write`
(02.5.1 §8:71, the enrolment token shown once). `ingest_token:read` is excluded too: 00
§9.3:356 says the list "shows prefix and status only".

## Comparison with `contracts/permissions.json`

Every cell of the plan-derived matrix equals the catalogue's seed, and the plan-derived
sensitive set equals the catalogue's `sensitive` flags (both discrepancy tables are empty).
The catalogue was authored from the same plan text, so this is the expected outcome; the
test exists to catch a future divergence in either direction.

## Service ceiling

A service principal's claims are checked against the union of the `kind: service` roles'
grants (02.6.2), not against the role it was issued for. So a `ci_producer` claim of
`job:claim` or `job:execute` (agent codes) is inside the ceiling and is not a 403 by
itself; `AgentCodes_AreInsideTheServiceCeiling_SoRunCreateStillHolds` pins that, and the
set actually issued to `ci_producer` is pinned by the minted-token test in
`CiProducerScopeTests`.

## Rule: the role model is edited in two places together

`contracts/permissions.json` is the single source of the seeded rows (02 D2), and this
fixture is the plan-derived expectation the matrix test compares them against. **A change
to the role model — a new role, a new permission, a changed grant or a changed `sensitive`
flag — edits both files in the same change set**, plus the plan text that is the fixture's
source:

- add/remove the grant in `contracts/permissions.json` (and regenerate the C# constants
  via `scripts/generate-permissions.py`, or `scripts/check-permissions-drift.sh` fails);
- add/remove the same cell here with its plan citation;
- set the cell's `basis` from its citation (see §Grant provenance) and keep the inferred
  table above in step with it;
- flip a `sensitive` flag in both files and keep the §Sensitive set table in step;
- update the plan section the citation points at.

Editing only one of the two is exactly the drift the matrix test fails on: a catalogue
grant missing from the fixture, or a fixture grant missing from the catalogue, is a
mismatching cell reported by name; a `sensitive` flag that disagrees is reported by code.

## Review checklist

Synthetic data only (00 §5.7 S3): the fixture carries role and permission codes, no
customer rows. Provenance is `meta.source = handcrafted`; the plan citations are the
provenance of every cell.
