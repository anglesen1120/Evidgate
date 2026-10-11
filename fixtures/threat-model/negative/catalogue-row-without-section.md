# Threat model v1

Version: v1
Status: draft
Last review: 2026-10-11 (M1)
Legend: ✅ verified · ⚠️ unverified · [INFERENCE] · [DECISION] · [OPEN]

## 1. Scope, method and reading guide

Fixture model: every module and boundary carries at least one dispositioned threat.

## 2. Catalogue

| ID                 | Kind     | Name                                  | Source                               |
| ------------------ | -------- | ------------------------------------- | ------------------------------------ |
| M-identity         | module   | Identity                              | FR-11-1                              |
| M-tenancy          | module   | Tenancy                               | FR-11-1                              |
| M-catalog          | module   | Catalog                               | FR-11-1                              |
| M-orchestrator     | module   | Orchestrator                          | FR-11-1                              |
| M-ingestion        | module   | Ingestion                             | FR-11-1                              |
| M-evidence         | module   | Evidence                              | FR-11-1                              |
| M-policy           | module   | Policy                                | FR-11-1                              |
| M-healing          | module   | Healing                               | FR-11-1                              |
| M-usage            | module   | Usage                                 | FR-11-1                              |
| M-audit            | module   | Audit                                 | FR-11-1                              |
| M-notifications    | module   | Notifications                         | FR-11-1                              |
| B-web              | boundary | Browser to API                        | plans/11-security-compliance.md §4.1 |
| B-cli-adapters     | boundary | CLI and adapters to API               | plans/11-security-compliance.md §4.1 |
| B-agent            | boundary | Agent to API                          | plans/11-security-compliance.md §4.1 |
| B-healing-sandbox  | boundary | Worker and sandbox to LLM             | plans/11-security-compliance.md §4.1 |
| B-llm-gateway      | boundary | LLM gateway                           | plans/11-security-compliance.md §4.1 |
| B-object-storage   | boundary | API to object storage                 | plans/11-security-compliance.md §4.1 |
| B-database         | boundary | API and worker to PostgreSQL          | plans/11-security-compliance.md §4.1 |
| B-ci               | boundary | Customer CI to API                    | plans/11-security-compliance.md §4.1 |
| B-artifact-parsers | boundary | Untrusted artifact content to parsers | plans/11-security-compliance.md §4.1 |
| B-webhook-out      | boundary | API to webhook receivers              | plans/11-security-compliance.md §4.1 |
| B-operator         | boundary | Operator to tenant data               | plans/11-security-compliance.md §4.1 |
| M-ghost            | module   | Ghost                                 | FR-11-1                              |

## 3. Module sections

### M-identity — Identity

| ID            | STRIDE | Threat              | Attacker          | Impact           | Disposition                                                 |
| ------------- | ------ | ------------------- | ----------------- | ---------------- | ----------------------------------------------------------- |
| TH-identity-1 | S      | Credential stuffing | External attacker | Account takeover | control: login rate limit and lockout — 02.3.1-T1 [planned] |

### M-tenancy — Tenancy

| ID           | STRIDE | Threat            | Attacker             | Impact          | Disposition                                                      |
| ------------ | ------ | ----------------- | -------------------- | --------------- | ---------------------------------------------------------------- |
| TH-tenancy-1 | I      | Cross-tenant read | Authenticated member | Data disclosure | control: row-level security and tenant GUC — 02.6.1-T1 [planned] |

### M-catalog — Catalog

| ID           | STRIDE | Threat            | Attacker             | Impact               | Disposition                                              |
| ------------ | ------ | ----------------- | -------------------- | -------------------- | -------------------------------------------------------- |
| TH-catalog-1 | T      | Catalog tampering | Authenticated member | Wrong policy applied | control: per-project authorization — 02.1.1-T1 [planned] |

### M-orchestrator — Orchestrator

| ID                | STRIDE | Threat             | Attacker          | Impact           | Disposition                                          |
| ----------------- | ------ | ------------------ | ----------------- | ---------------- | ---------------------------------------------------- |
| TH-orchestrator-1 | S      | Forged CI identity | External attacker | Unauthorized run | control: OIDC token validation — 02.4.1-T1 [planned] |

### M-ingestion — Ingestion

