// 09.1.1-T4.1 (plan 09.1.1 §10 T4, 00 §5.6 W1): the lazy route table of the `runs` area.
// `app.routes.ts` mounts it at the segment of every `NAV_ENTRIES` row whose `area` is
// `runs` — one lazy chunk per area — and the entry id reaches the page through that route's
// `data`, so this module repeats no segment, permission or label.
//
// W1 holds here: nothing outside `features/runs` imports this file; the entry-level guards
// (`phaseGuard`, `permissionGuard`) live on the route that loads it.

import type { Routes } from '@angular/router';

/** The run list owns the page heading; project context is inherited from the shell. */
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () => import('./list/run-list.page').then((module) => module.RunListPage),
    data: { pageHeadingLevel: 1 },
  },
  {
    path: ':runId',
    loadComponent: () => import('./detail/run-detail.page').then((module) => module.RunDetailPage),
    data: { pageHeadingLevel: 1 },
  },
];
