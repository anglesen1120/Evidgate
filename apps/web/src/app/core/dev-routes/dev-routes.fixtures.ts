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

import type { Routes } from '@angular/router';

import { authGuard, orgScopeGuard, permissionGuard } from '../guards';
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
  ];
}