| ID             | STRIDE | Threat              | Attacker          | Impact          | Disposition                                             |
| -------------- | ------ | ------------------- | ----------------- | --------------- | ------------------------------------------------------- |
| TH-ingestion-1 | R      | Leaked ingest token | External attacker | Forged evidence | accepted: RR-1 — producer-slot residual accepted for v1 |

### M-evidence — Evidence

| ID            | STRIDE | Threat                | Attacker           | Impact          | Disposition                                                                        |
| ------------- | ------ | --------------------- | ------------------ | --------------- | ---------------------------------------------------------------------------------- |
| TH-evidence-1 | I      | Artifact content leak | Malicious artifact | Data disclosure | control: split viewer origins and sandboxed CSP (00 §5.7 S3) — 06.3.3-T1 [planned] |

### M-policy — Policy

| ID          | STRIDE | Threat           | Attacker             | Impact         | Disposition                                                     |
| ----------- | ------ | ---------------- | -------------------- | -------------- | --------------------------------------------------------------- |
| TH-policy-1 | T      | Policy tampering | Authenticated member | Wrong decision | control: approval bound to candidate hash — 08.2.3-T1 [planned] |

### M-healing — Healing

| ID           | STRIDE | Threat               | Attacker        | Impact          | Disposition                                             |
| ------------ | ------ | -------------------- | --------------- | --------------- | ------------------------------------------------------- |
| TH-healing-1 | E      | Privilege escalation | Malicious patch | Operator access | control: kill switch before apply — 02.8.1-T1 [planned] |

### M-usage — Usage

| ID         | STRIDE | Threat      | Attacker          | Impact          | Disposition                                              |
| ---------- | ------ | ----------- | ----------------- | --------------- | -------------------------------------------------------- |
| TH-usage-1 | D      | Usage flood | External attacker | Cost exhaustion | control: per-org quota enforcement — 11.1.1-T1 [planned] |

### M-audit — Audit

| ID         | STRIDE | Threat            | Attacker | Impact             | Disposition                                                                 |
| ---------- | ------ | ----------------- | -------- | ------------------ | --------------------------------------------------------------------------- |
| TH-audit-1 | R      | Audit repudiation | Insider  | Untraceable action | control: append-only audit log — 02.7.1-T1 [implemented: scripts/verify.sh] |

### M-notifications — Notifications

| ID                 | STRIDE | Threat            | Attacker          | Impact          | Disposition                                                   |
| ------------------ | ------ | ----------------- | ----------------- | --------------- | ------------------------------------------------------------- |
| TH-notifications-1 | I      | Notification leak | External attacker | Data disclosure | control: notification payload redaction — 02.7.1-T1 [planned] |

## 4. Boundary sections

### B-web — Browser to API

| ID       | STRIDE | Threat         | Attacker          | Impact        | Disposition                                                                 |
| -------- | ------ | -------------- | ----------------- | ------------- | --------------------------------------------------------------------------- |
| TH-web-1 | S      | Session hijack | External attacker | Impersonation | control: cookie flags and CSRF token (00 §5.7 S2, S7) — 02.3.1-T1 [planned] |

### B-cli-adapters — CLI and adapters to API

| ID                | STRIDE | Threat                | Attacker          | Impact              | Disposition                                          |
| ----------------- | ------ | --------------------- | ----------------- | ------------------- | ---------------------------------------------------- |
| TH-cli-adapters-1 | S      | Stolen CLI credential | External attacker | Unauthorized access | control: short-lived CLI token — 11.2.1-T4 [planned] |

### B-agent — Agent to API

| ID         | STRIDE | Threat                | Attacker          | Impact           | Disposition                                          |
| ---------- | ------ | --------------------- | ----------------- | ---------------- | ---------------------------------------------------- |
| TH-agent-1 | S      | Forged agent identity | External attacker | Unauthorized run | control: agent OIDC validation — 02.4.1-T1 [planned] |

### B-healing-sandbox — Worker and sandbox to LLM

| ID                   | STRIDE | Threat         | Attacker        | Impact          | Disposition                                                        |
| -------------------- | ------ | -------------- | --------------- | --------------- | ------------------------------------------------------------------ |
| TH-healing-sandbox-1 | E      | Sandbox escape | Malicious patch | Host compromise | control: container-only isolation (see RR-3) — 11.2.1-T4 [planned] |

### B-llm-gateway — LLM gateway

