// 09.1.1-T4.1 (plan 09.1.1 §2 AC4, §10 T4; 00 §5.6 W5, 00 §9.2; D-S68-Q1..Q4): the route table at
// router level, against the real guards, the real entry routes and the real `SessionStore` — only
// the two seams that talk to the server are faked (`IdentityDataService` and
// `EffectivePermissionsSource`), because the point of these cases is what the client decides from a
// server-declared set, not what the server answers.
//
// What is stubbed, and why: `app.routes.ts` is imported unchanged and walked, replacing only the
// *components* whose own specs cover their templates — the shell host, the pattern pages and the
// Settings shell's three pages (09.7.1-T1) — with marker components. Every route, guard,
// `data.entryId` and lazy boundary in the table under test is the production one; `RouteStubPage`
// and `ProjectOverviewPage` are rendered for real, and the Settings shell table is reached through
// its real `ENTRY_ROUTES` override.
//
// The five behaviours AC4 names:
//   - a route whose permission the server-declared set lacks is not matched → `/forbidden`;
//   - a foreign org id, a foreign project id and a made-up path render the SAME not-found page;
//   - a later-phase entry does not match in the G1 build → not found;
//   - an anonymous session is sent to `/login` with the original URL;
//   - an empty permission set sends every active entry to `/forbidden`;
// plus the two landing rules (first org/project, and the deep link a stub renders).

