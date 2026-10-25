import type { Routes } from '@angular/router';

import { OnboardingStore } from './onboarding.store';

// Entry permissions and org scope are enforced by the registry-generated parent route.
// The store is provided here, on the parent, so both children share one instance: the URL change
// a create causes re-activates the shell on `:projectId`, and the scope `projectCreated` published
// is still there, so that second activation renders it instead of reading it again (S71 F1).
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
