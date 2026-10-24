// 09.1.1-T4.1 (plan 09.1.1 §10 T4; hand-over from 09.1.1-T7, S64): the seam the review-only
// routes hang on, and the only route-table module the application imports unconditionally.
//
// This file is the PRODUCTION stand-in: it adds no route, so a production bundle contains no
// review screen. The `development` and `fixtures` build configurations of
// `apps/web/angular.json` replace it (`fileReplacements`) with `dev-routes.fixtures.ts`, which is
// where the `shared/ui` gallery is mounted. Keeping the seam here is what makes `app.routes.ts`
// configuration-agnostic: it always spreads `devRoutes()` first, and which screens exist is a
// build decision, not a runtime flag a shipped page could flip.
//
// Why the gallery route matters beyond a review screen: importing a shared component from the
// route table is also what makes `ng extract-i18n` (which builds the `development`
// configuration) see the `shared.*` message ids, so they stay in the catalogue the `vi` bundle is
// keyed on. Production has no route to the gallery, and the ids it no longer uses stay in the
// catalogue unused — never the other way round.

import type { Routes } from '@angular/router';

/** Production: no review-only route. */
export function devRoutes(): Routes {
  return [];
}
