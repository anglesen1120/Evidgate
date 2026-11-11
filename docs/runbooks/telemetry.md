# Telemetry: resource attributes, sampling and redaction

Authority: [10.3.1-T1.3 and T1.4](../../plans/features/10-operations/10.3.1-telemetry-and-alerting.md)
(§10), the S3 redaction rule of [`../../plans/00-overview.md`](../../plans/00-overview.md) §5.7, and
the operational-SLI rows of §11. The collector itself — receivers, processors, retention and
exposure — is 10.3.1-T2 and is described in §5; §1–§4 are what an operator changes on the
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
contract stays 00 §5.5 C8 — code never puts a secret in a message. The collector is the third layer
where it runs (§5: it deletes credential-named attribute keys outright and masks secret-shaped
attribute values and log bodies), and the dev profile is the only place it runs today, so a shape
that leaks has to be fixed here, not there. If you find a shape that leaks, add it to `SecretRedactor`
with a negative test in `tests/Hosts.IntegrationTests` rather than to a collector rule, so it holds
for every deployment.

## 4. Keys, in one place

| Key | Matrix row | Consumer |
|---|---|---|
| `EVIDGATE__TELEMETRY__OTLPENDPOINT` | `infra/config-matrix.json` | both hosts; unset disables export |
| `EVIDGATE__TELEMETRY__SAMPLERATIO` | `infra/config-matrix.json` | both hosts; trace head sampling |
| `EVIDGATE__TELEMETRY__LIBRARYMETRICS` | `infra/config-matrix.json` | both hosts; runtime/Npgsql/ASP.NET Core meters, default on |
| `EVIDGATE__TELEMETRY__SERVICEVERSION` | `infra/config-matrix.json` | both hosts; `service.version` |

`node scripts/check-env-matrix.mjs` keeps that file equal to what the code consumes;
`bash scripts/check-no-secret-literals.sh` keeps a literal secret out of the repository.

## 5. Local backend: the `observability` compose profile (ADR-0012)

`scripts/dev/up.sh --observability` starts four more containers next to PostgreSQL and
SeaweedFS and points the Api and Worker at the collector
(`EVIDGATE__TELEMETRY__OTLPENDPOINT=http://127.0.0.1:4317`, unless `.env.dev` already
sets it). Without the flag export stays disabled unless an endpoint is configured: an
endpoint `.env.dev` sets still applies without `--observability`.
`scripts/dev/down.sh` stops them; `up.sh --reset` also deletes their volumes.

| Service | Signal | Loopback port (override) | Retention | Ceiling | Memory |
|---|---|---|---|---|---|
| `otel-collector` | OTLP in, `memory_limiter` + `transform/redaction` + `batch`, three `otlphttp` exporters out | 4317 gRPC, 4318 HTTP (`EVIDGATE_OBS_OTLP_GRPC_PORT`, `EVIDGATE_OBS_OTLP_HTTP_PORT`) | none (stateless) | `memory_limiter` soft 45 MiB, hard 60 MiB (RSS runs ~50 MiB above it) | 128m |
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

Every pipeline runs the same three processors in this order: `memory_limiter` →
`transform/redaction` → `batch`.

- **The receivers cap what they read**: one gRPC message is at most 1 MiB
  (`max_recv_msg_size_mib: 1`) and one HTTP request body at most 1 MiB
  (`max_request_body_size: 1048576`). A payload is decoded before any processor can
  refuse it, so this cap, not the limiter, bounds a single request.
- **`memory_limiter` first**, at `limit_mib: 60` with `spike_limit_mib: 15`, so the soft
  limit is 60 − 15 = **45 MiB** and 60 MiB is the hard one. At 45 MiB the collector
  refuses data (its `otelcol_processor_refused_*` counters) instead of buffering without
  bound; past 60 MiB it also forces a GC. The limiter reads the Go heap of the whole process, so total
  RSS still runs about 50 MiB above it, and `GOMEMLIMIT=96MiB` makes the GC work harder as
  the heap nears the container's 128 MiB `mem_limit`. This mitigates an OOM kill; it does not
  guarantee against one.
