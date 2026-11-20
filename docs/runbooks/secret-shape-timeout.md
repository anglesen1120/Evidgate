# Secret-shape timeout runbook

Status: **alert rule specified, not deployed.** No rule file exists: `infra/alerts/` is absent and the rule format and paging route are owned by 10.3.1-T5 (see [incident process](incident-process.md) §1 and [dead-letter](dead-letter.md) §2). Nothing here pages anyone today.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. What the counter means

`evidgate_secret_shape_timeout_total` (catalogue id `secret_shape_timeout`, [`infra/metrics/catalogue.yaml`](../../infra/metrics/catalogue.yaml), meter `Evidgate`) increases by one each time a secret-shape regex runs out of its 100 ms match budget (`src/BuildingBlocks/SharedKernel/Security/SecretShape.cs:102-121` ✅). Its only label is `pattern`, one of `jwt`, `url_credentials`, `hex_run`, `random_run`, `uuid` (`src/BuildingBlocks/SharedKernel/Security/SecretShapeMetrics.cs:51-58` ✅). The checked value is never a label, a log line or an exception message.

The patterns are linear, so a timeout means a stalled host (CPU starvation, GC pause, noisy neighbour) rather than a hostile input (`SecretShape.cs:94-98` ✅). It does not mean a secret was found.

## 2. What the caller sees

The check never throws: it returns true (secret-shaped), and callers refuse secret-shaped values (`SecretShape.cs:94-98` ✅; that every caller maps true to a refusal and never a 500 is `[INFERENCE]` ⚠️). A legitimate caller can therefore see a refusal of a value that is not a secret, only while the condition lasts; a retry after the host recovers should pass `[INFERENCE]` ⚠️.

## 3. Alert rule (specification) `[INFERENCE]` ⚠️

The rule is the row `AR-secret-shape-timeout` in [alert rules](../security/alert-rules.md) §6: default threshold, its basis (a healthy host records zero; no traffic baseline exists, so the windows are defaults to tune), severity `warning`, route `ticket`. It is a specification only; the rule file is owed by 10.3.1-T5.1.

## 4. First checks

1. Read the `pattern` split. One pattern only points at a value shape; all patterns at once point at the host.
2. Compare with host health in the same window: CPU, `dotnet.gc.last_collection.heap.size`, `dotnet.thread_pool.queue.length` ([telemetry](telemetry.md) §6).
3. Check whether callers report refusals of ordinary values from the same time window. Collect request times and route templates, never the refused value, and never ask the customer for it.
4. Do not paste a request body, URL with identifiers, token or header into a ticket.

## 5. When to escalate

- The counter rises in 3 consecutive windows, or on every pattern at once: treat it as host stall and follow the [incident process](incident-process.md).
- Customers report refused ordinary values and the host looks healthy: raise it to the secret-shape owner; the 100 ms budget is a code decision, not an operator setting.

## 6. Not covered

Deployment of an alert rule file, paging, and a dashboard panel are open under 10.3.1-T5. The usage billing-key conflict counter has the same gap (`infra/metrics/catalogue.yaml`, id `usage_billing_key_conflict`); other counters without a rule are not listed here.
