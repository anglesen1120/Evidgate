import type { Routes } from '@angular/router';

export const mappingReviewRoutes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    data: { pageHeadingLevel: 1 },
    loadComponent: () =>
      import('./mapping-resolution.page').then((module) => module.MappingResolutionPage),
  },
];
