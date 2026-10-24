# @evidgate/web

Placeholder npm workspace for the Angular SPA. Owned by story 09.1.1-T1, which replaces this package.json with the real Angular 22 application (plans/09-web-ui.md, 00 §5.2).

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

## E2E (Playwright) — 01.6.1-T5

`playwright.config.ts` is the web E2E workspace: one `chromium` project, retries 2 when `CI` is
set, `trace: 'retain-on-failure'`, and a `webServer` that starts `e2e/static-server.mjs`
(a dependency-free Node static server over `e2e/public/`). Until 09.1.1-T2 lands the Angular
application, that static page is what `e2e/smoke.spec.ts` asserts (AC7); drop the `webServer`
block for `ng serve`/the built bundle then.

```sh
npm run e2e -w apps/web   # or: npx playwright test, with apps/web as the working directory
```

Locally the first run needs the pinned browser build once:

```sh
npx playwright install chromium
```

Vitest owns `src/**/*.test.ts` and never collects these `*.spec.ts` files (the root
`vitest.config.ts` lists the workspaces as `test.projects`, and `vitest.config.base.mts` fixes
the `include` glob), so `npx vitest run` stays E2E-free.

## CI hooks for 01.7.1-T1

Consumed by 01.7.1-T1.3 (`ci.yml`, job wired **non-blocking** — `continue-on-error: true` — until
09 has screens). No workflow file lives in this workspace.

- **Job:** `e2e`, `runs-on: ubuntu-24.04` (pinned per 01.7.1-T1: `ubuntu-latest` moves to
  Ubuntu 26 on 2026-10-19 — matches the .NET jobs' pin), independent of the .NET jobs. Steps: checkout →
  Node 26 setup → `npm ci` → `npx playwright install --with-deps chromium` → `npm run e2e -w apps/web`.
  Keep the browser cache step before the install so a cache hit makes `install` a no-op.
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
  `.zip` traces in `test-results/` open in `npx playwright show-trace`. The `html` report is for
  humans; the Evidgate-side dogfood (01.10.1-T1.1) needs the Playwright `junit` reporter, which
  T1.1 adds — the plan's `junit` config is not wired here.
- **Retries:** 2 on CI (`process.env.CI`), 0 locally; `forbidOnly` is set on CI so a committed
  `test.only` fails the job instead of silently shrinking the suite.
