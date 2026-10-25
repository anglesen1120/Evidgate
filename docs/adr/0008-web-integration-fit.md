# ADR-0008 — Web integration fit: SDK behind `HttpClient`, zoneless, Trusted Types, CDK tables, SignalStore

Legend: ✅ verified with link · ⚠️ unverified · `[INFERENCE]` — mark every external claim with one of these.

## Status

accepted

## Date

2026-10-24

## Context

`plans/09-web-ui.md` §3 D2 left four integration-fit questions to the spike 09.1.1-T2 ("integration fit, not library choice", F-09-2): (a) does the `@hey-api/openapi-ts` SDK fit behind Angular `HttpClient` interceptors, (b) are zoneless change detection and Trusted Types usable (00 §5.6 W3/W6 and DECISIONS §4.2 carried ⚠️), (c) do the CDK primitives carry dense tables and virtual scroll (W7: virtual scroll above 200 rows), (d) what is the `@ngrx/signals` store shape. DECISIONS §4.2 also marks two stack facts ⚠️: the Angular unit-test builder with Vitest, and `@angular/localize` (licence, release line). Nothing in `apps/web` may depend on these until they are settled (09.1.1 §2, third story; §11 rollout: "the spike ADR is reviewed before T3 merges"). Number note: the task file named `0007-web-integration-fit.md`, but `0007` is `0007-single-github-app.md`; this ADR takes the next free number `0008`.

Constraints that shaped the answers: one generated SDK shared by the Node CLI and the browser (`packages/sdk`, `openapi-ts.config.mjs` plugins `@hey-api/client-fetch`, `@hey-api/typescript`, `@hey-api/sdk`, 01.3.2-T1); W5 header duties (`X-Request-Id`/`traceparent`, `Idempotency-Key` per user intent, `X-XSRF-TOKEN`, §9.2 envelope mapping); T38 cookie session with XSRF double-submit; AC7 app-origin CSP (no `unsafe-inline` in `script-src`, style nonce, `connect-src` allowlist, `frame-ancestors 'none'`) plus Trusted Types; W7 budgets (initial ≤ 300 kB gzip, lazy ≤ 150 kB gzip); the pinned set in DECISIONS §4.2 (Angular 22.2.0, TypeScript 6.0.x, `@ngrx/signals` 22.0.1, `@hey-api/openapi-ts` 0.99.0, Vitest 5.0.1, Playwright 1.63.0).

## Decision

All four questions and both stack facts are answered **yes** (a: adapter and same-origin; c: fixed row height). The shapes below are the contract for 09.1.1-T1/T3/T4/T5/T7/T10 and 10.2.1; each ⚠️ that the spike closed is flipped to ✅ in DECISIONS §4.2 and 00 §5.6 W3/W6 in the same PR.

