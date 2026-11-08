# Core-flow E2E suite

The short "is the product usable" gate for the six core flows, against the fixtures build
(`apps/web/playwright.core.config.ts`); one spec per built flow, reusing `e2e/shell/**`.

| Flow                                            | Spec                            |
| ----------------------------------------------- | ------------------------------- |
| Login (anonymous deep link, return URL)         | `login.spec.ts`                 |
| Project switch (org/project from the shell)     | `project-switch.spec.ts`        |
| Run detail (list to detail, missing-shard FAIL) | `run-detail.spec.ts`            |
| Onboarding (empty org to evidence step)         | `onboarding.spec.ts`            |
| WCAG 2.2 AA scan of every built flow            | `a11y.spec.ts`                  |
| Failure triage                                  | none — screen not built (below) |
| Evidence viewer (incl. expired-URL refresh)     | none — screen not built (below) |

## Open by design

- **Failure triage.** `apps/web/src/app/features/failures/failures.routes.ts:14` mounts only
  `RouteStubPage`, a placeholder card (`apps/web/src/app/shell/stub/route-stub.page.ts:34-58`);
  the Failure Inbox entry (`apps/web/src/app/shell/nav/nav-registry.ts:175-185`) has no triage
  control to drive, so its spec lands with the screen.
- **Evidence viewer.** No artifact download-url or expired-URL refresh UI exists. The run-detail
  evidence panel renders missing reasons, badges and revision/evaluation ids only
  (`apps/web/src/app/features/runs/detail/evidence/evidence-panel.component.html:109-143`); its
  only link handling qualifies fragment anchors (`evidence-panel.component.ts:160-170`). The
  attempts table shows an artifact count, never a link
  (`apps/web/src/app/features/runs/detail/attempts/attempts-table.component.html:155-164`), and
  `download-url` occurs only as an API path template
  (`apps/web/src/app/core/telemetry/api-path-template.ts:54`).

## Running and sharding (from `apps/web`)

```sh
EVIDGATE_E2E_CORE_SHARD=1 npx playwright test -c playwright.core.config.ts --shard=1/2
EVIDGATE_E2E_CORE_SHARD=2 npx playwright test -c playwright.core.config.ts --shard=2/2
```

The env var keeps each shard's JUnit file apart (`test-results/e2e-core-junit-<shard>.xml`); `setup`
runs first in every shard and stores the seeded tenant's auth state, so specs start signed in. The
web server builds the fixtures bundle itself (`npm run build:core:e2e`); specs run in `en` and `vi`.

## Gate and CI

- `scripts/verify.sh --stage e2e-a11y` runs `e2e/core/a11y.spec.ts` (about 35 s on the fixtures build); the
  Vitest axe specs (`*.a11y.spec.ts`) already run inside the `vitest` stage's `ng test`. Both stages
  run in the `no-dotnet` and `full` tiers.
- The CI `e2e` job is blocking: one `npx playwright install --with-deps chrome chromium` step, that
  stage, then `npm run e2e:core` with `EVIDGATE_E2E_CORE_SKIP_A11Y=1` (the axe spec already ran in
  the stage, so it does not run twice; locally the variable is unset and `npm run e2e:core` runs
  every spec), then the production smoke; each step's report, traces and JUnit are uploaded right
  after it (`playwright-a11y`, `playwright-core`, `playwright-smoke`, 7 days, never the stored auth
  state) because every Playwright run empties `test-results`.
- Retries: none, except for a test whose title carries `@infra` (browser or runner flake), which the
  `core-infra` project (core suite) and the `chromium-infra` project (production smoke, CI only) retry twice;
  the tag regex lives in `e2e/infra-tag.ts`. A product assertion is never tagged.
- Ports: every Playwright webServer (`playwright.config.ts`, `playwright.core.config.ts`,
  `playwright.fixtures.config.ts`; the dev-login config reuses the production one) takes a random
  port in 20000-29999 (`e2e/infra-tag.ts`), below every OS ephemeral range (Linux 32768-60999,
  macOS/Windows 49152-65535), so an automatically allocated Docker or SSH forward never lands on
  it (an explicitly published port still can: the server then exits with `EADDRINUSE`). Pin one
  with `EVIDGATE_WEB_E2E_PORT` or `EVIDGATE_WEB_FIXTURES_E2E_PORT`.
