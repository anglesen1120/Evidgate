# Telemetry: resource attributes, sampling and redaction

Authority: [10.3.1-T1.3 and T1.4](../../plans/features/10-operations/10.3.1-telemetry-and-alerting.md)
(§10), the S3 redaction rule of [`../../plans/00-overview.md`](../../plans/00-overview.md) §5.7, and
the operational-SLI rows of §11. The collector itself — receivers, redaction processor, retention,
exposure — is 10.3.1-T2 and is **not** described here. This page is what an operator changes on the
application side, and what the application guarantees before the collector sees a byte.

Both hosts boot the pipeline through one call, `AddEvidgateTelemetry` in
[`../../src/BuildingBlocks/Observability/ObservabilityExtensions.cs`](../../src/BuildingBlocks/Observability/ObservabilityExtensions.cs):
`Evidgate.Api` passes `evidgate-api`, `Evidgate.Worker` passes `evidgate-worker`. There is one
registration; a second `AddOpenTelemetry` in a host would be a defect, not a configuration option.

## 1. Resource attributes

Every trace, metric and log carries the same three attributes, so one backend filter finds all
signals of one deployment:

| Attribute | Value | Where it comes from |
|---|---|---|
| `service.name` | `evidgate-api` / `evidgate-worker` | The host's `AddEvidgateTelemetry(<name>)` call |
| `deployment.environment` | `Development`, `Staging`, `Production` | The host environment name (`ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`); no key of its own |
| `service.version` | image tag, digest or build version | `EVIDGATE__TELEMETRY__SERVICEVERSION`; unset ⇒ the entry assembly's informational version |

Notes that matter during an incident:

- **`service.version` is how a rollback is proven.** A deploy that sets the key to the image tag or
  digest makes "which build produced this error" a filter, not an investigation. Unset, the value is
  the `AssemblyInformationalVersion`, which in this repository is the SDK default (`1.0.0`): no build
  sets `Version`/`SourceRevisionId`, so the fallback does **not** tell two commits apart. Set the key
  in staging and production to the image tag or digest; that is what makes "which build produced this
  error" a filter.
- A blank `EVIDGATE__TELEMETRY__SERVICEVERSION` fails the boot and names the key (`Configuration
  invalid: …`), the same clean path as an invalid OTLP endpoint (D4). Remove the key to fall back.
- The browser (`evidgate-web`) sends only `service.name` and the SDK identity keys — today
  **no `service.version`**, because `startTelemetrySdk` is called with `{ locale }` only (the SDK
  supports a version, the app passes none; cited in `docs/runbooks/web-vitals-intake.md`). Its
  exporter allowlist drops `deployment.environment` on purpose (no customer or environment naming
  from the browser). Attributing web spans to an environment or a build is the web intake pipeline's
  job (10.3.1-T2.1, `[OPEN]`), not a browser setting — do not write a collector rule that expects a
  page-side `service.version`.

## 2. Head sampling

| Key | Default | Valid values | Effect |
|---|---|---|---|
| `EVIDGATE__TELEMETRY__SAMPLERATIO` | `1` | decimal fraction in `[0, 1]` | Fraction of **root traces** kept by trace id; every child span follows its parent |

- **Default 1, deliberately.** Silence is never the default: a deployment that wants less volume says
  so. This matches the browser runtime, where an unset `evidgate-telemetry-sample-ratio` meta tag also
  means 1.
- **Decision shape:** `ParentBased(TraceIdRatioBased(ratio))` — a root trace is kept with probability
  `ratio` *by trace id*, so every service and every retry of the same trace reaches the same decision,
  and a child never contradicts its parent. A trace sampled by a caller (`traceparent` flag `01`) is
  kept regardless of this host's ratio.
- **Validation:** a value outside `[0, 1]`, `NaN` or `Infinity` fails the boot naming the key. A typo
  can neither sample everything nor turn tracing off silently.
- **Metrics are never head-sampled.** They are aggregates; the ratio applies to traces only.
- **The ratio does not reduce local CPU.** A dropped trace is recorded in-process all the same (spans,
  tags, events, the Npgsql and HttpClient child spans) so an error in it can still be exported; only
  the export of its non-error spans is skipped. Lowering the ratio buys backend volume and network,
  not cheaper requests or jobs — 10.3.1-T2's collector-side tail sampling is what would relax that.
- **Per environment:** dev omits the key (ratio 1). staging and prod set their own value in the
  deployment environment (the matrix row records the key, the default and the type; it carries no
  per-environment value) and the prod value is tuned with the load test (10.6.1), not guessed here.
