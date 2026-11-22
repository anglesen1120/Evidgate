import type { Routes } from '@angular/router';

export const supportAccessReviewRoutes: Routes = [
  {
    path: '',
    data: { pageHeadingLevel: 1 },
    loadComponent: () =>
      import('./support-access-review.page').then((m) => m.SupportAccessReviewPage),
  },
];