- **`transform/redaction` second** (10.3.1-T2.2): the third redaction layer, with
  `error_mode: propagate`, so a statement that errors drops that payload instead of
  shipping it unredacted. It **deletes** every attribute whose key names a credential —
  `authorization`, `cookie`/`set-cookie`, `token`, `secret`, `password`/`passwd`/`pwd`,
  `api_key`/`apikey`, `credential`, `pepper`, `__Host-`/`__Secure-` cookie names,
  `private`/`access`/`account`/`signing`/`encryption`/`sharedaccess` keys, `signature`,
  `session`, a `sid` segment, `jwt`, `bearer`, `connection_string`, `dsn` — on resource,
  scope, span, span-event, datapoint and log record attributes, because a masked key would
  still say that a value existed. It **masks string values to `***`** in every one of
  those contexts and in the log body, for the shapes the app cannot see once a record has
  left the process:
  - a `key=value` / `key: value` assignment — quoted, `%3D`/`%3A` percent-encoded, or
    unquoted to the end of the line, the next `;` or the next `&` — including the
    `EVIDGATE__X__SECRET_VALUE=…` identifier spelling;
  - a JSON pair, plain or backslash-escaped (`"password":"…"`, `\"password\":\"…\"`);
  - an XML element named for a secret (`<password>…</password>`);
  - an `authorization`/`cookie` header pair, to the end of its line;
  - a URL query parameter with a secret-named key (`?access_token=…&sig=…`) and a URL
    userinfo password (`scheme://user:…@host`);
  - a bare `Bearer`/`Basic` token.
  It is the stock `transform` (OTTL) processor, not the alpha `redaction` one: that one
  masks a matched key with `****` and cannot delete it, so the key name would still ship.
- **`batch` last**, at 512 items to wake a batch, at most 1024 items per request and 5 s
  for a quiet signal, so a masked record is what the batch and the queues ever hold.

Each `otlphttp` exporter retries with backoff for at most 5 minutes and then drops the
batch, and keeps a queue bounded in items (5000, four consumers) with
`block_on_overflow: false`: a full queue drops the newest item, counted in
`otelcol_exporter_enqueue_failed_*`, while the 5000 items already queued stay. The queues
are bounded in items rather than in bytes, so the memory limiter, not the queue, is what
bounds memory.

Accepted limits — deliberate, recorded so a review does not re-open them:

- over-deletion: a key that merely contains a secret word (`token_count`) is deleted, and
  an unquoted assignment masks to the end of its line, so a following `k=v` there is
  masked too;
- span links and exemplar attributes are not scrubbed, and a structured log body loses
  only its top-level secret-named keys: a nested map keeps its keys;
- the stores accept OTLP writes directly on their loopback ports with no credential, so a
  local process can bypass the collector; the collector is a redaction layer, not an
  enforcement point;
- a bare `Bearer`/`Basic` token shorter than eight characters under an otherwise harmless
  key is not masked, there is no allowlist of metric label keys, and no static test pins the
  loopback prefix of the compose ports (the smoke reads the bound addresses back at run time);
- the store UIs answer cross-origin reads, which this profile accepts because every port
  is loopback-only and no store has a credential to commit.

This profile, its collector and these processors are the dev stack (ADR-0012). Staging and
production still supply their own endpoint through `EVIDGATE__TELEMETRY__OTLPENDPOINT`, and
nothing here is a deployment. The receivers also set no CORS block, so no
`Access-Control-Allow-*` header is emitted and a browser origin cannot post to them; the public
web traces intake 10.3.1-T2.2 describes is a separate deployment, not this profile.

Prove the profile on a throwaway compose project (no `.env.dev`, free ports, torn down at
the end): `scripts/dev/observability-smoke.sh`. It checks that the default stack is still
`postgres seaweedfs`, that every published port is loopback, that each store reports the
retention above, and that one span, one metric point and one log record sent to the
collector are queryable. It also builds and runs the real Api and asserts its span, log,
metric and resource version arrive, and that planted secret canaries do not reach the stores.

Not covered yet (open items, see the 10.3.2 hand-over): the memory limits above are
sized, not measured — neither the limiter ceiling nor RSS with full queues has been
measured under load; the collector's own drop counters (`otelcol_*`) are not scraped; the
smoke reads the retention settings back and proves ingest and query, but does not wait for
expiry or for a store to hit its size ceiling; the stores share the default compose
network with PostgreSQL and SeaweedFS.

## 6. The metrics catalogue

