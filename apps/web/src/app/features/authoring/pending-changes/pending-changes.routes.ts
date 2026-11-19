import type { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () => import('./pending-changes.page').then((m) => m.PendingChangesPage),
    data: { pageHeadingLevel: 1 },
  },
];
