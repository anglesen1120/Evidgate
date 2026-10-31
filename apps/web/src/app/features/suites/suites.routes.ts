// The shell mounts the suites area with its project scope and entry guards. The index remains
// the existing placeholder while pinned revision deep links load their read-only composition.

import type { Routes } from '@angular/router';

import { RouteStubPage } from '../../shell/stub/route-stub.page';

export const routes: Routes = [
  {
    path: 'revisions/:revisionId',
    loadComponent: () =>
      import('./composition/suite-composition.component').then(
        (module) => module.SuiteCompositionComponent,
      ),
  },
  { path: '', pathMatch: 'full', component: RouteStubPage },
];
