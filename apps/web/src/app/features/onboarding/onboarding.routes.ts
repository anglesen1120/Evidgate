import type { Routes } from '@angular/router';

import { OnboardingStore } from './onboarding.store';

// Entry permissions and org scope are enforced by the registry-generated parent route.
export const routes: Routes = [
  {
    path: '',
    providers: [OnboardingStore],
    children: [
      {
        path: '',
        pathMatch: 'full',
        loadComponent: () =>
          import('./shell/onboarding-shell.component').then((m) => m.OnboardingShellComponent),
      },
      {
        path: ':projectId',
        loadComponent: () =>
          import('./shell/onboarding-shell.component').then((m) => m.OnboardingShellComponent),
      },
    ],
  },
];