- **An inbound `traceparent` is honoured — including its sampled flag.** AC1 needs the W3C link from a
  client (an adapter, the web app) into our trace, so the ASP.NET Core instrumentation adopts the
  caller's trace id and parent span. Two consequences: a caller that sends `00-<trace>-<span>-01`
  forces that trace to be exported whatever this host's ratio says (`ParentBased` keeps a
  sampled parent's children), and a caller can place its request inside a trace id of its choosing.
  That is an accepted trade-off `[DECISION]`, recorded for owner review rather than fixed here: it
  is the price of propagating a real end-to-end trace, the span is still ours and still tagged with
  our `request_id`, and refusing the remote parent for unauthenticated routes (or keeping it only as
  a link, and capping `tracestate`/carrier size) is a separate decision with its own test plan.

### Errors are always kept, and what that costs

A span whose status is `Error` is exported even when the ratio dropped its trace. The mechanism is the
OTel .NET tail-sampling recipe: a dropped trace is *recorded but not sampled* (`RecordOnly`), and at
span end `ErrorRetainingProcessor` sets the W3C recorded flag on an error span, which is the only flag
the SDK's export processors look at before handing a span to the exporter. The processor is registered
before the exporter in `AddEvidgateTelemetry`.

Two consequences to know before lowering a ratio:

1. **Local work is not saved.** The instrumentation for a dropped trace still runs (tags, events,
   status). The ratio saves backend volume and network, not CPU. That is the price of keeping errors;
   a future collector tail-sampler (10.3.1-T2) can relax it.
2. **A dropped trace's error arrives alone.** Retention is per span, so the error span of a dropped
   trace is exported while its non-error parent and sibling spans are already gone. Reconstructing a
   whole error trace needs the collector's tail sampler; this policy is the app-side floor that works
   with no collector configuration.

### Drills

- **Ratio takes effect:** set `EVIDGATE__TELEMETRY__SAMPLERATIO=0` on one host, restart, send a
  request that succeeds: no span of it appears in the backend. Then send one that fails: its
  error-status span does appear (alone). Set the ratio back to `1`.
- **Validation is clean:** set `EVIDGATE__TELEMETRY__SAMPLERATIO=1.5`. The host exits non-zero with
  `Configuration invalid:` naming `SAMPLERATIO`, and no stack trace (the same expectation as
  `Invalid_OtlpEndpoint_Fails_Startup_Cleanly`).
- **Attributes are present:** in the backend, filter any span, metric point or log line by
  `service.name` and read `deployment.environment` and `service.version` beside it.

## 3. Redaction (S3): what the app removes before export

Two layers, both in the app, because the collector is the third (10.3.1-T2.2):

| Layer | Where | What it covers |
|---|---|---|
| Logger decorator | `RedactingLoggerFactory` / `RedactingLogger` in `src/BuildingBlocks/Observability/Redaction/` | The state pairs of every log entry, scope state, and the exception object, for **every** provider (console, OpenTelemetry, tests) |
| Export processor | `LogRedactionProcessor`, added by `AddEvidgateTelemetry` | Each `LogRecord` just before the export processor: attributes (including any a processor added), body, formatted message, and the exception attributes |

**The order is fixed, and it is part of the contract.** `AddEvidgateTelemetry` registers the log
pipeline's processors in one place and in this order: `CorrelationLogRecordProcessor` first (it only
ever *adds* `evidgate.request_id`/`evidgate.run_id`/`evidgate.job_id`/`evidgate.trace_id`, read from
the current span's tags and baggage and validated — a value that is not a request id or a GUID is
dropped, so a forged `baggage: evidgate.run_id=…` from a client signs nothing), then
`LogRedactionProcessor` (which therefore also covers a value an earlier processor added), then the
export processor. An exporter buffers the record at its own `OnEnd`, so a processor registered after
it is invisible to the backend: that is why `AddEvidgateCorrelation` (instrumentation only) never
touches the logging options, and why a review of this pipeline must check order, not just presence.

The list (`SecretRedactor`):

- **A key that names a whole secret is erased**, value replaced by `***`: `authorization`, `token`,
  `secret`, `password`/`passwd`/`pwd`, `api_key`/`apikey`, `credential`, `cookie`/`set-cookie`,
  `privatekey`, `accesskey`, `accountkey`, `sharedaccesskey`, `pepper`, and any `__Host-`/`__Secure-`
  cookie name.
- **A connection string keeps its non-secret parts** and is masked inline: `Host=h;Database=d` stays,
  `Password=…` becomes `Password=***` (quoted forms included — a password containing `;` is quoted by
  a driver and would otherwise leak its tail).
- **Free text** (formatted messages, template arguments, exception text) is masked for
  `secret=value` assignments (including `EVIDGATE__X__TOKEN=…`-style environment spellings), cookie
  and authorization header lines, and `__Host-`/`__Secure-` cookie values.
- **Nested state** is walked by key (dictionaries, pair sequences, sequences), bounded to 4 levels and
  32 entries, so a token inside a dictionary cannot travel through the dictionary's `ToString()`.
