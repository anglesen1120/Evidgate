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
