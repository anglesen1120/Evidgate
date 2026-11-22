import type { Routes } from '@angular/router';

export const webhooksReviewRoutes: Routes = [
  {
    path: '',
    data: { pageHeadingLevel: 1 },
    loadComponent: () => import('./webhooks-review.page').then((m) => m.WebhooksReviewPage),
  },
];
