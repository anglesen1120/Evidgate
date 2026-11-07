// Shared harness for the app.routes partial specs: marker stubs for the components whose own
// specs cover their templates, the two faked server seams, and `setUp`, so each partial spec
// carries one responsibility instead of repeating the wiring.

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
import { CallbackPage } from './features/auth/callback/callback.page';
import { LoginPage } from './features/auth/login/login.page';
import { SettingsNoTabsPage } from './features/settings/settings-no-tabs.page';
import { SettingsShellPage } from './features/settings/settings-shell.page';
import { SettingsTabPlaceholderPage } from './features/settings/settings-tab-placeholder.page';
import { QuotaPanelComponent } from './features/settings/quota-retention/quota-panel.component';
import { ShellComponent } from './shell/layout/shell.component';
import type { NavEntry } from './shell/nav/nav.models';
import { NAV_ENTRIES } from './shell/nav/nav-registry';
import { ForbiddenPage } from './shell/pages/forbidden.page';
import { NoOrganizationPage } from './shell/pages/no-organization.page';
import { NotFoundPage } from './shell/pages/not-found.page';
import { ServiceUnavailablePage } from './shell/pages/service-unavailable.page';

/** Marker text each stub renders, so a case can prove which page the router mounted. */
export const FORBIDDEN_MARKER = 'forbidden-stub';
export const NOT_FOUND_MARKER = 'not-found-stub';

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
export class LoginStub {
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
  [LoginPage, LoginStub],
  [CallbackPage, LoginStub],
  [SettingsShellPage, SettingsShellStub],
  [SettingsTabPlaceholderPage, SettingsTabStub],
  [SettingsNoTabsPage, SettingsNoTabsStub],
  [QuotaPanelComponent, SettingsTabStub],
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
    // 09.1.1-T5.1: `/login` and `/auth/callback` are lazy screens, so the stub has to sit behind
    // their loader — the route table is otherwise untouched and the loader is still the production
    // one, which is what makes the lazy boundary itself part of this spec.
    if (typeof route.loadComponent === 'function') {
      const load = route.loadComponent as () => Promise<Type<unknown>>;
      next.loadComponent = async () => {
        const loaded = await load();
        return COMPONENT_STUBS.get(loaded) ?? loaded;
      };
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
export const ORG: OrganizationSummaryDto = {
  id: '018f0000-0000-7000-8000-000000000001',
  slug: 'acme-robotics',
  name: 'Acme Robotics',
  status: 'active',
};

/** The one project of {@link ORG}, from `fixtures/web-api/projects-list/input.json`. */
export const PROJECT: ProjectDto = {
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
export const FOREIGN_ORG_ID = '018f0000-0000-7000-8000-0000000000ff';
export const FOREIGN_PROJECT_ID = '018f0000-0000-7000-8000-0000000000fe';

/** Two permissions the "signed in, allowed to read the project" cases start from (AC4). */
export const BASE_PERMISSIONS: ReadonlySet<string> = new Set(['project:read', 'organization:read']);

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
export class FakeIdentityData implements IdentityDataReads {
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
export interface Harness {
  readonly harness: RouterTestingHarness;
  readonly identity: FakeIdentityData;
  readonly permitted: PermittedCodes;
}

/** Configure the TestBed with the stubbed table and mount a `RouterTestingHarness`. */
export async function setUp(
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
export function entryWithPermission(permission: string): NavEntry {
  const entry = NAV_ENTRIES.find((candidate) => candidate.permission === permission);
  if (entry === undefined) {
    throw new Error(`NAV_ENTRIES has no entry gated by '${permission}'`);
  }
  return entry;
}

/** The URL a registry entry lives at (Q4: the scope picks the prefix, the registry the segment). */
export function urlOf(entry: NavEntry): string {
  return entry.scope === 'org'
    ? `/orgs/${ORG.id}/${entry.segment}`
    : `/orgs/${ORG.id}/projects/${PROJECT.id}/${entry.segment}`;
}

/** The marker the currently mounted stub renders; empty when the mounted page is a real one. */
export function renderedMarker(): string {
  return document.querySelector('.marker')?.textContent?.trim() ?? '';
}
