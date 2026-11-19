// Entry guards and navigation metadata stay on the shell route.

import type { Routes } from '@angular/router';

import { AuthoringPage } from './authoring.page';

export const routes: Routes = [
  { path: '', pathMatch: 'full', component: AuthoringPage, data: { pageHeadingLevel: 1 } },
  {
    path: 'import-export',
    loadComponent: () =>
      import('./import-export/import-export.page').then((m) => m.ImportExportPage),
    data: { pageHeadingLevel: 1 },
  },
];
