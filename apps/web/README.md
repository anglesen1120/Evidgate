# @evidgate/web

The Evidgate Angular 22.2 single-page app (`plans/09-web-ui.md`, 00 §5.2): standalone components
with `ChangeDetectionStrategy.OnPush`, zoneless change detection, no `NgModule`. Story 09.1.1-T1
replaced the 01.1.1-T3 placeholder in this workspace with the real application project; today it
renders the shell placeholder heading of `src/app/app.html`, and the shell, guards and screens
land in 09.1.1-T4 and 09.2.1+.

## Running it

Run from the repository root (npm workspaces) or from `apps/web`:

| Command                     | What it does                                                                                                 |
| --------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `npm run start -w apps/web` | `ng serve` — the dev server with the `development` configuration (no optimization, source maps)              |
| `npm run build -w apps/web` | `ng build` (production) into `apps/web/dist/browser`, then `node scripts/check-bundle-budget.mjs`            |
| `npm run test -w apps/web`  | `ng test --watch=false` — component/unit specs, writes `apps/web/test-results/junit.xml`                     |
| `npm run e2e -w apps/web`   | `npm run build && playwright test` — builds, then runs E2E against the built bundle ([E2E](#e2e-playwright)) |

Build output and reports are git-ignored: `apps/web/dist/`, `apps/web/.angular/` (build cache),
`apps/web/test-results/` (JUnit XML, traces) and `apps/web/playwright-report/`.

### Unit tests

Component specs run through the Angular builder (`angular.json` target `test`,
`@angular/build:unit-test`) with the **Vitest** runner, the **jsdom** environment and a zoneless
TestBed (`src/zoneless.spec.ts` guards the zoneless setup). The builder's `junit` reporter writes
`apps/web/test-results/junit.xml` for the Evidgate dogfooding of 01.10.1-T1.1 (T48).

`apps/web` is **not** a project of the root `vitest.config.ts` any more: a root `npx vitest run`
collects only `packages/*`, because running the Angular specs outside the builder would skip its
TestBed and jsdom wiring. `npm run test --workspaces` still reaches them through the script above.

### Build budgets (00 §5.6 W7)

W7 allows **300 kB gzip** for the initial JS and **150 kB gzip** for any lazy chunk. Two gates
enforce it, and only one of them is `angular.json`:

- `apps/web/scripts/check-bundle-budget.mjs` is the W7 gate. It gzips the emitted scripts, takes
  the initial set from the `<script>`/`modulepreload` tags of `dist/browser/index.html`, and fails
  the build on a breach. Chained after `ng build` by `npm run build -w apps/web`.
- The `budgets` block of `angular.json` is a **raw-size backstop**, not the gzip numbers of W7:
  the installed `@angular/build` compares `budgets` against on-disk asset bytes and never gzips,
  so it cannot express W7 as written (the AC1 wording gap). Its thresholds sit at 3× the gzip
  targets (initial 900 kB error / 600 kB warning, `anyScript` 450/300 kB, component style
  12/6 kB), which still makes AC1's threshold test — lowering a budget below the current output
  size fails the build — hold for that backstop.

The production configuration sets `optimization.styles.inlineCritical=false` (scripts and style
minification stay on), so `dist/browser/index.html` carries no inline `<script>` or `<style>` and
can be served under a strict CSP without `unsafe-inline` in 09.1.1-T10.

`@evidgate/sdk` is the workspace link `"*"` and is consumed through [Data access](#data-access-0911-t3)
below — nothing in this app calls it outside `src/app/core`.

### Zoneless change detection

`src/app/app.config.ts` provides zoneless change detection and `zone.js` is not a dependency.
ADR-0008 (`docs/adr/0008-web-integration-fit.md`, 09.1.1-T2) confirmed zoneless as stable for this
app (00 §5.6 W3 is ✅); its fallbacks are recorded there rather than silently in code. The same ADR
settled the API integration shape described in [Data access](#data-access-0911-t3).

## Data access (09.1.1-T3)

One SDK client, one gateway, no wire types. `src/app/core/http/api-client.ts` builds the single
`@evidgate/sdk` client on top of Angular `HttpClient` (ADR-0008 shape A):

- **Client.** `createClient(createConfig({ baseUrl: '/v1', credentials: 'same-origin', fetch: createHttpClientFetch(inject(HttpClient)) }))`,
  provided as the `API_CLIENT` token by `provideApi()`. `provideApi()` also installs
  `provideHttpClient(withInterceptors([requestIdInterceptor, idempotencyInterceptor]), withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }))`,
  and `app.config.ts` adds it to the app providers. The API must stay on the page origin under
  `/v1` (ADR-0008 rule 1): a foreign origin or a non-`/v1` path never receives an API header.
- **Gateway.** `ApiGateway.call((client) => listRuns({ client, path: { p } }))` resolves the SDK's
  2xx body with the operation's own inferred type and throws `ApiRequestError` for anything else —
  a 00 §9.2 envelope mapped to a typed `ApiError` (`not_found`, `csrf_failed`,
  `idempotency_conflict`, `precondition_failed` + `details`, `validation`, `rate_limited` +
  `retryAfterSeconds`, `unavailable`, …) or `kind: 'network'` for a transport failure. A 2xx
  without a body resolves `undefined`; an `AbortError` is re-thrown untouched. A feature screen
  never calls an SDK operation directly: it injects a data service.
- **Intent.** A mutation's `Idempotency-Key` belongs to one user action: mint
  `createIntent()` when the button is pressed and pass the same `Intent` to every retry of that
  action (`service.create(projectId, body, intent)`); a new action mints a new intent. A key is
  never shared across actions. `src/app/core/data/runs-data.service.ts` is the reference data
  service every feature service copies.
- **Header duties** (00 §5.6 W5): `X-Request-Id` + `traceparent` on every `/v1` request
  (`requestIdInterceptor`); `Idempotency-Key` from the caller's intent, with the interceptor
  filling only a missing key on untyped mutations and never on GET/HEAD/OPTIONS
  (`idempotencyInterceptor`); `X-XSRF-TOKEN` from the readable `XSRF-TOKEN` cookie, added by
  Angular's own interceptor on same-origin mutations only (T38).
- **Contract smoke.** `src/app/core/http/contract-smoke.spec.ts` re-derives the consumed
  operations from `contracts/openapi.yaml` on every run, so the recorded fixture
  `src/app/core/http/testing/openapi-operations.fixture.json` cannot drift silently, and then
  drives each recorded operation through the real stack (SDK function → gateway → adapter →
  `HttpClient`) to assert the method and `/v1` URL on the wire. Re-record after a contract or SDK
  change, from the repository root:

  ```sh
  node apps/web/scripts/record-openapi-fixture.mjs           # rewrite the fixture
  node apps/web/scripts/record-openapi-fixture.mjs --check   # diff summary, exit 1 on drift
  ```

  The recorder reads `contracts/openapi.yaml` with the root `yaml` devDependency. A new operation
  is added in three places: the recorder's `RECORDED_OPERATION_IDS`, the smoke's
  `OPERATION_DRIVERS`, and the re-recorded fixture.

## Design reference — Web UI v2 (S50)

The screens this app implements are mocked up as static HTML in [`docs/design/web-ui/output/`](../../docs/design/web-ui/output/). Open `index.html` there for the page list, `00-components/` for the component gallery, and [`NAV.md`](../../docs/design/web-ui/output/NAV.md) for every page with its CR-D-2 phase (G1–G4). How the pages were generated and reviewed is in [`RUNS.md`](../../docs/design/web-ui/output/RUNS.md). The mockups are a reference, not code to copy: owner approval of the CR-D-2 screens (gate B2 in [`prompts-crd2/00-README.md`](../../docs/design/web-ui/prompts-crd2/00-README.md)) and the 09.1.1-T11 design tasks still come before feature screens.

What the mockups fix, and where it lands in this app:

| Mockup                                             | Content                                                                                                                                                                                                                          | Target in `apps/web`                                                  |
| -------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------- |
| `assets/evidgate.css`, `@layer tokens`             | `--eg-*` custom properties: colour roles (surface, text, border, accent, success/danger/warning/info/neutral fg+bg), type scale 12–28 px, weights, spacing, radius; dark values under `[data-theme="dark"]`                      | `src/styles/tokens.css` (09.1.1-T11.2)                                |
| `assets/evidgate.css`, `@layer layout, components` | Shell (`eg-shell`, `eg-topbar`, `eg-nav`), and components such as `eg-btn`, `eg-badge`, `eg-table`, `eg-toolbar`, `eg-dialog`, `eg-drawer`, `eg-tabs`, `eg-state`, `eg-banner`, `eg-tree`, `eg-editor`, `eg-diff`, `eg-timeline` | Shared components of 09.1.1-T7/T8 and the state sheet of 09.1.1-T11.4 |
| Nav breakpoints in `assets/evidgate.js`            | Sidebar expanded at ≥ 1280 px, icon rail at 768–1279 px, drawer below 768 px                                                                                                                                                     | Layout rules of 09.1.1-T11.3                                          |
| Nav registry in `assets/evidgate.js`               | One list of routes with label (vi + en), group, phase and parent; the sidebar is generated from it and hides routes of a later phase                                                                                             | Route table and menu config, one source for both                      |
| Locale and theme toggles                           | vi default, en; light and dark; the choice is persisted (`evidgate-locale`, `evidgate-theme`)                                                                                                                                    | i18n setup and the theme toggle of 09.1.1-T11.2                       |
| State switch on each page                          | default, loading, empty, error, 403, 412 where the page saves, and page-specific states, shown in place                                                                                                                          | The state each component and screen must render                       |

Rules the mockups keep and the app keeps too: completeness, outcome and decision are separate fields; a missing or pending result never renders as PASS; healing is proposed as a PR and never applied from the UI; controls in one row share a height, action labels do not wrap, and nothing scrolls sideways at 360 px.

## E2E (Playwright)

`playwright.config.ts` is the web E2E workspace: one `chromium` project, `retries: 2` when `CI`
is set (0 locally), `forbidOnly` on CI, `trace: 'retain-on-failure'`,
`screenshot: 'only-on-failure'`, and three reporters — `list`, `html` (into
`apps/web/playwright-report/`) and `junit` (into `apps/web/test-results/e2e-junit.xml`, read by the
01.10.1-T1.1 dogfooding).

The suite runs against the **built** application, never `ng serve`: the `webServer` block starts
`e2e/static-server.mjs`, a dependency-free Node http server over `apps/web/dist/browser`, with SPA
fallback to `index.html` for client routes, correct content types for the emitted assets, no
directory listing, and a traversal guard so no request can read outside `dist/browser`. The server
picks an **ephemeral** high port (chosen once per run in `playwright.config.ts` and passed on
through `EVIDGATE_WEB_E2E_PORT`, so parallel worktrees never share 4200/8080/8765 or a fixed
4173), and it exits with a "run `npm run build -w apps/web`" message when
`dist/browser/index.html` is missing — a stale dist is a loud failure, not a silent fallback to
some other page.

```sh
npm run e2e -w apps/web   # builds first (`npm run build`), then runs Playwright
npx playwright test       # inside apps/web: skips the build, needs an existing dist/browser
```

Locally the first run needs the pinned browser build once:

```sh
npx playwright install chromium
```

Specs: `e2e/smoke.spec.ts` (AC2 — the built bundle bootstraps: page title `Evidgate`, the
`app-root` heading from `src/app/app.html`, and no console or page errors during load) and
`e2e/static-server.spec.ts` (SPA fallback and the traversal guard of the server itself).

Vitest owns `src/**/*.spec.ts` through the Angular builder and never collects these `*.spec.ts`
files: the root `vitest.config.ts` lists only `packages/*` as projects, so `npx vitest run` stays
E2E-free.

## CI hooks for 01.7.1-T1

Consumed by 01.7.1-T1.3 (`ci.yml`, job wired **non-blocking** — `continue-on-error: true` — until
09 has screens). No workflow file lives in this workspace.

- **Job:** `e2e`, `runs-on: ubuntu-24.04` (pinned per 01.7.1-T1: `ubuntu-latest` moves to
  Ubuntu 26 on 2026-10-19 — matches the .NET jobs' pin), independent of the .NET jobs, 20-minute
  timeout. Steps: checkout → Node from `.nvmrc` (26) with npm cache → `npm ci --ignore-scripts` →
  `npx playwright install --with-deps chromium` → `npm run e2e -w apps/web`.
  Keep the browser cache step before the install so a cache hit makes `install` a no-op.
- **Build before the test run:** the job runs only `npm run e2e -w apps/web`, and that script is
  `npm run build && playwright test`, so `ci.yml` needs no separate build step. `npm run build`
  inside the workspace is `ng build` plus the W7 budget gate, so the E2E job also proves the
  bundle is inside budget. `playwright.config.ts` deliberately does not build, so a bundle
  missing from `dist/browser` stays a loud failure instead of a hidden rebuild.
- **Browser cache:** the default cache directory on Linux is `~/.cache/ms-playwright`; set
  `PLAYWRIGHT_BROWSERS_PATH: ~/.cache/ms-playwright` on the job so the path is explicit.

  ```yaml
  - uses: actions/cache@v4
    with:
      path: ~/.cache/ms-playwright
      key: playwright-${{ runner.os }}-chromium-${{ hashFiles('package-lock.json') }}
      restore-keys: playwright-${{ runner.os }}-chromium-
  ```

  The lock-file hash pins the cache to the `@playwright/test` version, so bumping the runner
  invalidates it exactly. `chromium` is the only browser installed, so the key names it.

- **Artifacts on failure:** the `list` + `html` reporters and `trace: 'retain-on-failure'` write
  into `apps/web/playwright-report/` and `apps/web/test-results/` (both git-ignored). Upload both
  with `if: failure()`, `retention-days: 14` (01.7.1 §8), artifact name `playwright-report`; the
  `.zip` traces in `test-results/` open in `npx playwright show-trace`. The `junit` reporter writes
  `apps/web/test-results/e2e-junit.xml` beside them, the E2E counterpart of the unit-test JUnit
  XML that the dogfooding of 01.10.1-T1.1 consumes.
- **Retries:** 2 on CI (`process.env.CI`), 0 locally; `forbidOnly` is set on CI so a committed
  `test.only` fails the job instead of silently shrinking the suite.
