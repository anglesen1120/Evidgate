import type { Routes } from '@angular/router';

export const deployTargetsReviewRoutes: Routes = [
  {
    path: '',
    data: { pageHeadingLevel: 1 },
    loadComponent: () =>
      import('./deploy-targets-review.page').then((m) => m.DeployTargetsReviewPage),
  },
];
