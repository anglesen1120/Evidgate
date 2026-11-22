import type { Routes } from '@angular/router';

export const agentPoolsReviewRoutes: Routes = [
  {
    path: '',
    data: { pageHeadingLevel: 1 },
    loadComponent: () => import('./agent-pools-review.page').then((m) => m.AgentPoolsReviewPage),
  },
];
