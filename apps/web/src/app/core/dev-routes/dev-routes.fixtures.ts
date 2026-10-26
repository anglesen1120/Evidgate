// 09.1.1-T4.1 (plan 09.1.1 §10 T4; hand-over from 09.1.1-T7, S64): the review-only routes.
//
// `angular.json`'s `development` and `fixtures` configurations replace `dev-routes.ts` with this
// file, so `ng serve`, `ng serve --configuration fixtures`, `ng test` and `ng extract-i18n` see
// these routes and the production build does not. The gallery is the one screen that is not a
// product screen: every shared state component (`shared/ui`) with all of its states, in en and vi,
// light and dark, at 360px and up.
//
// Dev-only, like `core/dev-fixtures`: never import this module from application code — only the
// `fileReplacements` entry may name it.
//
// 09.7.1-T3 / AC3 browser review: Secrets remains a phase-G2 product tab in the G1 build.
// `/dev/settings-secrets/orgs/:orgId` mounts its real page inside the normal app shell, with the
// same authenticated org/project session and organization/read + ingest-token/read permission
// gates. Only the phase gate is omitted on this review address; no product route or tab changes.
// Page-local SecretsStore/RevealSlot providers are the real ones. This file-replacement seam is
// absent from production, so the review address can never expose a phase-later product route there.
//
// 09.7.1-T4 / AC1-AC5 browser review: Policy is the same shape. It is a phase-G4 product tab in the
// G1 build, so `/dev/settings-policy/orgs/:orgId` mounts its real page inside the normal app shell,
// under the same authenticated org/project session and the organization/read + policy/read
// permission gates (the exact pair the product tab is gated on: `settings-tabs.ts` is unchanged).
// Only the phase gate is omitted on this review address; no product route or tab changes. The
// page-local PolicyStore provider is the real one, and the publish control stays grant-derived —
// `session.has('policy:write')`, absent from the DOM without it. The same seam rule applies: this
// file is replaced in the `development`/`fixtures` configurations only, so the review address can
// never expose a phase-later product route in production.
//
// 09.3.1-T1 / plan 09.3.1 §10 T1 browser review: `runs` is a phase-G2 entry, so in the G1 build
// `/:locale/orgs/:orgId/projects/:projectId/runs` does not match and the wildcard renders
// not-found. `/dev/run-list/orgs/:orgId/projects/:projectId` mounts the real run list inside the
// normal app shell, under the same guard chain `app.routes.ts` builds for the product route —
// `authGuard`, `orgScopeGuard`, then `projectScopeGuard` and `permissionGuard('run:read')` on the
// nested project route (nested on purpose: `projectScopeGuard` reads its id from `segments[1]`).
// Only the phase gate is omitted on this review address; no product route changes. The page reads
// `projectId` from the ancestor `pathFromRoot` param maps and navigates its filter query relative
// to its own route, so it needs no change on this address. The same seam rule applies: this file
// is replaced in the `development`/`fixtures` configurations only, so the review address can never
// expose a phase-later product route in production.

import type { Routes } from '@angular/router';

import { authGuard, orgScopeGuard, permissionGuard, projectScopeGuard } from '../guards';
import { ShellComponent } from '../../shell/layout/shell.component';

/** The review-only routes, mounted first so a product path can never shadow one. */
export function devRoutes(): Routes {
  return [
    {
      path: 'dev/ui-gallery',
      loadComponent: () => import('../../shared/ui/gallery').then((m) => m.UiGalleryComponent),
    },
    {
      path: 'dev/settings-secrets',
      component: ShellComponent,
      children: [
        {
          path: 'orgs/:orgId',
          canMatch: [
            authGuard,
            orgScopeGuard,
            permissionGuard('organization:read'),
            permissionGuard('ingest_token:read'),
          ],
          loadComponent: () =>
            // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph; mounts the real G2 page without changing its product phase.
            import('../../features/settings/secrets/secrets.page').then((m) => m.SecretsPage),
        },
      ],
    },
    {
      path: 'dev/settings-policy',
      component: ShellComponent,
      children: [
        {
          path: 'orgs/:orgId',
          canMatch: [
            authGuard,
            orgScopeGuard,
            permissionGuard('organization:read'),
            permissionGuard('policy:read'),
          ],
          loadComponent: () =>
            // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph; mounts the real G4 page without changing its product phase.
            import('../../features/settings/policy/policy.page').then((m) => m.PolicyPage),
        },
      ],
    },
    {
      path: 'dev/run-list',
      component: ShellComponent,
      children: [
        {
          path: 'orgs/:orgId',
          canMatch: [authGuard, orgScopeGuard],
          children: [
            {
              path: 'projects/:projectId',
              canMatch: [projectScopeGuard, permissionGuard('run:read')],
              // The product table carries this on the same leaf (`runs.routes.ts`); it is route
              // data, not a gate, so the review address keeps it too.
              data: { pageHeadingLevel: 1 },
              children: [
                {
                  path: '',
                  pathMatch: 'full',
                  loadComponent: () =>
                    // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement; real G2 list, same product guards.
                    import('../../features/runs/list/run-list.page').then((m) => m.RunListPage),
                },
                {
                  path: ':runId',
                  loadComponent: () =>
                    // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only detail child makes relative product row links usable in G1 review.
                    import('../../features/runs/detail/run-detail.page').then(
                      (m) => m.RunDetailPage,
                    ),
                },
              ],
            },
          ],
        },
      ],
    },
    {
      path: 'dev/run-detail',
      component: ShellComponent,
      children: [
        {
          path: 'orgs/:orgId',
          canMatch: [authGuard, orgScopeGuard],
          children: [
            {
              path: 'projects/:projectId',
              canMatch: [projectScopeGuard, permissionGuard('run:read')],
              children: [
                {
                  path: 'runs',
                  data: { pageHeadingLevel: 1 },
                  children: [
                    {
                      path: '',
                      pathMatch: 'full',
                      loadComponent: () =>
                        // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement; back-to-runs target under identical product guards.
                        import('../../features/runs/list/run-list.page').then((m) => m.RunListPage),
                    },
                    {
                      path: ':runId',
                      loadComponent: () =>
                        // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement; real G2 detail, no product phase change.
                        import('../../features/runs/detail/run-detail.page').then(
                          (m) => m.RunDetailPage,
                        ),
                    },
                  ],
                },
              ],
            },
          ],
        },
      ],
    },
  ];
}
