// 09.1.1-T4.1 (plan 09.1.1 §10 T4, §4 "routes delivered"; 00 §5.6 W1/W7, 00 §9.2; D-S68-Q1..Q4): the
// route table of the app. It is generated from `NAV_ENTRIES`, so a sidebar entry and its route can
// never disagree: the segment, the phase and the permission all come from the registry row, and
// the row's `area` picks the lazy chunk. Nothing here repeats a segment or a permission string.
//
// Shape (Q4 — the URL mirrors the API paths):
//
//   /login                                  → sign-in placeholder, outside the shell (real login is T5)
//   /orgs/:orgId                            → shell; org scope guards
//   /orgs/:orgId/<segment>                  → one org-scope entry
//   /orgs/:orgId/projects/:projectId        → project scope guards; '' redirects to `overview`
//   /orgs/:orgId/projects/:projectId/<segment> → one project-scope entry
//   /forbidden /welcome /unavailable → shell-local pattern pages (T8 owns the real patterns)
//   **                                      → not found
//
// Guard refusal semantics — the client gate is a convenience, the API's 403/404 stay authoritative:
//   - a foreign or unknown org/project id makes the scope guard return `false`, so the route is not
//     matched and the wildcard renders the same not-found page an unknown path renders. It is never
//     a 403: a 403 would confirm that the id exists in some other tenant (00 §9.2).
//   - a missing permission on a route that does exist redirects to `/forbidden` (UrlTree).
//   - an anonymous session redirects to `/login?returnUrl=<url>`.
//   - a phase-later entry (Q: G2+ in the G1 build) does not match at all, so it is a 404 like any
//     other unknown path — hidden, never teased.
//
// Lazy boundaries: every entry loads exactly one chunk. An area that serves several entries is
// loaded through one module per area (the same chunk for all of its entries), and the page inside
// it reads its entry from the owning route's `data.entryId`. The one entry with a real screen
// (`overview`) loads that single component instead.

import type { Type } from '@angular/core';
import type { Route, Routes } from '@angular/router';

import { devRoutes } from './core/dev-routes/dev-routes';
import {
  authGuard,
  landingRedirectGuard,
  orgLandingGuard,
  orgScopeGuard,
  permissionGuard,
  phaseGuard,
  projectScopeGuard,
} from './core/guards';
import { ShellComponent } from './shell/layout/shell.component';
import type { NavEntry } from './shell/nav/nav.models';
import { NAV_ENTRIES } from './shell/nav/nav-registry';
import { ForbiddenPage } from './shell/pages/forbidden.page';
import { NoOrganizationPage } from './shell/pages/no-organization.page';
import { NotFoundPage } from './shell/pages/not-found.page';
import { ServiceUnavailablePage } from './shell/pages/service-unavailable.page';
import { SignInRequiredPage } from './shell/pages/sign-in-required.page';

/**
 * The lazy route table of each `features/<area>` folder. A registry row whose `area` is not named
 * here is a build error at module load rather than a dead navigation entry — the map is the only
 * place an area folder is named, and each loader is a static `import()` so the bundler can split it.
 */
const AREA_ROUTES: Readonly<Record<string, () => Promise<Routes>>> = {
  authoring: () => import('./features/authoring/authoring.routes').then((m) => m.routes),
  availability: () => import('./features/availability/availability.routes').then((m) => m.routes),
  catalog: () => import('./features/catalog/catalog.routes').then((m) => m.routes),
  deploy: () => import('./features/deploy/deploy.routes').then((m) => m.routes),
  failures: () => import('./features/failures/failures.routes').then((m) => m.routes),
  knowledge: () => import('./features/knowledge/knowledge.routes').then((m) => m.routes),
  mapping: () => import('./features/mapping/mapping.routes').then((m) => m.routes),
  onboarding: () => import('./features/onboarding/onboarding.routes').then((m) => m.routes),
  policies: () => import('./features/policies/policies.routes').then((m) => m.routes),
  project: () => import('./features/project/project.routes').then((m) => m.routes),
  quality: () => import('./features/quality/quality.routes').then((m) => m.routes),
  releases: () => import('./features/releases/releases.routes').then((m) => m.routes),
  repairs: () => import('./features/repairs/repairs.routes').then((m) => m.routes),
  runs: () => import('./features/runs/runs.routes').then((m) => m.routes),
  scripts: () => import('./features/scripts/scripts.routes').then((m) => m.routes),
  settings: () => import('./features/settings/settings.routes').then((m) => m.routes),
  studio: () => import('./features/studio/studio.routes').then((m) => m.routes),
  suites: () => import('./features/suites/suites.routes').then((m) => m.routes),
};

