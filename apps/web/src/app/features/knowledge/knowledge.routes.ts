// 09.1.1-T4.1 (plan 09.1.1 §10 T4, 00 §5.6 W1): the lazy route table of the `knowledge` area.
// `app.routes.ts` mounts it at the segment of every `NAV_ENTRIES` row whose `area` is
// `knowledge` — one lazy chunk per area — and the entry id reaches the page through that route's
// `data`, so this module repeats no segment, permission or label.
//
// W1 holds here: nothing outside `features/knowledge` imports this file; the entry-level guards
// (`phaseGuard`, `permissionGuard`) live on the route that loads it.

import type { Routes } from '@angular/router';

import { RouteStubPage } from '../../shell/stub/route-stub.page';

/** Everything mounted under a `knowledge` segment: the shell placeholder until its feature story. */
export const routes: Routes = [{ path: '', pathMatch: 'full', component: RouteStubPage }];
