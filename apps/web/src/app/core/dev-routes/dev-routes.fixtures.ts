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
//
// Suite composition is the same shape: `suites` is a phase-G4 entry with a real product child
// (`revisions/:revisionId`), so `/dev/suite-composition/orgs/:orgId/projects/:projectId/suites/
// revisions/:revisionId` mounts it under the identical guard chain with only the phase gate omitted.

import type { Type } from '@angular/core';
import type { Routes } from '@angular/router';

import { authGuard, orgScopeGuard, permissionGuard, projectScopeGuard } from '../guards';
import { ShellComponent } from '../../shell/layout/shell.component';
import { setupScriptsReviewRoutes } from './dev-routes.setup-scripts.fixtures';

/**
 * Review-only v3 screens fed by static view models, no HTTP: mounted under the project scope with
 * the permission the owning nav entry uses. Absent from production like every `dev/*` address.
 */
function reviewScreen(
  path: string,
  permission: string,
  load: () => Promise<Type<unknown>>,
): Routes[number] {
  return {
    path,
    component: ShellComponent,
    children: [
      {
        path: 'orgs/:orgId',
        canMatch: [authGuard, orgScopeGuard],
        children: [
          {
            path: 'projects/:projectId',
            canMatch: [projectScopeGuard, permissionGuard(permission)],
            data: { pageHeadingLevel: 1 },
            loadComponent: load,
          },
        ],
      },
    ],
  };
}

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
      path: 'dev/settings-api-tokens',
      component: ShellComponent,
      children: [
        {
          path: 'orgs/:orgId',
          canMatch: [
            authGuard,
            orgScopeGuard,
            permissionGuard('organization:read'),
            permissionGuard('api_token:read'),
          ],
          loadComponent: () =>
            // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only replacement mounts the real G4 page without changing its product phase.
            import('../../features/settings/api-tokens/api-tokens.page').then(
              (module) => module.ApiTokensPage,
            ),
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
      path: 'dev/settings-security-events',
      component: ShellComponent,
      children: [
        {
          path: 'orgs/:orgId',
          canMatch: [
            authGuard,
            orgScopeGuard,
            permissionGuard('organization:read'),
            permissionGuard('security_event:read'),
          ],
          data: { pageHeadingLevel: 1 },
          loadComponent: () =>
            // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only replacement mounts the real G2 page without changing its product phase.
            import('../../features/settings/security/security-events.page').then(
              (module) => module.SecurityEventsPage,
            ),
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
      path: 'dev/catalog-list',
      component: ShellComponent,
      children: [
        {
          path: 'orgs/:orgId',
          canMatch: [authGuard, orgScopeGuard],
          children: [
            {
              path: 'projects/:projectId',
              canMatch: [projectScopeGuard, permissionGuard('case:read')],
              loadComponent: () =>
                // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only replacement mounts the real G2 catalog under the product scope and permission guards.
                import('../../features/catalog/case-list.page').then((m) => m.CaseListPage),
            },
          ],
        },
      ],
    },
    {
      path: 'dev/authoring-tree',
      component: ShellComponent,
      children: [
        {
          path: 'orgs/:orgId',
          canMatch: [authGuard, orgScopeGuard],
          children: [
            {
              path: 'projects/:projectId',
              canMatch: [projectScopeGuard, permissionGuard('authoring:read')],
              data: { pageHeadingLevel: 1 },
              loadComponent: () =>
                // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only replacement mounts the product page under its scope and permission guards.
                import('../../features/authoring/authoring.page').then((m) => m.AuthoringPage),
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
    {
      // `suites` is a phase-G4 entry, so its product deep link does not match in this build. This
      // review address mounts the real composition screen under the same project scope and
      // `suite:read` grant, and only omits the phase gate.
      path: 'dev/suite-composition',
      component: ShellComponent,
      children: [
        {
          path: 'orgs/:orgId',
          canMatch: [authGuard, orgScopeGuard],
          children: [
            {
              path: 'projects/:projectId',
              canMatch: [projectScopeGuard, permissionGuard('suite:read')],
              children: [
                {
                  path: 'suites',
                  children: [
                    {
                      path: 'revisions/:revisionId',
                      loadComponent: () =>
                        // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph; mounts the real composition screen without changing its product phase.
                        import('../../features/suites/composition/suite-composition.component').then(
                          (m) => m.SuiteCompositionComponent,
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
    reviewScreen('dev/release-decision', 'run:read', () =>
      // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph.
      import('../../features/releases/decision/decision-review.page').then(
        (m) => m.ReleaseDecisionReviewPage,
      ),
    ),
    reviewScreen('dev/studio-gallery', 'run:read', () =>
      // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph.
      import('../../features/studio/gallery/studio-gallery-review.page').then(
        (m) => m.StudioGalleryReviewPage,
      ),
    ),
    reviewScreen('dev/studio-draft-editor', 'run:read', () =>
      // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph.
      import('../../features/studio/draft-editor/studio-draft-editor-review.page').then(
        (m) => m.StudioDraftEditorReviewPage,
      ),
    ),
    reviewScreen('dev/studio-review-merge', 'run:read', () =>
      // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph.
      import('../../features/studio/review-merge/studio-review-merge-review.page').then(
        (m) => m.StudioReviewMergeReviewPage,
      ),
    ),
    reviewScreen('dev/release-view', 'run:read', () =>
      // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph.
      import('../../features/releases/release-view/release-view-review.page').then(
        (m) => m.ReleaseViewReviewPage,
      ),
    ),
    reviewScreen('dev/quality-overview', 'run:read', () =>
      // eslint-disable-next-line evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph.
      import('../../features/quality/overview/quality-overview-review.page').then(
        (m) => m.QualityOverviewReviewPage,
      ),
    ),
    /* eslint-disable evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph; fixture-fed screens with no product route. */
    reviewScreen('dev/failure-inbox', 'healing:triage', () =>
      import('../../features/failures/inbox/failure-inbox.review.page').then(
        (m) => m.FailureInboxReviewPage,
      ),
    ),
    reviewScreen('dev/failure-detail', 'healing:triage', () =>
      import('../../features/failures/detail/failure-detail.review.page').then(
        (m) => m.FailureDetailReviewPage,
      ),
    ),
    reviewScreen('dev/investigation-history', 'healing:triage', () =>
      import('../../features/failures/history/investigation-history.review.page').then(
        (m) => m.InvestigationHistoryReviewPage,
      ),
    ),
    reviewScreen('dev/artifact-viewer', 'healing:triage', () =>
      import('../../features/failures/artifact/artifact-viewer.review.page').then(
        (m) => m.ArtifactViewerReviewPage,
      ),
    ),
    reviewScreen('dev/healing-queue', 'healing:triage', () =>
      import('../../features/repairs/queue/healing-queue.review.page').then(
        (m) => m.HealingQueueReviewPage,
      ),
    ),
    reviewScreen('dev/shadow-detail', 'healing:triage', () =>
      import('../../features/repairs/shadow/shadow-detail.review.page').then(
        (m) => m.ShadowDetailReviewPage,
      ),
    ),
    reviewScreen('dev/healing-approval', 'healing:triage', () =>
      import('../../features/repairs/approval/healing-approval.review.page').then(
        (m) => m.HealingApprovalReviewPage,
      ),
    ),
    reviewScreen('dev/knowledge-lineage', 'healing:triage', () =>
      import('../../features/knowledge/lineage/knowledge-lineage.review.page').then(
        (m) => m.KnowledgeLineageReviewPage,
      ),
    ),
    /* eslint-enable evidgate/no-feature-cross-import */
    ...setupScriptsReviewRoutes(),
  ];
}