| ID               | STRIDE | Threat           | Attacker     | Impact          | Disposition                                                   |
| ---------------- | ------ | ---------------- | ------------ | --------------- | ------------------------------------------------------------- |
| TH-llm-gateway-1 | I      | Prompt data leak | LLM provider | Data disclosure | control: egress allowlist and redaction — 11.2.1-T4 [planned] |

### B-object-storage — API to object storage

| ID                  | STRIDE | Threat             | Attacker          | Impact          | Disposition                                                    |
| ------------------- | ------ | ------------------ | ----------------- | --------------- | -------------------------------------------------------------- |
| TH-object-storage-1 | I      | Bucket enumeration | External attacker | Data disclosure | control: private buckets and signed URLs — 06.3.3-T1 [planned] |

### B-database — API and worker to PostgreSQL

| ID            | STRIDE | Threat              | Attacker | Impact          | Disposition                                                              |
| ------------- | ------ | ------------------- | -------- | --------------- | ------------------------------------------------------------------------ |
| TH-database-1 | T      | Direct DB tampering | Insider  | Data corruption | control: least-privilege DB roles (00 §5.7 S1, S8) — 02.6.1-T1 [planned] |

### B-ci — Customer CI to API

| ID      | STRIDE | Threat          | Attacker     | Impact           | Disposition                                                          |
| ------- | ------ | --------------- | ------------ | ---------------- | -------------------------------------------------------------------- |
| TH-ci-1 | S      | CI secret theft | Malicious PR | Credential theft | control: fork-safe workflow rules (00 §5.7 S6) — 11.2.1-T4 [planned] |

### B-artifact-parsers — Untrusted artifact content to parsers

| ID                    | STRIDE | Threat       | Attacker           | Impact            | Disposition                                                             |
| --------------------- | ------ | ------------ | ------------------ | ----------------- | ----------------------------------------------------------------------- |
| TH-artifact-parsers-1 | D      | Parser crash | Malicious artifact | Denial of service | control: parser hardening and limits (00 §5.7 S5) — 06.3.3-T1 [planned] |

### B-webhook-out — API to webhook receivers

| ID               | STRIDE | Threat           | Attacker          | Impact       | Disposition                                                        |
| ---------------- | ------ | ---------------- | ----------------- | ------------ | ------------------------------------------------------------------ |
| TH-webhook-out-1 | S      | Webhook spoofing | External attacker | Forged event | control: HMAC webhook signature (00 §5.7 S4) — 02.7.1-T1 [planned] |

### B-operator — Operator to tenant data

| ID            | STRIDE | Threat             | Attacker | Impact              | Disposition                                       |
| ------------- | ------ | ------------------ | -------- | ------------------- | ------------------------------------------------- |
| TH-operator-1 | E      | Operator overreach | Insider  | Cross-tenant access | control: operator grant RLS — 02.8.1-T1 [planned] |

## 5. Residual-risk register

| ID   | Title                                           | State         | Owner     | Reviewed   | Next review | Receiving task | Rationale                                    |
| ---- | ----------------------------------------------- | ------------- | --------- | ---------- | ----------- | -------------- | -------------------------------------------- |
| RR-1 | T9 producer-slot residual                       | accepted      | Tech Lead | 2026-10-11 | M2          | —              | Leaked ingest token residual accepted for v1 |
| RR-3 | Container-only isolation of the healing sandbox | open-decision | Tech Lead | 2026-10-11 | M3          | 11.2.1-T4      | [OPEN] owner decision pending                |

## 6. Milestone-exit ritual

| Milestone | Re-review slot     | Last review | Result                          |
| --------- | ------------------ | ----------- | ------------------------------- |
| M1        | 00 §4 M1 exit gate | 2026-10-11  | draft v1 written; checker green |
| M2        | 00 §4 M2 exit gate | pending     | pending                         |
| M3        | 00 §4 M3 exit gate | pending     | pending                         |

| Date       | Milestone | Reviewer  | Change                 |
| ---------- | --------- | --------- | ---------------------- |
| 2026-10-11 | M1        | Tech Lead | v1 written (T1.1–T1.4) |

## 7. Disposition summary

| State                 | Count |
| --------------------- | ----- |
| control (implemented) | 1     |
| control (planned)     | 20    |
| accepted              | 1     |
| open                  | 0     |
