// 09.1.1-T5.1 (plan 09.1.1 §10 T5.1; D-S98-Q2/Q3): the two routes of the sign-in flow, OUTSIDE the
// shell — a caller without a session must not see a navigation they cannot use, and the shell's
// guards would bounce every one of these paths back here anyway.
//
//   /login           → the login page (lazy; `login/login.page.ts`, owned by the UI slice)
//   /auth/callback   → the post-login landing (this feature's page)
//
// Both are lazy `loadComponent` boundaries, so an anonymous visitor downloads the sign-in screen and
// nothing else. The table is spread into `app.routes.ts` before the shell route, so no product path
// can shadow `/login` or `/auth/callback` (there is no product route with those segments, and the
// wildcard lives inside the shell).
//
// `AuthFlow` is provided by the `/login` route, not by the root injector: it is per activation, and
// the guard below never injects it (it reads the route's own query), so a cached flow cannot answer
// for a later visit.
//
// `signedInRedirectGuard` is the "already signed in" answer the T4 placeholder never had: a session
// visiting `/login` (a bookmark, a back button after signing in) goes to the validated `returnUrl`, or
// to the workspace root whose landing guard resolves the first org and project.

import type { Routes } from '@angular/router';

import { AuthFlow, signedInRedirectGuard } from './auth-flow';

export const AUTH_ROUTES: Routes = [
  {
    path: 'login',
    canActivate: [signedInRedirectGuard],
    // 09.1.1-T5 review (major 1): one flow per activation of the login route. The flow reads the
    // activation's own query (`?reason=`, `?returnUrl=`), so a second visit — logout, expiry, a
    // bookmark — must not inherit the first one's notices.
    providers: [AuthFlow],
    loadComponent: () => import('./login/login.page').then((page) => page.LoginPage),
  },
  {
    path: 'auth/callback',
    loadComponent: () => import('./callback/callback.page').then((page) => page.CallbackPage),
  },
];
