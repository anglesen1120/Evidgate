# Web-vitals intake: the `web_vital` span schema

Authority: ruling **T39** in [`../../plans/features/README.md`](../../plans/features/README.md) and story
[10.3.1-T1.4](../../plans/features/10-operations/10.3.1-telemetry-and-alerting.md). Emission is owned
by **09.1.1-T9.1** (`apps/web/src/app/core/telemetry/`); this page records the shape the collector and
the recording rules (10.4.1) may rely on. Nothing here asks the app to change: the schema is what the
shipped emitter sends, read from the code, not a wish list.

## 1. Transport

| Property | Value |
|---|---|
| Signal | **Traces only** (no metrics, no logs); each vital is one span |
| Protocol | OTLP/HTTP, `@opentelemetry/exporter-trace-otlp-http` (`telemetry-sdk.ts:29`) |
| Endpoint | The `evidgate-otlp-endpoint` `<meta>` tag of `index.html`; only an absolute `https:` URL with no userinfo, query or fragment is accepted, and anything else means "export nothing" (no network call) |
| Credential | **None.** The intake is public and unauthenticated; the app sends no token, cookie or tenant identity |
| Preflight | The intake must answer the CORS preflight for the app origin with `Access-Control-Allow-Headers: content-type` (it is a browser POST, so the collector needs an `https:` listener and an origin allowlist — 10.3.1-T2.2) |
| Batch | `BatchSpanProcessor` in the browser, flushed on route change, `visibilitychange → hidden`, `pagehide` and shutdown |

The intake is deliberately separate from the service OTLP receivers: those stay internal-only, this one
is the only public listener and must be traces-only with an attribute allowlist on the collector side
(10.3.1-T2.2). The collector host and its exact URL remain `[OPEN]` until T2.1 settles them.

## 2. Resource attributes

| Key | Value | Note |
|---|---|---|
| `service.name` | `evidgate-web` | constant |
| `service.version` | the app version, **only when the app passes one** to `startTelemetrySdk(options.serviceVersion)`; `telemetry.service.ts` currently passes `{ locale }` alone, so web spans carry none today | optional |
| `telemetry.sdk.language` / `telemetry.sdk.name` / `telemetry.sdk.version` | as the SDK sets them | kept |

Everything else on the resource is **dropped** by the app's allowlist
(`attribute-allowlist.ts`, `ALLOWED_RESOURCE_KEYS`). In particular there is **no
`deployment.environment`** from the browser: the environment of a web span is attached by the
collector's web pipeline (a resource processor or a per-environment receiver), never by the page.
`[OPEN]` for 10.3.1-T2.1.

## 3. The span

| Field | Value |
|---|---|
| Name | `web_vital` |
| Kind | `INTERNAL` |
| Parent | The `route.change` span of the current navigation when it is still open, otherwise the span is a root (a vital that settles after the route span closed, e.g. a late LCP) |
| Status | Not set by the vital itself (`Unset`); a vital is data, not an error |

### Attributes (the complete set)

| Attribute | Type | Allowed values | Meaning / unit |
|---|---|---|---|
| `web_vital.name` | string | `LCP`, `INP`, `CLS` | Which core vital this span reports |
| `web_vital.value` | number | finite | **LCP and INP in milliseconds**; **CLS unitless** (layout-shift score, the `web-vitals` value) — the unit follows the metric, it is not a label |
| `web_vital.rating` | string | `good`, `needs-improvement`, `poor` | The `web-vitals` rating for that value, as the library computes it |
| `web_vital.navigation_type` | string | as `web-vitals` reports it: `navigate`, `reload`, `back-forward`, `back-forward-cache`, `prerender`, `restore`; the app scrubs it to `^[a-z][a-z-]{0,31}$` | How the page was entered |
| `evidgate.route` | string | a **route template** (`/projects/:projectId/runs`), never a URL | Which route the vital belongs to |
| `evidgate.locale` | string | `en`, `vi` | Present only when the page language is one of the two |

No message text, no URL, no user or session id, no stack trace — the browser exporter's allowlist
drops any other key, so a future attribute needs a rule in `scrub.ts` before it can leave the page.

### Cardinality and repetition rules

- **LCP is emitted at most once per page.** `web-vitals` finalises it on the first input or when the
  page is hidden; an earlier candidate is never emitted, so a recorder must not expect a stream of LCP
  samples per page.
- **INP and CLS are page-cumulative.** They are registered with `reportAllChanges`, and the flush
  emits the current running value when it changed since the previous flush; a page that shifts its
  layout reports CLS more than once, each span carrying the value *at that moment*. Take the last span
  per `(trace, web_vital.name)` for a page-level figure, or the maximum — never the sum.
- **A flush emits only dirty vitals**, so a route change flushes the three vitals of the route just
  left, attributed to that route (the flush runs before the new `route.change` starts).

## 4. Sampling

The browser traces use `ParentBased(TraceIdRatioBased(ratio))` with the ratio from the
`evidgate-telemetry-sample-ratio` `<meta>` tag; an absent or unparsable value means **1** (sample
everything), never a silent clamp. A web vital inherited a sampled parent may therefore still be
missing from the backend — the ratio is a volume control, and web vitals are not exempt from it.
Server-side error retention (10.3.1-T1.3) does not apply here: a vital has no error status, so
recording a low ratio means accepting fewer vital samples, not losing failures.

## 5. What the collector must do (contract for 10.3.1-T2.1/T2.2)

1. Accept OTLP/HTTP from the app origin, traces only, with body-size and rate limits, on the one
   public `https:` traces endpoint.
2. Keep only the attributes listed in §3 and the resource keys in §2. The app itself admits only four
   span names (`web_vital`, `route.change`, `api.request`, `client.error`) and exports anything else
   as `unknown`; the collector may drop `unknown` and should alert on a sudden amount of it, because
   that is instrumentation the app did not write.
3. Attach the environment (and any deployment identity) in the pipeline, since the browser sends none.
4. Never accept a credential: no API key on this listener, and no tenant identity in any attribute.

Recording rules for the SLIs (p95 LCP/INP per route and per environment, CLS good-rate) are 10.4.1-T1's;
this page only fixes the shape they read.
