// Review-only mounts for the setup, settings-extended and scripts screens. Named only by
// `dev-routes.fixtures.ts`, which `angular.json` swaps in for the `development` and `fixtures`
// configurations, so the production bundle never contains these screens. Each address sits in the
// normal shell under the org and project scope guards; no phase gate and no product route.

import type { LoadChildrenCallback, Route, Routes } from '@angular/router';

import { authGuard, orgScopeGuard, permissionGuard, projectScopeGuard } from '../guards';
import { ShellComponent } from '../../shell/layout/shell.component';

function reviewMount(path: string, permission: string, loadChildren: LoadChildrenCallback): Route {
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
            loadChildren,
          },
        ],
      },
    ],
  };
}

/** The review-only routes of screens 26, 34, 35, 36, 38, 46 and 47. */
export function setupScriptsReviewRoutes(): Routes {
  /* eslint-disable evidgate/no-feature-cross-import -- review-only file replacement, absent from the production core graph. */
  return [
    reviewMount('dev/mapping-resolution', 'case:read', () =>
      import('../../features/mapping/review/mapping-review.routes').then(
        (m) => m.mappingReviewRoutes,
      ),
    ),
    reviewMount('dev/agent-pools', 'agent_pool:read', () =>
      import('../../features/settings/review-agent-pools/agent-pools-review.routes').then(
        (m) => m.agentPoolsReviewRoutes,
      ),
    ),
    reviewMount('dev/webhooks', 'webhook:read', () =>
      import('../../features/settings/review-webhooks/webhooks-review.routes').then(
        (m) => m.webhooksReviewRoutes,
      ),
    ),
    reviewMount('dev/support-access', 'support:read', () =>
      import('../../features/settings/review-support-access/support-access-review.routes').then(
        (m) => m.supportAccessReviewRoutes,
      ),
    ),
    reviewMount('dev/deploy-targets', 'environment:write', () =>
      import('../../features/deploy/review/deploy-targets-review.routes').then(
        (m) => m.deployTargetsReviewRoutes,
      ),
    ),
    reviewMount('dev/generated-scripts', 'case:read', () =>
      import('../../features/scripts/review/scripts-review.routes').then(
        (m) => m.generatedScriptsReviewRoutes,
      ),
    ),
    reviewMount('dev/ui-change-proposal', 'authoring:read', () =>
      import('../../features/scripts/review/scripts-review.routes').then(
        (m) => m.uiChangeProposalReviewRoutes,
      ),
    ),
  ];
  /* eslint-enable evidgate/no-feature-cross-import */
}