| # | Question | Answer | Shape to build |
|---|---|---|---|
| a | SDK behind `HttpClient` interceptors | **YES** (adapter) | Keep the one generated `client-fetch` SDK. `apps/web` builds its client with `createClient(createConfig({ baseUrl: '/v1', fetch: createHttpClientFetch(inject(HttpClient)) }))`. The adapter turns the SDK's `Request` into `http.request(method, url, { body: <text>, headers, observe: 'response', responseType: 'blob', withCredentials })`, converts the result and every `HttpErrorResponse` back into a fetch `Response` (so the §9.2 envelope reaches the SDK's own error path), and rethrows status `0` as `TypeError`. Cross-cutting concerns are functional `HttpInterceptorFn`s: `X-Request-Id`/`traceparent`, `Idempotency-Key` (fallback only), §9.2 error mapper; XSRF is Angular's `withXsrfConfiguration`. |
| a′ | SDK consumed by the Angular build | **YES** | Workspace dependency on the SDK's `tsc -b` output (`main: dist/src/index.js`), with the web build running `npm run build -w @evidgate/sdk` first. Fallback: `paths` to `packages/sdk/src/index.ts` (the `.js` NodeNext specifiers resolve to `.ts` under `moduleResolution: bundler`). |
| b1 | Zoneless change detection | **YES, stable** | `provideZonelessChangeDetection()` stays explicit in `app.config.ts`; no `zone.js` in `package.json` or `angular.json` polyfills; `OnPush` + state in signals (a plain field mutated in `setTimeout` does **not** re-render). |
| b2 | Trusted Types | **YES, zero violations** | Header `require-trusted-types-for 'script'; trusted-types angular`. The spike build listed `angular#unsafe-bypass` besides `angular`; the app origin drops that entry (amendment of 2026-10-24, S66, D-S66-Q1, after the rules below). `angular#bundler` is not created by the application builder and is not listed. `security.autoCsp` is **rejected** (breaks Trusted Types). |
| c | CDK dense tables and virtual scroll | **YES, with limits** | `@angular/cdk/table` for tables, `@angular/cdk/scrolling` virtual scroll for lists/tables above 200 rows with a fixed row height; variable-height virtual rows are **not** supported in the stable CDK (cap the page at ≤ 200 rows with cursor pagination instead); ARIA gaps are the app's duty (below). |
| d | `@ngrx/signals` store shape | **YES** | `signalStore` + `withState`/`withComputed`/`withProps`/`withMethods`/`withHooks`; `rxMethod` from `@ngrx/signals/rxjs-interop`; list stores keep the cursor only. |
| e | Angular unit-test builder with Vitest | **YES** (no experimental marker in the schema; release-note stability not read ⚠️) | `@angular/build:unit-test`, `runner: vitest`, `reporters: ["default", ["junit", { outputFile }]]` (AC2/T48), `vitest` 5.0.1 plus one DOM library. |
| f | `@angular/localize` | **YES** | MIT, 22.2.0, exact peers on `@angular/compiler`/`compiler-cli` 22.2.0; `ng build --localize` emits `/en/` and `/vi/` (AC6). |

### Rules that follow (enforced in code review and by the tasks named)