import {
  ChangeDetectionStrategy,
  Component,
  Injectable,
  provideZonelessChangeDetection,
  inject,
  type Type,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  Router,
  RouterOutlet,
  provideRouter,
  type Route,
  type Routes,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { OrganizationSummaryDto, PermissionView, ProjectDto } from '@evidgate/sdk';

import { routes } from './app.routes';
import { IdentityDataService } from './core/data/identity-data.service';
import { ApiRequestError, type ApiError } from './core/http/api-error';
import { EffectivePermissionsSource } from './core/session/effective-permissions.source';
import { SettingsNoTabsPage } from './features/settings/settings-no-tabs.page';
import { SettingsShellPage } from './features/settings/settings-shell.page';
import { SettingsTabPlaceholderPage } from './features/settings/settings-tab-placeholder.page';
import { ShellComponent } from './shell/layout/shell.component';
import { ACTIVE_PHASE } from './shell/nav/product-phase';
import type { NavEntry } from './shell/nav/nav.models';
import { NAV_ENTRIES } from './shell/nav/nav-registry';
import { ForbiddenPage } from './shell/pages/forbidden.page';
import { NoOrganizationPage } from './shell/pages/no-organization.page';
import { NotFoundPage } from './shell/pages/not-found.page';
import { ServiceUnavailablePage } from './shell/pages/service-unavailable.page';
import { SignInRequiredPage } from './shell/pages/sign-in-required.page';

/** Marker text each stub renders, so a case can prove which page the router mounted. */
const FORBIDDEN_MARKER = 'forbidden-stub';
const NOT_FOUND_MARKER = 'not-found-stub';

@Component({
  selector: 'app-spec-shell-host',
  imports: [RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<router-outlet />',
})
class ShellHostStub {}

@Component({
  selector: 'app-spec-forbidden',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker">{{ marker }}</p>',
})
class ForbiddenStub {
  readonly marker = FORBIDDEN_MARKER;
}

@Component({
  selector: 'app-spec-not-found',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker">{{ marker }}</p>',
})
class NotFoundStub {
  readonly marker = NOT_FOUND_MARKER;
}

@Component({
  selector: 'app-spec-welcome',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker">{{ marker }}</p>',
})
class WelcomeStub {
  readonly marker = 'welcome-stub';
}

@Component({
  selector: 'app-spec-unavailable',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker">{{ marker }}</p>',
})
class UnavailableStub {
  readonly marker = 'unavailable-stub';
}

@Component({
  selector: 'app-spec-login',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker">{{ marker }}</p>',
})
class LoginStub {
  readonly marker = 'login-stub';
}

/** The Settings shell without its strip: the tab routes render into this outlet. */
@Component({
  selector: 'app-spec-settings-shell',
  imports: [RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<router-outlet />',
})
class SettingsShellStub {}

/** A Settings tab body that prints the id its route carried in `data.settingsTabId`. */
@Component({
  selector: 'app-spec-settings-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker" [attr.data-settings-tab-body]="tabId">{{ tabId }}</p>',
})
class SettingsTabStub {
  private readonly route = inject(ActivatedRoute);

  readonly tabId = settingsTabIdOf(this.route);
}

/** The `settingsTabId` an activated route carried, or a marker naming the missing datum. */
function settingsTabIdOf(route: ActivatedRoute): string {
  const id: unknown = route.snapshot.data['settingsTabId'];
  return typeof id === 'string' ? id : 'missing-settings-tab-id';
}

/** The Settings no-tabs state. */
@Component({
  selector: 'app-spec-settings-no-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker">{{ marker }}</p>',
})
class SettingsNoTabsStub {
  protected readonly marker = 'no-tabs-stub';
}

/**
 * The production components this spec replaces with markers: the layout slice's shell and pattern
 * pages, plus the Settings shell's three pages (09.7.1-T1) whose templates their own specs cover.
 * Keyed by the production class, so a route the table stops mounting through one of them simply
 * renders the real thing instead.
 */
const COMPONENT_STUBS = new Map<unknown, Type<unknown>>([
  [ShellComponent, ShellHostStub],
  [ForbiddenPage, ForbiddenStub],
  [NotFoundPage, NotFoundStub],
  [NoOrganizationPage, WelcomeStub],
  [ServiceUnavailablePage, UnavailableStub],
  [SignInRequiredPage, LoginStub],
  [SettingsShellPage, SettingsShellStub],
  [SettingsTabPlaceholderPage, SettingsTabStub],
  [SettingsNoTabsPage, SettingsNoTabsStub],
]);

/**
 * The production table with only the components above swapped; every guard, `data` entry and lazy
 * boundary stays. The copy is mutated through `Route`'s own optional members
 * (`exactOptionalPropertyTypes` forbids writing `component: undefined`), and nothing else about a
 * route changes.
 *
 * A `loadChildren` loader is wrapped so the table it returns gets the same stubs: the Settings
 * shell is one such table (`ENTRY_ROUTES`), and it must not drag the UI slice's templates into this
 * spec. Every loader in the table returns a promise of `Routes`.
 */
function withComponentStubs(table: readonly Route[]): Routes {
  return table.map((route) => {
    const next: Route = { ...route };
    if (route.component !== undefined) {
      next.component = COMPONENT_STUBS.get(route.component) ?? route.component;
    }
    if (route.children !== undefined) {
      next.children = withComponentStubs(route.children);
    }
    if (typeof route.loadChildren === 'function') {
      const load = route.loadChildren as () => Promise<Routes>;
      next.loadChildren = async () => withComponentStubs(await load());
    }
    return next;
  });
}

/** The org the fake identity lists; every other id must be indistinguishable from a missing one. */
const ORG: OrganizationSummaryDto = {
  id: '018f0000-0000-7000-8000-000000000001',
  slug: 'acme-robotics',
  name: 'Acme Robotics',
  status: 'active',
};

/** The one project of {@link ORG}, from `fixtures/web-api/projects-list/input.json`. */
const PROJECT: ProjectDto = {
  id: '018f0000-0000-7000-8000-000000000011',
  org_id: ORG.id,
  name: 'Storefront web',
  slug: 'storefront-web',
  status: 'active',
  default_environment_id: null,
  created_at: '2026-10-02T09:00:00Z',
  updated_at: '2026-10-18T11:20:00Z',
  version: 3,
  retention_days: null,
  monthly_run_quota: 5000,
  case_prefix: 'STOR',
};

/** An id of the right shape that belongs to nobody: the URL of a foreign tenant. */
const FOREIGN_ORG_ID = '018f0000-0000-7000-8000-0000000000ff';
const FOREIGN_PROJECT_ID = '018f0000-0000-7000-8000-0000000000fe';

/** Two permissions the "signed in, allowed to read the project" cases start from (AC4). */
const BASE_PERMISSIONS: ReadonlySet<string> = new Set(['project:read', 'organization:read']);

/** The typed 401 the store turns into `sessionState === 'anonymous'`. */
function unauthenticated(): ApiRequestError {
  const error: ApiError = {
    kind: 'unauthenticated',
    status: 401,
    code: 'unauthenticated',
    message: 'no usable session for this request',
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  };
  return new ApiRequestError(error);
}

/** The three reads the store makes of `IdentityDataService`; the fake answers exactly those. */
type IdentityDataReads = Pick<
  IdentityDataService,
  'listPermissions' | 'listMyOrganizations' | 'listProjects'
>;

/** The catalogue a session would read; the fake source returns the codes it is given. */
class FakeIdentityData implements IdentityDataReads {
  /** `false` answers every identity call with a typed 401. */
  signedIn = true;
  organizations: readonly OrganizationSummaryDto[] = [ORG];
  projects: readonly ProjectDto[] = [PROJECT];

  listPermissions(): Promise<readonly PermissionView[]> {
    if (!this.signedIn) {
      return Promise.reject(unauthenticated());
    }
    // The fake source is what decides the effective set, so the catalogue body is irrelevant here.
    return Promise.resolve([]);
  }

  listMyOrganizations(): Promise<readonly OrganizationSummaryDto[]> {
    if (!this.signedIn) {
      return Promise.reject(unauthenticated());
    }
    return Promise.resolve(this.organizations);
  }

  listProjects(orgId: string): Promise<readonly ProjectDto[]> {
    if (!this.signedIn) {
      return Promise.reject(unauthenticated());
    }
    return Promise.resolve(this.projects.filter((project) => project.org_id === orgId));
  }
}

/** The per-test effective permission set, injected into the fake source below. */
class PermittedCodes {
  constructor(readonly codes: ReadonlySet<string>) {}
}

/** `@Injectable()` on the double: Angular warns (NG0203-adjacent deprecation) for a class that
 * inherits its decorator without declaring one. */
@Injectable()
class FakePermissionsSource extends EffectivePermissionsSource {
  private readonly permitted = inject(PermittedCodes);

  override load(): Promise<ReadonlySet<string>> {
    return Promise.resolve(this.permitted.codes);
  }
}

/** A mounted harness plus the fakes the case configured. */
interface Harness {
  readonly harness: RouterTestingHarness;
  readonly identity: FakeIdentityData;
  readonly permitted: PermittedCodes;
}

/** Configure the TestBed with the stubbed table and mount a `RouterTestingHarness`. */
async function setUp(
  identity: FakeIdentityData = new FakeIdentityData(),
  codes: ReadonlySet<string> = BASE_PERMISSIONS,
): Promise<Harness> {
  const permitted = new PermittedCodes(codes);
  await TestBed.configureTestingModule({
    imports: [
      ShellHostStub,
      ForbiddenStub,
      NotFoundStub,
      WelcomeStub,
      UnavailableStub,
      LoginStub,
      SettingsShellStub,
      SettingsTabStub,
      SettingsNoTabsStub,
    ],
    providers: [
      provideZonelessChangeDetection(),
      provideRouter(withComponentStubs(routes)),
      { provide: IdentityDataService, useValue: identity },
      { provide: PermittedCodes, useValue: permitted },
      { provide: EffectivePermissionsSource, useClass: FakePermissionsSource },
    ],
  }).compileComponents();
  return { harness: await RouterTestingHarness.create(), identity, permitted };
}

/** The registry row a case names by permission, or a failure that names the missing row. */
function entryWithPermission(permission: string): NavEntry {
  const entry = NAV_ENTRIES.find((candidate) => candidate.permission === permission);
  if (entry === undefined) {
    throw new Error(`NAV_ENTRIES has no entry gated by '${permission}'`);
  }
  return entry;
}

/** The URL a registry entry lives at (Q4: the scope picks the prefix, the registry the segment). */
function urlOf(entry: NavEntry): string {
  return entry.scope === 'org'
    ? `/orgs/${ORG.id}/${entry.segment}`
    : `/orgs/${ORG.id}/projects/${PROJECT.id}/${entry.segment}`;
}

/** The marker the currently mounted stub renders; empty when the mounted page is a real one. */
function renderedMarker(): string {
  return document.querySelector('.marker')?.textContent?.trim() ?? '';
}

describe('App routes (09.1.1-T4.1)', () => {
  it('does not match a route whose permission the server-declared set lacks (AC4)', async () => {
    const authoring = entryWithPermission('authoring:read');
    const { harness } = await setUp();
    const router = TestBed.inject(Router);

    // `project:read` is present, `authoring:read` is not: the route exists, the grant does not.
    await harness.navigateByUrl(urlOf(authoring));

    expect(router.url).toBe('/forbidden');
    expect(renderedMarker()).toBe(FORBIDDEN_MARKER);
  });

  it('renders the same not-found page for a foreign org, a foreign project and an unknown path', async () => {
    const { harness } = await setUp();
    const router = TestBed.inject(Router);
    const foreignOrgUrl = `/orgs/${FOREIGN_ORG_ID}/overview`;
    const foreignProjectUrl = `/orgs/${ORG.id}/projects/${FOREIGN_PROJECT_ID}/overview`;
    const unknownUrl = '/no-such-screen';

    // A foreign id must never become a 403: that would confirm the id exists in another tenant
    // (00 §9.2). The scope guards refuse the match, so the wildcard answers exactly like it does
    // for a path that exists nowhere.
    await harness.navigateByUrl(foreignOrgUrl);
    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(router.url).toBe(foreignOrgUrl);

    await harness.navigateByUrl(foreignProjectUrl);
    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(router.url).toBe(foreignProjectUrl);

    await harness.navigateByUrl(unknownUrl);
    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(router.url).toBe(unknownUrl);
  });

  it('does not match a later-phase entry in the G1 build', async () => {
    const laterPhase = NAV_ENTRIES.find((candidate) => candidate.phase !== ACTIVE_PHASE);
    if (laterPhase === undefined) {
      throw new Error('NAV_ENTRIES has no entry from a later phase to check the phase gate with');
    }
    const { harness } = await setUp();

    await harness.navigateByUrl(urlOf(laterPhase));

    // Hidden, never teased: the entry is as absent as a path that does not exist.
    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(TestBed.inject(Router).url).toBe(urlOf(laterPhase));
  });

  it('sends an anonymous session to /login, carrying the attempted URL for a deep link', async () => {
    const identity = new FakeIdentityData();
    identity.signedIn = false;
    const { harness } = await setUp(identity);
    const router = TestBed.inject(Router);

    // The root attempt is the root itself, so it needs no `returnUrl` (landing.guard.ts).
    await harness.navigateByUrl('/', LoginStub);
    expect(router.url.split('?')[0]).toBe('/login');

    // A deep link does: it is the URL the sign-in flow has to come back to.
    const deepLink = `/orgs/${ORG.id}/projects/${PROJECT.id}/overview`;
    await harness.navigateByUrl(deepLink, LoginStub);
    const url = router.url;
    expect(url.split('?')[0]).toBe('/login');
    expect(url).toContain('returnUrl=');
    expect(decodeURIComponent(url)).toContain(deepLink);
    expect(renderedMarker()).toBe('login-stub');
  });

  it('renders the entry stub for a deep link to an allowed entry', async () => {
    const entry = NAV_ENTRIES.find(
      (candidate) =>
        candidate.phase === ACTIVE_PHASE &&
        candidate.id !== 'overview' &&
        // `settings` owns a route table of its own (`ENTRY_ROUTES`), covered by the cases below.
        candidate.id !== 'settings' &&
        BASE_PERMISSIONS.has(candidate.permission),
    );
    if (entry === undefined) {
      throw new Error('NAV_ENTRIES has no allowed, active-phase entry without a real screen');
    }
    const { harness } = await setUp();

    await harness.navigateByUrl(urlOf(entry));

    // The harness's own outlet renders the shell host stub, so `navigateByUrl`'s expected-component
    // form can only be used for a route mounted outside the shell (the `/login` cases above): the
    // page under the shell is asserted on what it rendered.
    expect(document.querySelector('h1')?.textContent?.trim()).toBe(entry.label);
  });

  it('lands on the first org and its first project', async () => {
    const { harness } = await setUp();

    await harness.navigateByUrl('/');

    expect(TestBed.inject(Router).url).toBe(`/orgs/${ORG.id}/projects/${PROJECT.id}/overview`);
    expect(document.querySelector('h1')?.textContent?.trim()).toBe(PROJECT.name);
  });

  it('sends every active entry to /forbidden when the server declares no permission', async () => {
    const { harness } = await setUp(new FakeIdentityData(), new Set<string>());
    const router = TestBed.inject(Router);

    for (const entry of NAV_ENTRIES.filter((candidate) => candidate.phase === ACTIVE_PHASE)) {
      await harness.navigateByUrl(urlOf(entry));
      expect(router.url, `entry '${entry.id}' should have been refused`).toBe('/forbidden');
    }
  });
});

describe('Settings shell entry override (09.7.1-T1.1)', () => {
  /** The registry row a case names, or a failure naming the missing row. */
  function entryOf(id: string): NavEntry {
    const entry = NAV_ENTRIES.find((candidate) => candidate.id === id);
    if (entry === undefined) {
      throw new Error(`NAV_ENTRIES has no '${id}' entry`);
    }
    return entry;
  }

  it('mounts the Settings shell table for the settings entry, landing on the first available tab', async () => {
    const { harness } = await setUp();
    const router = TestBed.inject(Router);

    await harness.navigateByUrl(urlOf(entryOf('settings')));

    // `BASE_PERMISSIONS` holds `project:read`, so Quota & Retention is the first tab in strip order
    // the caller may open: the shell table answered, not the area's stub table.
    expect(router.url).toBe(`/orgs/${ORG.id}/settings/quota-retention`);
    expect(
      document.querySelector('[data-settings-tab-body]')?.getAttribute('data-settings-tab-body'),
    ).toBe('quota-retention');
    expect(document.querySelector('app-route-stub')).toBeNull();
  });

  it('renders not found, never /forbidden, for a Settings tab the caller may not open (AC1)', async () => {
    const { harness } = await setUp();
    const router = TestBed.inject(Router);
    const url = `/orgs/${ORG.id}/settings/members`;

    // `membership:read` is absent: the tab is absent, and its route is as unmatched as an unknown
    // segment — the app's single wildcard renders the 404 pattern with the URL untouched.
    await harness.navigateByUrl(url);

    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(router.url).toBe(url);
    expect(router.url).not.toBe('/forbidden');
  });

  it('keeps the area stub table for the other settings destinations', async () => {
    const quota = entryOf('quota');
    const { harness } = await setUp(
      new FakeIdentityData(),
      new Set(['organization:read', 'usage:read']),
    );

    await harness.navigateByUrl(urlOf(quota));

    // `quota` is gated by `usage:read` and served by `features/settings/settings.routes.ts`, which
    // no tab task has replaced yet: the placeholder table is still its destination.
    expect(TestBed.inject(Router).url).toBe(`/orgs/${ORG.id}/usage`);
    expect(document.querySelector('app-route-stub')).not.toBeNull();
    expect(document.querySelector('h1')?.textContent?.trim()).toBe(quota.label);
  });
});
