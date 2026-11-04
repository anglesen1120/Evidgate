# ADR-0012 — Observability backend with per-signal retention and cost limits

Legend: ✅ verified with link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]`.

## Status

proposed

## Date

2026-11-05

## Context

Review finding S11 requires the observability backend to be decided, with retention and cost limits per signal, before the OpenTelemetry Collector (10.3.1-T2) is deployed. The application side is fixed: `Evidgate.Api`, `Evidgate.Worker` and the migrator export OTLP to one endpoint, `EVIDGATE__TELEMETRY__OTLPENDPOINT` (`infra/config-matrix.json`), and every trace, metric and log carries the same resource attributes, including the tenant `org_id` (`docs/runbooks/telemetry.md`). DECISIONS §4 fixes OpenTelemetry and the licence rule: no AGPL/SSPL/BUSL/RPL or non-commercial component in shipped code or hosted services; ADR-0005 enforces the same list. Because the telemetry may carry tenant ids, the backend also decides where those ids are stored and for how long (11.9.1 deletion promises, checked by 10.3.2-T3).

Constraints: the backend must run on the dev Mac through `infra/compose.dev.yml` and on the Pilot-0 review host, an EC2 `t4g.medium` (arm64, 2 vCPU, 4 GB RAM, 30 GB gp3) whose compose limits already total ≈2.9 GB (`docs/runbooks/review-deploy.md` §3). The cloud provider is undecided (O3, DECISIONS §8), so the choice must have an exit to a managed OTLP backend that does not touch application code.

## Decision

1. `[DECISION]` **The collector is the only application-facing seam.** The application exports OTLP to the collector only; the collector exports to the backend. No application code, option or config key refers to a backend product. Swapping the backend, locally or after O3, changes collector exporter configuration only.
2. `[DECISION]` **Compose-local backend: three single-node, Apache-2.0 stores, one per signal.**
   - Metrics: **Prometheus** (Apache-2.0), OTLP receiver enabled with `--web.enable-otlp-receiver`, serving `/api/v1/otlp/v1/metrics`. Chosen over VictoriaMetrics because retention by both time and size is documented for Prometheus and the SLI recording rules of 10.4.1-T1 are native Prometheus rules.
   - Logs: **VictoriaLogs** (Apache-2.0), OTLP/HTTP ingestion at `/insert/opentelemetry/v1/logs`, built-in query UI.
   - Traces: **VictoriaTraces** (Apache-2.0), OTLP/HTTP at `/insert/opentelemetry/v1/traces` (optional OTLP/gRPC), Jaeger Query API for Jaeger UI or compatible clients.
   - The collector (`opentelemetry-collector-contrib`, Apache-2.0) uses its `otlphttp` exporter for all three; no vendor-specific exporter is needed.
3. `[DECISION]` **Excluded:** Grafana Tempo, Grafana Loki, Grafana and OpenObserve (AGPL-3.0), SigNoz (repository licence is not a single SPDX identifier: `ee/` is under a separate licence, so ADR-0005's `NOASSERTION`/missing-licence deny rule applies until the owner reviews it), ClickHouse-based stores as an operational burden on the 4 GB host. No dashboard product is chosen here: use the stores' built-in query UIs. A dashboard layer (for example Perses, Apache-2.0 ⚠️ not evaluated further) is a later decision; Grafana stays excluded unless the owner accepts AGPL for an internal-only, operator tool through an ADR amendment.
4. `[DECISION]` **Per-signal retention defaults and storage ceilings** (`[INFERENCE]` for every number, a starting point revised after the 10.6.1-T1 load numbers; they are defaults, not a customer promise, and never exceed the matching 11.9.1 deletion window, checked by 10.3.2-T3.1):

   | Signal | Retention | Dev Mac ceiling | Review EC2 ceiling | Enforced by |
   |---|---|---|---|---|
   | Traces | 7 days (also head-sampled by 10.3.1-T1.3) | 2 GiB | 1 GiB | VictoriaTraces `-retentionPeriod`, `-retention.maxDiskSpaceUsageBytes` |
   | Logs | 30 days | 3 GiB | 1.5 GiB | VictoriaLogs `-retentionPeriod`, `-retention.maxDiskSpaceUsageBytes` |
   | Metrics | 90 days (full resolution) | 2 GiB | 1 GiB | Prometheus retention time and size |

   The plan's earlier starting point of 13 months at reduced resolution for metrics is not adopted: no downsampling is documented for single-node Prometheus (⚠️), so long-horizon SLI history needs recording-rule aggregates or a managed backend after O3 (`[INFERENCE]`). When a ceiling is reached the store drops the oldest data first; the collector buffers within its memory limit and then drops, and a drop counter is exposed (10.3.2 §8 failure paths).
5. `[DECISION]` **Resource budget** (compose `mem_limit`, `[INFERENCE]`, not measured): Prometheus 256m, VictoriaLogs 256m, VictoriaTraces 256m, collector 128m, ≈0.9 GB in total. This does not fit next to the review stack's ≈2.9 GB limits on a 4 GB host, so the profile is **opt-in** (off by default) on the review EC2 and on by flag on the dev Mac. Measured values replace these in T1.2.
6. `[DECISION]` **Cost.** Compose-local cost is host disk and memory only. Disk ceiling for the review host is 3.5 GiB of its 30 GB volume (`[INFERENCE]`, about 12 %). ⚠️ gp3 price per GB-month is not cited here; the figure comes from the coordinator notes of the review-host work and the owner's $200 budget makes the order of magnitude (well under $1/month for 3.5 GiB) immaterial.
7. `[DECISION]` **Tenant data.** Telemetry holds ids, route templates, status and durations only (10.3.1-T2 redaction, AC9). Backend access is operator-only and is never exposed on public ingress. Purging a deleted tenant's telemetry is **by retention expiry**: the deletion report quotes the per-signal window (AC10, second alternative). ⚠️ No per-tenant delete is relied on; whether VictoriaLogs or VictoriaTraces offer one is not verified here.
8. `[DECISION]` **Exit to managed OTLP after O3.** Any provider that accepts OTLP/HTTP replaces the three `otlphttp` exporters in the collector configuration; the application endpoint stays the collector. The provider's retention and price tiers, region, and the staging probe location remain `[OPEN]` O3. Portability caveat: alert and recording rules written in PromQL port to providers that accept PromQL ⚠️; logs and traces query syntaxes do not, so no runbook may depend on VictoriaLogs or VictoriaTraces query language outside the operator documentation.

## Alternatives rejected

| Alternative | Reason rejected |
|---|---|
| Grafana Tempo + Loki (+ Grafana) | AGPL-3.0 ✅ ([Tempo](https://raw.githubusercontent.com/grafana/tempo/main/LICENSE), [Loki](https://github.com/grafana/loki), [Grafana](https://github.com/grafana/grafana) report AGPL-3.0 through the GitHub API); denied by DECISIONS §4 and ADR-0005 |
| OpenObserve | AGPL-3.0 ✅ ([repository](https://github.com/openobserve/openobserve)); denied |
| SigNoz | Repository licence is split: code under `ee/` has a separate licence ✅ ([LICENSE](https://raw.githubusercontent.com/SigNoz/signoz/main/LICENSE)); needs a ClickHouse cluster ⚠️ (not measured), too heavy for the review host; excluded until the owner reviews the `ee/` terms |
| VictoriaMetrics single-node for metrics | Apache-2.0 ✅ ([repository](https://github.com/VictoriaMetrics/VictoriaMetrics)), OTLP at `/opentelemetry/v1/metrics` ✅ ([guide](https://github.com/VictoriaMetrics/VictoriaMetrics/blob/master/docs/victoriametrics/data-ingestion/OpenTelemetry-Collector.md)); the time retention flag is documented ✅ ([docs](https://docs.victoriametrics.com/victoriametrics/single-server-victoriametrics/)) but a disk-size ceiling flag was not found in the documentation fetched ⚠️. A strong fallback if Prometheus size retention proves unreliable |
| Jaeger v2 for traces | Apache-2.0 ✅ ([repository](https://github.com/jaegertracing/jaeger)); viable, but a second query/storage design next to VictoriaLogs. VictoriaTraces gives one operational shape for logs and traces and serves the Jaeger Query API ✅ ([docs](https://docs.victoriametrics.com/victoriatraces/)). Kept as the fallback if VictoriaTraces (young, v0.x ✅ releases page) proves unstable |
| Managed OTLP now | Provider undecided (O3); cost before any customer data; kept as the exit |
| Collector `debug`/file exporters only | Nothing to query, so AC2 (a span, metric and log visible by query) cannot be met |

## Consequences

- Unblocks 10.3.1-T2 (collector deployment) once the compose profile exists; the application is unchanged (AC2).
- Three data directories and four containers in a new compose profile; the review host cannot run them beside the full stack (decision 5).
- No Grafana: operators use the built-in query UIs until a dashboard decision. Owner decision needed: accept this ADR, and later a dashboard layer.
- VictoriaTraces is a v0.x product ✅; the revisit trigger covers it.
- Hand-over: 10.3.2-T1.2 (compose profile, pinned image digests, volumes, retention flags, `infra/config-matrix.json` keys, ADR-0002 image provenance if the matrix requires it) is not part of this change because `infra/**` puts a diff in the `full` gate tier and the Docker gate lock is held by another lane. It is recorded in the 10.3.2 plan Notes for the next backend lane.
- No migration, no contract change, no application code.

## Evidence

Prior art (official documentation read, 2026-11-05):

- ✅ [OpenTelemetry Collector contrib](https://github.com/open-telemetry/opentelemetry-collector-contrib): Apache-2.0; latest release v0.164.1 on 2026-10-26. Took the `otlphttp` exporter as the only backend seam.
- ✅ [Prometheus](https://github.com/prometheus/prometheus): Apache-2.0, v3.15.1 (2026-10-21). [OTLP guide](https://prometheus.io/docs/guides/opentelemetry/): `--web.enable-otlp-receiver` serves `/api/v1/otlp/v1/metrics`. [Storage docs](https://github.com/prometheus/prometheus/blob/main/docs/storage.md): retention by time (default 15d) and by size; the command-line retention flags are marked DEPRECATED in the current command-line reference ✅ ([reference](https://github.com/prometheus/prometheus/blob/main/docs/command-line/prometheus.md)), so T1.2 sets them through the configuration the pinned version documents.
- ✅ [VictoriaLogs](https://github.com/VictoriaMetrics/VictoriaLogs): Apache-2.0, v1.53.0 (2026-10-01). [Docs](https://docs.victoriametrics.com/victorialogs/): `-retentionPeriod` (1d–100y), `-retention.maxDiskSpaceUsageBytes`, `-retention.maxDiskUsagePercent`, per-day partitions dropped oldest first; [OTLP ingestion](https://docs.victoriametrics.com/victorialogs/data-ingestion/opentelemetry/) at `/insert/opentelemetry/v1/logs`.
- ✅ [VictoriaTraces](https://github.com/VictoriaMetrics/VictoriaTraces): Apache-2.0, v0.12.1 (2026-10-23). [Docs](https://docs.victoriametrics.com/victoriatraces/): default retention 7 days, same two retention flags; [OTLP](https://docs.victoriametrics.com/victoriatraces/data-ingestion/opentelemetry/) HTTP at port 10428 `/insert/opentelemetry/v1/traces`, gRPC when `-otlpGRPCListenAddr` is set.
- ✅ Licences read through the GitHub API on 2026-11-05: Tempo, Loki, Grafana, OpenObserve AGPL-3.0; SigNoz NOASSERTION; ClickHouse, Jaeger, Perses Apache-2.0.
- ⚠️ Not measured: memory and disk use of any candidate on the dev Mac or the t4g.medium; the ceilings and `mem_limit` values above are `[INFERENCE]` until T1.2 measures them.
- ⚠️ Not verified: arm64 images for each pinned version (required for the t4g.medium); T1.2 confirms the manifest lists.

Open questions for the owner: accept the ADR; the dashboard layer; whether SigNoz's `ee/` terms are acceptable (not needed for this decision).

## Revisit trigger

Reopen if a chosen component changes licence or drops arm64 images, VictoriaTraces is abandoned or fails the AC2/AC3 checks in T1.2, a measured footprint breaks the budget in decision 5, the 10.6.1-T1 load numbers show a retention or ceiling is wrong, the O3 provider offers managed OTLP at an acceptable cost, or the owner decides to accept AGPL for an operator-only dashboard.