`infra/metrics/catalogue.yaml` is the register of every metric an operator or the cost view
reads: per entry its id, the exact OTel instrument name, the meter that publishes it, the
instrument kind and unit, its labels, and **either** `source` (the code or library
subscription that emits it) **or** `unavailable` (a sentence saying why it cannot be reported
in this build). A metric with no source is absent, never `0`: an absent series is what a
dashboard shows as "no data", and a fabricated zero is indistinguishable from a measured one.

Both hosts subscribe to the catalogue's meters through `AddEvidgateTelemetry`
(`src/BuildingBlocks/Observability/ObservabilityExtensions.cs`): the application meter
`Evidgate`, the runtime's built-in `System.Runtime` (via `AddRuntimeInstrumentation`; on .NET 9+ the
instrument names are the runtime's own, so queries read `dotnet.gc.last_collection.heap.size`
for gen-2 and LOH, `dotnet.thread_pool.queue.length` and `dotnet.monitor.lock_contentions` for
starvation, and `dotnet.exceptions` for the exception counter), and
Npgsql's `Npgsql` meter (pool occupancy by state, pool timeouts, operation durations). The Api
adds the hosting meter's `http.server.request.duration` histogram, which is the ingest SLI
filtered by route template.

Labels are bounded token vocabularies. No label carries an organization, tenant, run, job,
request, trace, artifact or project id, a billing key, a token or a secret — a label is a
series, and a series is a bill. Three of them are worth knowing about when writing a query:
`evidgate_artifact_failure_total` carries `stage` and the failure `reason` (a bounded code word;
the column is `reason`, not `code`), and its `stage="parse"` series counts only the rejections
`ParseArtifactTask` writes — a report the raw-report parser refuses is a `raw_report` row with
`malformed`/`unsupported`, not an artifact verdict, and is absent from this counter; the usage
counter carries the ledger's closed `metric` vocabulary; and Npgsql's `db.client.*` series carry
`db.client.connection.pool.name`, which is a fixed pool name (`evidgate_app`,
`evidgate_worker_sched`) — never a connection string.

`evidgate_usage_billing_key_conflicts_total` carries no labels and counts billing keys a ledger batch refused
because the key already claims another payload (or the request repeated it with a different payload). The batch is
rolled back, nothing is billed, and the Error log names the key, the org, the project and the two digests. There is
no alert rule for this counter yet: watch it by hand. An increase means a batch was refused, so its usage is not
billed and the task that recorded it completed instead of retrying; the only recovery is to re-enqueue the batch
once the producer stops reusing the key for new work.

Apart from that refusal counter, counters describe committed work. Every counter other than the billing-key conflict
counter is recorded after the transaction that causes it commits, so a rolled-back attempt contributes nothing, and
the `incomplete` counter counts only a reconcile that settles the run: an append of late or reprocessed evidence
writes a revision and adds no sample. Read a counter's rate against the same window as the denominator it divides
into.

The gauges that need a database — `task_queue_oldest_age_seconds` and
`task_dead_letter_depth` — are refreshed by a single Worker loop every 10 s with one bounded
aggregate query; a scrape runs no SQL, a failed refresh keeps the last value, and a value not
renewed within two intervals is not reported at all. `task_dead_letter_depth` saturates at a
fixed cap, so a reading at the cap means "at least this deep" and the operator drains from the
`dead_letter` table itself. Alerts that read these series are 10.3.1-T5's rules; the thresholds
are not in this file.

The catalogue is pinned by `tests/Hosts.IntegrationTests/MetricCatalogueTests.cs`: the
committed YAML is parsed, every entry must name exactly one of a source or a reason, every
declared source instrument must actually be published by the real registration (a
`MeterListener` over the four meter names, fed by the application's own recording methods, a
real PostgreSQL connection and the runtime instrumentation), and a banned label fails the test.
Npgsql's `db.client.*` labels are bounded by the fixed data-source names the persistence wiring
sets — `evidgate_app` for the app role and `evidgate_worker_sched` for the sweep role — so a pool
name is a token, never the connection string the driver would otherwise report.
`scripts/dev/observability-smoke.sh` additionally asserts that a runtime metric
(`dotnet_thread_pool_thread_count_total` — a monotonic sum from an observable counter, so the
collector's translation appends `_total`; its `{thread}` annotation unit yields no suffix)
reaches Prometheus through the collector in the dev profile.
