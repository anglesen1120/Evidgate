import type { Routes } from '@angular/router';

import { OnboardingStore } from './onboarding.store';

// Entry permissions and org scope are enforced by the registry-generated parent route.
// `providers` here make the store a route-level singleton: one instance for the app's lifetime,
// NOT one per activation. So its `ready` state can belong to an earlier visit, and nothing in it
// may be reused across activations except the one-shot create marker `consumeCreated` takes —
// that is what spares the activation a create's URL change causes a second read (S71 F1/N1).
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
