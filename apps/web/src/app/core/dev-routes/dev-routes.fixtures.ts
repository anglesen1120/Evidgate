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

import type { Routes } from '@angular/router';

/** The review-only routes, mounted first so a product path can never shadow one. */
export function devRoutes(): Routes {
  return [
    {
      path: 'dev/ui-gallery',
      loadComponent: () => import('../../shared/ui/gallery').then((m) => m.UiGalleryComponent),
    },
  ];
}