- **Exceptions** are masked when their text contains a maskable shape. The decorator replaces the
  object, and the export processor writes the semantic attributes `exception.type` (the **original**
  type name — the wrapper object cannot report it through `GetType`), `exception.message` and
  `exception.stacktrace`, then clears the raw object so the exporter cannot re-add it. A clean
  exception is untouched.
- **Processor-added attributes are covered too.** `LogRecord.Attributes` is settable (the list itself
  is read-only), so the export processor copies the pairs, runs every value through the list, and
  assigns the list back. This is the layer that catches a value a processor of its own added.
- **Scopes** are redacted in the decorator. `IncludeScopes` is off for the OpenTelemetry provider
  today (only structured pairs are exported), so this matters if that is ever enabled.

**Known limits, by construction.** This is a shape-aware scrubber, not a value oracle: a bare secret
with no name beside it (a token pasted as an entire exception message) is not detectable here. The
contract stays 00 §5.5 C8 — code never puts a secret in a message — and the collector's redaction
processor is the third layer for attribute *names*. If you find a shape that leaks, add it to
`SecretRedactor` with a negative test in `tests/Hosts.IntegrationTests` rather than to a collector
rule, so it holds for every deployment.

## 4. Keys, in one place

| Key | Matrix row | Consumer |
|---|---|---|
| `EVIDGATE__TELEMETRY__OTLPENDPOINT` | `infra/config-matrix.json` | both hosts; unset disables export |
| `EVIDGATE__TELEMETRY__SAMPLERATIO` | `infra/config-matrix.json` | both hosts; trace head sampling |
| `EVIDGATE__TELEMETRY__SERVICEVERSION` | `infra/config-matrix.json` | both hosts; `service.version` |

`node scripts/check-env-matrix.mjs` keeps that file equal to what the code consumes;
`bash scripts/check-no-secret-literals.sh` keeps a literal secret out of the repository.

## 5. Local backend: the `observability` compose profile (ADR-0012)

`scripts/dev/up.sh --observability` starts four more containers next to PostgreSQL and
SeaweedFS and points the Api and Worker at the collector
(`EVIDGATE__TELEMETRY__OTLPENDPOINT=http://127.0.0.1:4317`, unless `.env.dev` already
sets it). Without the flag the dev stack is unchanged and exports nowhere.
`scripts/dev/down.sh` stops them; `up.sh --reset` also deletes their volumes.

| Service | Signal | Loopback port (override) | Retention | Ceiling | Memory |
|---|---|---|---|---|---|
| `otel-collector` | OTLP in, three `otlphttp` exporters out | 4317 gRPC, 4318 HTTP (`EVIDGATE_OBS_OTLP_GRPC_PORT`, `EVIDGATE_OBS_OTLP_HTTP_PORT`) | none (stateless) | `memory_limiter` 60 MiB (RSS runs above the limiter value, so it sits well under the cgroup) | 128m |
| `victoriatraces` | traces | 10428 (`EVIDGATE_OBS_TRACES_PORT`) | 7 d | 2 GiB | 256m |
| `victorialogs` | logs | 9428 (`EVIDGATE_OBS_LOGS_PORT`) | 30 d | 3 GiB | 256m |
| `prometheus` | metrics | 9090 (`EVIDGATE_OBS_PROMETHEUS_PORT`) | 90 d | 2 GiB | 256m |

Query UIs: Prometheus `http://127.0.0.1:9090/`, VictoriaLogs and VictoriaTraces
`http://127.0.0.1:<port>/select/vmui/` (traces also answer the Jaeger Query API under
`/select/jaeger/`). The UIs have no login, which is why every port is bound to `127.0.0.1`
and none is ever published on a review or production host.

The ceilings are retention-by-size thresholds, not allocation limits: the Victoria stores
keep at least the newest per-day partitions and Prometheus can overshoot while compacting
(ADR-0012 decision 4), so keep disk headroom. The application only knows the collector;
changing a backend means editing the exporters in `infra/observability/otel-collector.yaml`.
Redaction and sampling processors arrive with the collector deployment task.

Prove the profile on a throwaway compose project (no `.env.dev`, free ports, torn down at
the end): `scripts/dev/observability-smoke.sh`. It checks that the default stack is still
`postgres seaweedfs`, that every published port is loopback, that each store reports the
retention above, and that one span, one metric point and one log record sent to the
collector are queryable.

Not covered yet (open items, see the 10.3.2 hand-over): the memory limits above are
sized, not measured under load; the collector's own drop counters (`otelcol_*`) are not
scraped; the smoke reads the retention settings back and proves ingest and query, but
does not wait for expiry or for a store to hit its size ceiling; the stores share the
default compose network with PostgreSQL and SeaweedFS.
