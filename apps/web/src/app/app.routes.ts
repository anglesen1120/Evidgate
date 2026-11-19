// 09.1.1-T4.1 (plan 09.1.1 §10 T4, §4 "routes delivered"; 00 §5.6 W1/W7, 00 §9.2; D-S68-Q1..Q4): the
// route table of the app. It is generated from `NAV_ENTRIES`, so a sidebar entry and its route can
// never disagree: the segment, the phase and the permission all come from the registry row, and
// the row's `area` picks the lazy chunk. Nothing here repeats a segment or a permission string.
//
// Shape (Q4 — the URL mirrors the API paths):
//
//   /login, /auth/callback                  → sign-in flow, outside the shell (09.1.1-T5)
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
//
// An entry can also own a route table of its own instead of its area's (`ENTRY_ROUTES`): the
// `settings` entry mounts the Settings shell — a tab strip with one gated child route per tab —
// while the other ten `settings` destinations still render the area's placeholder table, so the
// override is per entry and never per area. Precedence in `entryRoute`: `ENTRY_PAGES` (a single
// screen), then `ENTRY_ROUTES` (a table), then the area's table.
//
// The entry's own guards are NOT changed by that override: `phaseGuard` and
// `permissionGuard(entry.permission)` run before the shell table exactly as they do for every other
// entry. Which code the `settings` sidebar row demands is the registry's decision (today
// `organization:read`), so a session that lacks it is refused on the entry with `/forbidden` before
// any tab route is attempted; the per-tab decision of plan 09.7.1 §5 starts from a caller who may
// open Settings at all. Replacing the entry gate with the tab gates would be a navigation-registry
// change (the sidebar shows the entry under the same code), not a change to this table.

import type { Type } from '@angular/core';
import type { Route, Routes } from '@angular/router';

import { devRoutes } from './core/dev-routes/dev-routes';
import { AUTH_ROUTES } from './features/auth/auth.routes';
import {
  authGuard,
  landingRedirectGuard,
  orgLandingGuard,
  orgScopeGuard,
  permissionGuard,
  phaseGuard,
  projectScopeGuard,
} from './core/guards';
import type { NavEntry } from './shell/nav/nav.models';
import { NAV_ENTRIES } from './shell/nav/nav-registry';

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

/**
 * The entries that own a route table instead of the one their `area` serves (09.7.1-T1.1): the
 * `settings` entry mounts the Settings shell, whose children are one gated route per tab, while its
 * ten sibling `settings` entries keep the area's placeholder table. Keyed by entry id, so the
 * override can never spread to an entry that did not ask for it.
 */
const ENTRY_ROUTES: Readonly<Record<string, () => Promise<Routes>>> = {
  authoring: () => import('./features/authoring/authoring-tree.routes').then((m) => m.routes),
  settings: () => import('./features/settings/settings-shell.routes').then((m) => m.routes),
  members: () => import('./features/settings/members/members.routes').then((m) => m.routes),
  audit: () => import('./features/settings/audit/audit.routes').then((m) => m.routes),
  'ai-settings': () =>
    import('./features/authoring/ai-settings/ai-settings.routes').then((m) => m.routes),
  'pending-changes': () =>
    import('./features/authoring/pending-changes/pending-changes.routes').then((m) => m.routes),
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
 * reads its label from. The page comes from `ENTRY_PAGES` when the entry has a real screen, else
 * from `ENTRY_ROUTES` when it owns a table, else from its area's placeholder table.
 */
function entryRoute(entry: NavEntry): Route {
  const page = ENTRY_PAGES[entry.id];
  const table = ENTRY_ROUTES[entry.id];
  const common = {
    path: entry.segment,
    canMatch: [phaseGuard(entry), permissionGuard(entry.permission)],
    data: { entryId: entry.id },
  };
  if (page !== undefined) {
    return { ...common, loadComponent: page };
  }
  return { ...common, loadChildren: table ?? areaRoutesOf(entry) };
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
  // 09.1.1-T5.1 replaced the T4 placeholder with the real flow: `/login` (provider buttons, the
  // validated `returnUrl`, the reason notice) and `/auth/callback` (the post-login landing).
  ...AUTH_ROUTES,

  {
    path: '',
    loadComponent: () => import('./shell/layout/shell.component').then((m) => m.ShellComponent),
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
              {
                path: 'authoring/cases/:caseId',
                loadChildren: () =>
                  import('./features/authoring/editor/editor.routes').then((m) => m.routes),
              },
              ...PROJECT_SCOPE_ENTRY_ROUTES,
            ],
          },

          ...ORG_SCOPE_ENTRY_ROUTES,
        ],
      },

      {
        path: 'forbidden',
        loadComponent: () => import('./shell/pages/forbidden.page').then((m) => m.ForbiddenPage),
      },
      {
        path: 'welcome',
        loadComponent: () =>
          import('./shell/pages/no-organization.page').then((m) => m.NoOrganizationPage),
      },
      {
        path: 'unavailable',
        loadComponent: () =>
          import('./shell/pages/service-unavailable.page').then((m) => m.ServiceUnavailablePage),
      },
      // The only wildcard: the shell route matches every URL, so an unknown path, a foreign org or
      // project id and a phase-hidden entry all land here, with the shell around them.
      {
        path: '**',
        loadComponent: () => import('./shell/pages/not-found.page').then((m) => m.NotFoundPage),
      },
    ],
  },
];