/**
 * The entries that already have a real screen, so they load that component instead of the area's
 * placeholder table. Every other entry is a `RouteStubPage` until its feature story lands.
 */
const ENTRY_PAGES: Readonly<Record<string, () => Promise<Type<unknown>>>> = {
  overview: () => import('./features/project/overview.page').then((m) => m.ProjectOverviewPage),
};

/** The lazy loader of one area, or a load-time failure naming the missing folder. */
function areaRoutesOf(entry: NavEntry): () => Promise<Routes> {
  const loadArea = AREA_ROUTES[entry.area];
  if (loadArea === undefined) {
    throw new Error(
      `NAV_ENTRIES['${entry.id}'] names area '${entry.area}', but app.routes.ts has no ` +
        `features/${entry.area}/${entry.area}.routes.ts loader for it`,
    );
  }
  return loadArea;
}

/**
 * One navigation entry as a route: the registry's segment, the phase gate and then the permission
 * gate (both `CanMatchFn`, so a refusal never instantiates the page), and the entry id the page
 * reads its label from.
 */
function entryRoute(entry: NavEntry): Route {
  const page = ENTRY_PAGES[entry.id];
  const common = {
    path: entry.segment,
    canMatch: [phaseGuard(entry.phase), permissionGuard(entry.permission)],
    data: { entryId: entry.id },
  };
  return page === undefined
    ? { ...common, loadChildren: areaRoutesOf(entry) }
    : { ...common, loadComponent: page };
}

/**
 * The routes of every org-scope entry (`/orgs/:orgId/<segment>`). Exported so the router spec can
 * mount the real table under a stub shell: the shell host and the pattern pages are the only
 * components a jsdom spec replaces.
 */
export const ORG_SCOPE_ENTRY_ROUTES: readonly Route[] = NAV_ENTRIES.filter(
  (entry) => entry.scope === 'org',
).map(entryRoute);

/** The same for the project-scope entries (`/orgs/:orgId/projects/:projectId/<segment>`). */
export const PROJECT_SCOPE_ENTRY_ROUTES: readonly Route[] = NAV_ENTRIES.filter(
  (entry) => entry.scope === 'project',
).map(entryRoute);

export const routes: Routes = [
  // Review-only routes first, so no product path can shadow them. Production spreads nothing:
  // `dev-routes.ts` is the empty stand-in (see its module comment).
  ...devRoutes(),

  // Outside the shell: a signed-out visitor must not see a navigation they cannot use.
  { path: 'login', component: SignInRequiredPage },

  {
    path: '',
    component: ShellComponent,
    children: [
      // `/` decides where a session lands and mounts nothing itself.
      { path: '', pathMatch: 'full', canActivate: [landingRedirectGuard], children: [] },

      {
        path: 'orgs/:orgId',
        canMatch: [authGuard, orgScopeGuard],
        children: [
          // `orgLandingGuard` answers with a redirect, so this route never renders: `children: []`
          // is what makes it a valid component-less match in v22 (a route with neither a component
          // nor children fails NG04014).
          { path: '', pathMatch: 'full', canActivate: [orgLandingGuard], children: [] },

          {
            path: 'projects/:projectId',
            canMatch: [projectScopeGuard],
            children: [
              { path: '', pathMatch: 'full', redirectTo: 'overview' },
              ...PROJECT_SCOPE_ENTRY_ROUTES,
            ],
          },

          ...ORG_SCOPE_ENTRY_ROUTES,
        ],
      },

      { path: 'forbidden', component: ForbiddenPage },
      { path: 'welcome', component: NoOrganizationPage },
      { path: 'unavailable', component: ServiceUnavailablePage },
      // The only wildcard: the shell route matches every URL, so an unknown path, a foreign org or
      // project id and a phase-hidden entry all land here, with the shell around them.
      { path: '**', component: NotFoundPage },
    ],
  },
];