1. **Same origin for API and bundle (T3, T5, 10.2.1).** `baseUrl` is the relative `/v1`. Angular's XSRF interceptor skips GET/HEAD, skips any request whose origin differs from the page's, and skips **silently** when `PlatformLocation` cannot be injected; the server then answers 403 `csrf_failed`. The CDN/edge in front of the static bundle must therefore route `/v1/*` to the API on the same origin (T38 `__Host-` cookie already requires a single host). A cross-origin API would need a hand-written `X-XSRF-TOKEN` hook (`createHeaderHookInterceptor` in `packages/sdk/src/headers.ts`) plus CORS credentials; that is an owner decision, not a code choice. Test setups provide `{ provide: PlatformLocation, useClass: BrowserPlatformLocation }`, otherwise XSRF specs pass vacuously.
2. **Idempotency keys are per intent, passed by the caller (T3).** `HttpContext` is unreachable from an SDK call site because the adapter owns the `HttpRequest`. The data service generates one `Idempotency-Key` per user intent and passes it in `options.headers` (the spec requires it on `createRun`); the interceptor only fills a missing key for untyped mutations and never for GET.
3. **Interceptors see text bodies and URL-embedded query strings (T3).** The adapter passes `await request.text()` as the body and the query inside `req.url`; interceptors must not assume JSON objects or `HttpParams`.
4. **Nonce CSP, Recipe B is the default (T10, 10.2.1).** Build with `optimization.styles.inlineCritical: false` and `subresourceIntegrity: false`; `<app-root ngCspNonce="__CSP_NONCE__">` in `index.html`; the edge replaces the placeholder per response. Header: `default-src 'none'; script-src 'self'; style-src 'self' 'nonce-<n>'; connect-src 'self' <allowlist>; img-src 'self' data:; font-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'; require-trusted-types-for 'script'; trusted-types angular` (amended 2026-10-24, S66, D-S66-Q1 — the amendment after this list; the spike's `angular#unsafe-bypass` entry is not shipped at the app origin). This is AC7's text literally (no inline script at all). Recipe A (critical CSS inlined, nonce in **both** `script-src` and `style-src`) is the permitted alternative if the NFR-09-1 LCP measurement needs inline critical CSS. The style nonce is mandatory in both recipes: component CSS is a runtime `<style>` (global CSS was 121 B). Turning `subresourceIntegrity` off is a deliberate **security trade-off** — subresource integrity is not enforced — and its cause is `[INFERENCE]`: a per-response edge rewrite of `index.html` would invalidate integrity hashes, and the SRI build's inline integrity import map does not fit Recipe B's `script-src 'self'`; the evidence records only the SRI build green with 0 violations plus that import map, not a measured reason. The spike listed `angular#unsafe-bypass` only for the 06 artifact-viewer bridge, because W6 allows one reviewed bridge that calls `bypassSecurityTrust*` and with `trusted-types angular` alone that call is blocked (two violations, the HTML never reaches the DOM); 09.1.1-T10 decided the app origin does **not** keep the entry (amendment of 2026-10-24, S66, D-S66-Q1, below) and bans the call by lint instead, while the bridge page's own policy stays a 06.3.3/09.4.1 decision. AC7 asserts the header string itself.
5. **`@angular/localize/init` polyfill in every profile (T1, T6).** Templates with `i18n` attributes compile to `$localize(...)` even in a non-localized build; without `"polyfills": ["@angular/localize/init"]` the app dies at boot with `TypeError: $localize is not a function`.
6. **Budgets are raw bytes (T1.2).** `angular.json` `budgets` compare **uncompressed** size, and a `bundle` budget's `name` must be an exact chunk name (a glob matched nothing). W7's gzip numbers therefore need (i) raw-size `angular.json` budgets as the build-failing mechanism (AC1 "lowering a threshold fails the build" holds) and (ii) a gzip check script over `dist` for the 300 kB / 150 kB gzip limits; measured raw:gzip ratio was ≈ 3.27 (initial 262.05 kB raw / 80.0 kB gzip), so a raw threshold derived from gzip × ratio would be a guess `[INFERENCE]`.
7. **Pins (T1).** Pin every `@angular/*` package to one exact version in lockstep — `22.2.0` at the spike, `22.2.3` (`@angular/cli` 22.2.4) since the S70 lockstep bump; ADR-0002 holds the current rows (caret ranges drifted and broke the exact `@angular/compiler`/`compiler-cli` peers with ERESOLVE). `@types/node@26.10.0` does not exist on the registry (latest seen 26.6.5): the workspace's `@types/node` pin must be checked at install time. `jsdom` (or `happy-dom`) is a new devDependency for the unit-test builder and needs an ADR-0002 matrix row (`check-adr-matrix`) in T1.
8. **CDK tables (T7, 09.3.1 and later list screens).** Default `role="table"` (not `grid`, which would need an APG keyboard layer); `fixedLayout` when columns are known; `dataSource` takes an array, `DataSource` or `Observable` but **not** a `Signal` (use `toObservable()` with CDK `ArrayDataSource`); sticky header row and sticky first column work but the app must paint cell backgrounds; `aria-sort` is set by hand on a real `<button>` in the `<th>`. Virtual scroll: `itemSize` equal to the CSS row height, `trackBy`, and manual `aria-rowcount`/`aria-rowindex` (tables) or `aria-setsize`/`aria-posinset` (lists) plus a focus-restore step, because recycled rows drop focus to `<body>`. Inside a table the viewport wraps the table, `fixedLayout` is forced and `when` row defs throw.
9. **Store shape (T4, T5).** `rxMethod` is **not** exported from the root of `@ngrx/signals`; a `withMethods` factory cannot call methods being defined on the same store (hold the `rxMethod` instance in a local const and reuse it). The guard reads `computed(() => (p: string) => permissions().includes(p))` and returns a `UrlTree` for `/forbidden`.

### Amendment 2026-10-24 (09.1.1-T10, S66, D-S66-Q1) — the app-origin Trusted Types list is `angular` only

Decision row b2 and rule 4 are amended: the app-origin header ships `require-trusted-types-for 'script'; trusted-types angular` with **no** `angular#unsafe-bypass` entry. The rest of the rule 4 header string, Recipe B and the Recipe A alternative are unchanged, and AC7 is unchanged (it asserts `script-src`, the style nonce, the `connect-src` allowlist, `frame-ancestors 'none'` and a Trusted Types policy).

The change is a **tightening** — one policy-list entry and one creatable policy name fewer at the app origin — and it rests on the Evidence section above, not on a new measurement: boot, routing, two lazy chunks and the SRI build already created the `angular` policy only, with 0 violations, and the one place the spike needed the bypass policy was its `bypassSecurityTrustHtml` probe, i.e. exactly the call 00 §5.6 W6 forbids outside the reviewed 06 artifact-viewer bridge. Application code therefore never created an `angular#unsafe-bypass` policy; listing it only made an accidental bypass compile and render instead of failing loudly.

Enforcement moves from the header to two places that fail earlier: the lint rule `evidgate/no-bypass-security-trust` (`apps/web/lint/rules/no-bypass-security-trust.cjs`) reports every `DomSanitizer.bypassSecurityTrust{Html,Style,Script,Url,ResourceUrl}` access, call or destructuring under `apps/web/src`, and at run time the app-origin policy refuses the same value when it reaches a DOM sink, wherever the code sits, because the policy list has no `angular#unsafe-bypass` entry that could hand the sink a value — a refusal at the sink, not a throw from the call. The rule's single allow-list is a directory named `artifact-viewer-bridge` below `apps/web/src/app` (D-S66-Q2) and is a lint exemption only: 09.4.1-T5 places the bridge there or amends that constant, and whether the bridge itself may render is its own origin policy's question (06.3.3 / 09.4.1-T5, open there).

Two things are deliberately **not** settled here. (i) The bridge page's own policy: whether the isolated 06 viewer origin needs `angular#unsafe-bypass` at all is decided against 06.3.3/09.4.1 when that component exists, because the viewer content lives on the isolated origin, not on the app origin. (ii) The Evidence section is left as recorded: the spike header `trusted-types angular angular#unsafe-bypass` stays readable there as the spike's own configuration and is not the shipped app-origin policy.

Shipped artefacts (D-S66-Q3): `apps/web/scripts/app-origin-csp.json` is the single policy source (10.2.1-T1.4's edge consumes it), `apps/web/scripts/csp.mjs` builds the header from it and injects the per-response nonce, `apps/web/src/index.html` carries `ngCspNonce="__CSP_NONCE__"`, and `apps/web/e2e/csp.spec.ts` is AC7's proof on both locales. Recorded for owner review (non-blocking) as D-S66-Q1 in `docs/phase-review.md`.

### Fallbacks (each ⚠️ that did not come out fully yes)

| Case | Fallback |
|---|---|
| The `fetch` → `HttpClient` adapter (~100 lines) becomes a maintenance burden | Second generation of the same spec with `@hey-api/client-angular` (exists in 0.99.0; 16 files, 368 KB, imports only `@angular/common`, `@angular/core`, `rxjs`, no runtime package; requests go through the `HttpClient` chain natively) plus a second drift check; the CLI keeps the fetch SDK. Cost: one more committed generated tree. |
| A library forces zone.js | `provideZoneChangeDetection({ eventCoalescing: true })` and `"polyfills": ["zone.js"]` (`@angular/core` optional peer `zone.js ~0.15.0 \|\| ~0.16.0`). Not exercised by the spike ⚠️ (zone.js was never installed). |
| The hosting layer cannot replace a per-response nonce | Angular's `security.autoCsp` plus `style-src 'self' 'unsafe-inline'` would force dropping `require-trusted-types-for 'script'` and is weaker than AC7: that needs an explicit owner waiver against FR-09-15 and is **not** adopted here. |
| Variable-height rows must be virtualised | `@angular/cdk-experimental` `autosize` (MIT, exact `@angular/cdk` peer, experimental) is **not** adopted (not in DECISIONS §4.2; a dependency decision for the owner); the default is cursor pagination with page size ≤ 200. A hand-rolled windowing directive (~60–80 lines) is the no-dependency alternative. |
| Unit-test builder gaps | `"runner": "karma"` on the same target (the schema enum is `["karma", "vitest"]`; the same options are accepted; Karma packages and their ADR-0002 rows are not installed or verified ⚠️). |

## Alternatives rejected

- **Hand-written `HttpClient` data layer instead of the generated SDK** — contradicts D5 and FR-09-10 (no hand-written wire shapes) and forks the CLI/web contract.
- **SDK request hooks only, no `HttpClient` (shape B)** — works (`createHeaderHookInterceptor` set all three headers; real-server tests passed) and is right for the Node CLI, but with `provideHttpClient` present `HttpTestingController` sees zero requests, Angular's interceptors and `withXsrfConfiguration` never run, and the app would re-implement cookie parsing, same-origin checks and the GET/HEAD skip outside Angular's tested code.
- **`@hey-api/client-angular` as the primary web SDK (shape C)** — viable (kept as the fallback above) but it doubles the generated tree and the drift gate for a benefit the ~100-line adapter already gives.
- **`security.autoCsp`** — its loader assigns a raw string to `HTMLScriptElement.src` (a Trusted Types sink, app never boots under enforcement) and the emitted `script-src` keeps `'unsafe-inline'` as a fallback; adopting it means dropping Trusted Types (W6/FR-09-15).
- **`CSP_NONCE` provider for the nonce** — documented as incompatible with critical-CSS inlining; usable only with Recipe B, where `ngCspNonce` also works, so one mechanism is chosen.
- **Zone.js plus change-detection strategy defaults** — no benefit once zoneless is stable and the negative test shows the failure mode is detectable.
- **Hand-rolled virtual scroll / grid** — no dependency, but gives up CDK buffering and table integration; kept as a fallback only.
- **A router/data-fetching library** — closed by CR-D-1 (09-web-ui.md §11 #1); not in scope.

## Consequences

- Unblocks 09.1.1-T1 (zoneless line `provideZonelessChangeDetection` ⚠️ in T1.1 is now ✅; T1.2 gets the budget rule above), T3 (adapter + interceptors), T4/T5 (store/guard shape, same-origin XSRF), T6 (`localize/init`, per-locale builds), T7 (CDK table rules), T10 (CSP/TT header set). 10.2.1 reuses the header set and the placeholder-replacement requirement as its production acceptance check (09.1.1 §3, T63).
- Costs: one maintained adapter file with the recorded gotchas (below); a gzip-check script beside raw budgets; edge nonce replacement is a hosting requirement (a pure static host without an edge transform cannot meet AC7 with Trusted Types); the web and API must share an origin.
- Adapter gotchas to carry into T3's tests: the generated client calls `new Request(url)`, so a relative base only works in a document context (tests use an absolute base `new URL('/v1', location.href).href`); `HttpClient` throws on non-2xx whereas the SDK expects a `Response`; `blob` bodies must pass through unchanged (a cross-realm jsdom `Blob` is stringified by Node's `Response`, converted to bytes only when `blob.stream` is missing); `HttpTestingController` cannot flush objects for `responseType: 'blob'` and requires `statusText` on custom statuses.
- Nonce assertions in E2E must read the IDL property (`style.nonce`); `getAttribute('nonce')` returns `""` by design.
- No migration, no server change, no new runtime dependency by this ADR; the new devDependency (`jsdom`/`happy-dom`) and the CDK are covered by T1's ADR-0002 rows.

## Evidence

All commands ran on 2026-10-24 on macOS arm64, Node v26.10.0, npm 11.19.1, headless Chrome for Testing 153.0.8010.12 (via `@playwright/test` 1.63.0), in throwaway directories `/tmp/evidgate-spike-s60/{sdk-store,zoneless-tt,cdk-table}/` that are not part of the repository. Every package was installed at the exact pin and read back from `node_modules/<pkg>/package.json`:

```
@angular/core 22.2.0 MIT   @angular/build 22.2.0 MIT   @angular/cli 22.2.0 MIT
@angular/localize 22.2.0 MIT   @angular/cdk 22.2.0 MIT   typescript 6.0.3 Apache-2.0
vitest 5.0.1 MIT   @playwright/test 1.63.0 Apache-2.0   @ngrx/signals 22.0.1   @hey-api/openapi-ts 0.99.0
```

Registry sources ✅: [`@angular/core@22.2.0`](https://registry.npmjs.org/@angular/core/22.2.0) (engines `node ^22.22.3 || ^24.15.0 || >=26.0.0`, optional peer `zone.js ~0.15.0 || ~0.16.0`), [`@angular/build@22.2.0`](https://registry.npmjs.org/@angular/build/22.2.0) (peers `typescript >=6.0 <6.1`, `vitest ^4.0.8 || ^5.0.0`), [`@angular/localize@22.2.0`](https://registry.npmjs.org/@angular/localize/22.2.0) (MIT; peers `@angular/compiler`, `@angular/compiler-cli` exactly 22.2.0), [`@angular/cdk@22.2.0`](https://registry.npmjs.org/@angular/cdk/22.2.0) (MIT; peers core/common/forms/platform-browser `^22.0.0 || ^23.0.0`, rxjs), [`@angular/cdk-experimental@22.2.0`](https://registry.npmjs.org/@angular/cdk-experimental/22.2.0) (MIT; exact `@angular/cdk` peer), [`vitest@5.0.1`](https://registry.npmjs.org/vitest/5.0.1) (MIT; peer `vite ^6.4 || ^7 || ^8`; `jsdom`/`happy-dom` optional), [`@ngrx/signals@22.0.1`](https://registry.npmjs.org/@ngrx/signals/22.0.1). Documentation read ✅: [angular.dev zoneless guide](https://angular.dev/guide/zoneless), [angular.dev security best practices (Trusted Types, CSP nonce)](https://angular.dev/best-practices/security), [angular.dev testing guide](https://angular.dev/guide/testing), [CDK table overview](https://material.angular.dev/cdk/table/overview). The Angular v22 release page (<https://angular.dev/events/v22>) does not mention zoneless; the Hey API Angular client and CDK scrolling pages were not read (shape C and virtual scroll rest on the installed packages and measurements) ⚠️.

### (a) SDK behind `HttpClient` (`sdk-store`, 22 tests in 3 files, all green)

```
$ npx openapi-ts -f openapi-ts.config.mjs && diff -r .gen-committed packages/sdk/gen && echo IDENTICAL_REGEN
@hey-api/openapi-ts v0.99.0   [Job 1] ✓ ./packages/sdk/gen · 4 files · 152ms   IDENTICAL_REGEN
$ npm run build -w @evidgate/sdk   → tsc -b, EXIT=0 (dist/src/index.js present)
$ npx ng test --watch=false   → Test Files 3 passed | Tests 22 passed
```

The spec's external `$ref` files require the whole `contracts/` directory, not only `openapi.yaml`. With `dist/` renamed away the dist mode fails and the `paths`-to-source mode builds (`EXIT=0`). The generated client reads `fetch: options.fetch ?? _config.fetch ?? globalThis.fetch` (`gen/client/client.gen.ts:45`), builds `new Request(url, init)`, runs `interceptors.request`, resolves non-2xx to `{ error, response }` unless `throwOnError`. Shape A tests (`sdk-http-fit.spec.ts`, 9): A1 typed GET carries `X-Request-Id` (uuid v4), no XSRF; A2 POST carries `X-XSRF-TOKEN: abc123` from the cookie and the caller's `Idempotency-Key: intent-42`; A3 absolute cross-origin base ⇒ no XSRF header; A4 interceptor adds a key only for untyped mutations; A5 403 `csrf_failed` envelope survives (`result.error.error.code === 'csrf_failed'`, `request_id` intact); A6 429 keeps `Retry-After: 120`, `retryable: true`; A7 401 resolves with `unauthenticated`; A8 a non-envelope 502 HTML body is flagged `envelope_missing`; A9 without `PlatformLocation` XSRF is silently skipped. Source of the XSRF rule: `node_modules/@angular/common/fesm2022/_module-chunk.mjs:1162-1188` (`xsrfInterceptorFn`: skip when `!XSRF_ENABLED`, GET/HEAD, origin mismatch, or any exception while injecting `PlatformLocation`). Shape B tests (`sdk-hooks.spec.ts`, 6, B1–B6): hooks set all three headers and never overwrite a caller header; with `provideHttpClient(...)` present `HttpTestingController` saw zero requests; real `node:http` server on an ephemeral port returned 201 / 401 / 403 `csrf_failed` / 429 with `Retry-After: 30` preserved. Shape C: `ls node_modules/@hey-api/openapi-ts/dist/clients/` → `angular axios core fetch ky next nuxt ofetch`; scratch generation with `plugins: ['@hey-api/client-angular', '@hey-api/typescript', '@hey-api/sdk']` → 16 files, 368 KB, `provideHeyApiClient(client)` sets `httpClient: inject(HttpClient)`.

Bundle cost of the SDK: production `ng build` of the spike app (Angular, router, `@ngrx/signals`, SDK, app code) — initial 247.71 kB raw / 70.69 kB Angular-estimated transfer (78,537 B ≈ 76.7 KiB by `gzip -9` per file); the SDK alone, tree-shaken to two operations, 9,816 B raw / 3,997 B gzip.

### (d) `@ngrx/signals` store (`session.store.spec.ts`, 7 tests, zoneless)

`@ngrx/signals` root exports (read from the installed typings): `signalStore`, `signalState`, `signalMethod`, `patchState`, `getState`, `watchState`, `withState`, `withComputed`, `withMethods`, `withProps`, `withHooks`, `withFeature`, `deepComputed`, `withLinkedState`; `@ngrx/signals/rxjs-interop` exports exactly `rxMethod`. The seven tests (D1–D7) cover the permission lookup, org/project switching, a failing `load()` ⇒ `sessionState: 'expired'`, a zoneless DOM update (`'Zone' in globalThis === false`, no `detectChanges()`), the `CanMatchFn` returning `true` or a `UrlTree` to `/forbidden`, and a runs list store that keeps only the cursor. `sdk-store/EVIDENCE.md` §3 holds the details.

### (b) Zoneless and Trusted Types (`zoneless-tt`, 19/19 Playwright tests green)

```
$ grep: node_modules/@angular/core/types/core.d.ts:3466  @publicApi 20.2   (provideZonelessChangeDetection; no @developerPreview/@experimental)
  "NOTE: Zoneless is enabled by default in Angular v21+."   (same JSDoc, line 3437; angular.dev/guide/zoneless)
✓ no zone.js is loaded and no zone script is referenced
✓ signal set in a click handler / inside setTimeout / HttpClient response / lazy router navigation update the DOM
✓ NEGATIVE: a plain field mutated in setTimeout does NOT refresh the view
POLICIES CREATED: [{"name":"angular","at":163}]   VIOLATIONS: []   CONSOLE: []
```

Policies per scenario: boot + routes + 2 lazy chunks creates `angular` only (0 violations; the SRI build too, so rule 4's `subresourceIntegrity: false` is a deliberate security trade-off and not an untested path); `bypassSecurityTrustHtml` under `trusted-types angular` gives 2 violations (`trusted-types` sample `angular#unsafe-bypass` and `require-trusted-types-for`) and the HTML never renders; under `trusted-types angular angular#unsafe-bypass` 0 violations. `grep -o "angular#[a-z-]*" dist/…/*.js` finds only `angular#unsafe-bypass`. Recipe B served with the header in rule 4: `SELF-ONLY SCRIPT SRCS: ["polyfills-….js","main-….js"]` (no inline script), 2 runtime `<style>` elements carrying the per-response nonce, `SELF-ONLY VIOLATIONS: []`. Recipe A served with the nonce in both `script-src` and `style-src` also gave 0 violations. Injection probe: `[innerHTML]="<img src=x onerror=window.__pwn=1><b>safe bold</b>"` and a `javascript:` `[href]` render as an inert `<img src="x">`, `unsafe:javascript:…`, `window.__pwn: undefined`. Negative control: a raw `element.innerHTML = '<img src=x onerror=…>'` is reported as a violation (and executes with Trusted Types off), so the harness would catch a regression. `ng build --configuration autocsp` (`security.autoCsp: true`) emits a hashed inline loader that assigns `HTMLScriptElement.src` a string: violation, app never boots. First build without the localize polyfill: `TypeError: $localize is not a function` at `consts(...)`; with `"polyfills": ["@angular/localize/init"]` the polyfills chunk is 534 B.

### (e) and (f) Unit tests and localize (`zoneless-tt`)

```
$ npx ng test   → RUN v5.0.1 … src/app/home/home-page.spec.ts (3 tests)  Tests 3 passed (3)
JUNIT report written to …/junit/unit-junit.xml   <testsuites name="vitest tests" tests="3" failures="0" errors="0">
$ npx ng build --localize --output-path dist/localize-flag   → dist/localize-flag/browser/en  dist/localize-flag/browser/vi
```

Without `jsdom`/`happy-dom` the builder stops with `A DOM environment is required for non-browser tests. Please install either "jsdom" or "happy-dom".` (`runners/vitest/index.js:43`); the spike used `jsdom@30.1.3`. The builder schema has no experimental marker, `runner` defaults to `vitest`, and `reporters` accepts `junit`. Release-note wording that Vitest support was stabilised in v21 was **not** read ⚠️; stability here rests on the schema, the default scaffold and angular.dev's description of Vitest as the default. `/vi/index.html` has `<html lang="vi">`, `<base href="/vi/">`; the `vi` polyfills set `(globalThis.$localize??={}).locale="vi"`; a deep link `/vi/probe` loaded its lazy chunk under enforcement with `VI VIOLATIONS: []`.

### (c) CDK table and virtual scroll (`cdk-table`, headless Chrome 153, synthetic ~5-node rows)

| Measurement | Result |
|---|---|
| Dense table 50 × 8: numeric column, truncated cell with `title`, sticky header, sticky first column | renders; real scroll `scrollTop 250, scrollLeft 300` kept header and first column within 1 px |
| `dataSource` input type (`types/table.d.ts:359`) | `readonly T[] \| DataSource<T> \| Observable<readonly T[]>`; no `Signal` |
| Virtual scroll 10 000 rows | 33 → 43 → 23 rendered rows, `scrollHeight` 320 000 px, row 10 000 reachable, first row 11.2 ms, 196–246 DOM nodes (146 at the end of the scroll), 6.06 MiB heap, 16.3 ms frames |
| Virtual scroll inside the table (sticky header) | header offset 1 px at `scrollTop 160 000`; `fixedLayout` forced; `when` row defs throw; manual `aria-rowcount="10001"` and `aria-rowindex` verified |
| Variable row heights | stable CDK: none (fixed strategy ≈ 704 rows off at the expected offset); only `@angular/cdk-experimental` autosize is exact |
| ARIA out of the box | `role="table"` default; no `aria-sort`, `aria-rowcount`, `aria-rowindex`, `aria-setsize`; `role="grid"` is opt-in and ArrowDown does nothing; a focused row scrolled out drops focus to `<body>` |
| 200-row threshold | 200 rows plain `@for`: 7.4 ms, 3.5 MiB, no long task; 10 000 plain rows: 45 ms + 174 ms long task at 1× CPU, 901 ms first row + 958 ms long task at 4× CPU, 50 026 DOM nodes; `@for` without `track` is build error `NG5002` |
| Bundle | initial 262.05 kB raw / 80 018 B gzip; lazy gzip: table 8 357 B, scrolling 7 706 B (largest = 5.6 % of 150 kB); CDK is 100 % lazy |
| Budget mechanics | `angular.json` budget 20 kB on the initial bundle: over by 242.05 kB (total 262.05 kB raw), `✘ [ERROR] bundle initial exceeded maximum budget. Budget 20.00 kB was not met by 242.05 kB`, exit 1; budgets compare raw bytes (ratio 3.27) |

Not verified ⚠️: virtual scroll combined with horizontal overflow (sticky first column), real screen-reader behaviour of `aria-rowindex` in a virtual table, Firefox/Safari `position: sticky` in the viewport, and rows heavier than ~5 DOM nodes (re-measure on a real route template before 09.3.1 fixes its page size). Chrome build: the CDK measurements ran in headless Chrome for Testing **153.0.8010.12**, the build `@playwright/test` 1.63.0 downloads. The CDK notes contradict themselves on that number — the header says 153, one open-items line says "Chromium 156" — and the open-items number is the wrong one `[INFERENCE]`; the header value matches the recorded build.

## Revisit trigger

- Angular, `@hey-api/openapi-ts` or `@ngrx/signals` leaves the DECISIONS §4.2 pins (re-run the adapter, XSRF, zoneless and Trusted Types tests before the bump).
- The API is served from a different origin than the web bundle (rule 1), or the hosting layer cannot replace a per-response nonce (fallback table).
- A shipped screen needs virtualised rows of variable height or more than ~5 DOM nodes per row at over 200 rows (re-measure; consider the `cdk-experimental` decision).
- `security.autoCsp` or the application builder starts working under `require-trusted-types-for 'script'`.
- The NFR-09-1 LCP measurement (OI-09-02) shows Recipe B's non-inlined critical CSS costs too much: switch to Recipe A.
