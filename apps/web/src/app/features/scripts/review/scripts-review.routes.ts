import type { Routes } from '@angular/router';

export const generatedScriptsReviewRoutes: Routes = [
  {
    path: '',
    data: { pageHeadingLevel: 1 },
    loadComponent: () => import('./generated-scripts.page').then((m) => m.GeneratedScriptsPage),
  },
];
export const uiChangeProposalReviewRoutes: Routes = [
  {
    path: '',
    data: { pageHeadingLevel: 1 },
    loadComponent: () => import('./ui-change-proposal.page').then((m) => m.UiChangeProposalPage),
  },
];
