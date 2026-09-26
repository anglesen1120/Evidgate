# @evidgate/web

Placeholder npm workspace for the Angular SPA. Owned by story 09.1.1-T1, which replaces this package.json with the real Angular 22 application (plans/09-web-ui.md, 00 §5.2).

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
