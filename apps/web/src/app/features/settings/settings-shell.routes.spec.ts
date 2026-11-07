// 09.7.1-T1.2 (plan 09.7.1 §2 AC1, §5 route table; 00 §9.2; D-S68-Q4): the match table of the
// Settings shell, exercised through a real `Router` on the real table with only its page components
// replaced by markers.
//
// AC1 is a routing fact, not a styling one: a tab the caller may not use must be as absent as an
// unknown segment. So every refusal here is asserted the same way — the wildcard route renders the
// not-found pattern, the URL is left alone (no `/forbidden` rewrite) and the answer is byte for
// byte the one an unknown segment gets.

import { ChangeDetectionStrategy, Component, inject, type Type } from '@angular/core';
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

import { SessionStore, type SessionState } from '../../core/session/session.store';
import { AuditPage } from './audit/audit.page';
import { MembersPage } from './members/members.page';
import { PolicyPage } from './policy/policy.page';
import { SecretsPage } from './secrets/secrets.page';
import { QuotaPanelComponent } from './quota-retention/quota-panel.component';
import { SettingsNoTabsPage } from './settings-no-tabs.page';
import { SettingsShellPage } from './settings-shell.page';
import { SettingsTabPlaceholderPage } from './settings-tab-placeholder.page';
import { routes as shellRoutes } from './settings-shell.routes';
import { SETTINGS_TABS, isSettingsTabAvailable } from './settings-tabs';

/** Marker text the not-found stub renders. */
const NOT_FOUND_MARKER = 'not-found-stub';

/** The shell without its strip: the tab routes render into this outlet. */
@Component({
  selector: 'app-spec-settings-shell',
  imports: [RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<router-outlet />',
})
class SettingsShellStub {}

/** A tab body marker that prints the id the route carried in `data.settingsTabId`. */
@Component({
  selector: 'app-spec-settings-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker" [attr.data-settings-tab-body]="tabId">{{ tabId }}</p>',
})
class SettingsTabStub {
  private readonly route = inject(ActivatedRoute);

  readonly tabId = tabIdOf(this.route);
}

/** The no-tabs state marker. */
@Component({
  selector: 'app-spec-settings-no-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker">{{ marker }}</p>',
})
class SettingsNoTabsStub {
  protected readonly marker = 'no-tabs-stub';
}

/** What the wildcard route renders — the app's not-found pattern (09.1.1-T8). */
@Component({
  selector: 'app-spec-not-found',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p class="marker">{{ marker }}</p>',
})
class NotFoundStub {
  readonly marker = NOT_FOUND_MARKER;
}

/** The `settingsTabId` of the activated route, or a marker that the route carried none. */
function tabIdOf(route: ActivatedRoute): string {
  const id: unknown = route.snapshot.data['settingsTabId'];
  return typeof id === 'string' ? id : 'missing-settings-tab-id';
}

/** The UI slice's pages, replaced by the markers above (their own specs cover their templates). */
const PAGE_STUBS = new Map<unknown, Type<unknown>>([
  [SettingsShellPage, SettingsShellStub],
  [SettingsTabPlaceholderPage, SettingsTabStub],
  [SettingsNoTabsPage, SettingsNoTabsStub],
  [MembersPage, SettingsTabStub],
  [AuditPage, SettingsTabStub],
  [SecretsPage, SettingsTabStub],
  [PolicyPage, SettingsTabStub],
  [QuotaPanelComponent, SettingsTabStub],
]);

/** The real shell table with only its page components swapped. */
function withPageStubs(table: readonly Route[]): Routes {
  return table.map((route) => {
    const next: Route = { ...route };
    if (route.component !== undefined) {
      next.component = PAGE_STUBS.get(route.component) ?? route.component;
    }
    if (route.children !== undefined) {
      next.children = withPageStubs(route.children);
    }
    return next;
  });
}

/**
 * The shell mounted where the production table mounts it, plus the app's single wildcard: the
 * fall-through a refused tab relies on (00 §9.2) is that wildcard, so it is part of the table here.
 */
const TABLE: Routes = [
  { path: 'orgs/:orgId/settings', children: withPageStubs(shellRoutes) },
  { path: '**', component: NotFoundStub },
];

/** The guard's whole dependency, shaped like `SessionStore`. */
class FakeSessionStore {
  state: SessionState = 'authenticated';
  granted: ReadonlySet<string> = new Set<string>();

  bootstrap(): Promise<void> {
    return Promise.resolve();
  }

  sessionState(): SessionState {
    return this.state;
  }

  has(code: string): boolean {
    return this.state === 'authenticated' && this.granted.has(code);
  }
}

/** Mount the real table over a session that holds exactly `codes`. */
async function setUp(codes: ReadonlySet<string>): Promise<RouterTestingHarness> {
  const session = new FakeSessionStore();
  session.granted = codes;
  await TestBed.configureTestingModule({
    imports: [SettingsShellStub, SettingsTabStub, SettingsNoTabsStub, NotFoundStub],
    providers: [provideRouter(TABLE), { provide: SessionStore, useValue: session }],
  }).compileComponents();
  return RouterTestingHarness.create();
}

/** The marker the mounted page renders. */
function renderedMarker(): string {
  return document.querySelector('.marker')?.textContent?.trim() ?? '';
}

/** The tabs of `SETTINGS_TABS` that a caller holding `codes` may not open. */
function refusedTabs(codes: ReadonlySet<string>): readonly string[] {
  return SETTINGS_TABS.filter((tab) => !isSettingsTabAvailable(tab, (code) => codes.has(code))).map(
    (tab) => tab.segment,
  );
}

describe('Settings shell route table (09.7.1-T1.2)', () => {
  it('mounts delivered settings bodies and keeps other tabs as placeholders', async () => {
    const tabs = shellRoutes[0]?.children?.filter((route) => route.data?.['settingsTabId']);
    expect(tabs?.find((route) => route.path === 'members')?.component).toBe(MembersPage);
    expect(tabs?.find((route) => route.path === 'audit')?.component).toBe(AuditPage);
    expect(tabs?.find((route) => route.path === 'secrets')?.component).toBe(SecretsPage);
    expect(tabs?.find((route) => route.path === 'policy')?.component).toBe(PolicyPage);
    expect(tabs?.find((route) => route.path === 'quota-retention')?.component).toBe(
      QuotaPanelComponent,
    );
    expect(tabs?.find((route) => route.path === 'api-tokens')?.loadComponent).toEqual(
      expect.any(Function),
    );
    // Delivered bodies are independent of phase visibility: Secrets is G2 and Policy is G4, so
    // both are hidden in the G1 build even though their bodies exist.
    const delivered = tabs?.filter((route) => route.component !== SettingsTabPlaceholderPage);
    expect(delivered?.map((route) => route.path)).toEqual(
      SETTINGS_TABS.filter((tab) =>
        ['members', 'audit', 'secrets', 'api-tokens', 'policy', 'quota-retention'].includes(tab.id),
      ).map((tab) => tab.segment),
    );
    const harness = await setUp(new Set(['membership:read']));
    await harness.navigateByUrl('/orgs/o1/settings/members');
    expect(
      document.querySelector('[data-settings-tab-body]')?.getAttribute('data-settings-tab-body'),
    ).toBe('members');
  });
  it('renders the body of a tab the caller may use, with its id in the route data', async () => {
    // Every G1 tab is delivered now; the body stub stands in for QuotaPanelComponent, whose own
    // spec covers the panel. The guard is what this case is about: `usage:read` opens the tab.
    const harness = await setUp(new Set(['usage:read']));

    await harness.navigateByUrl('/orgs/o1/settings/quota-retention');

    const body = document.querySelector('[data-settings-tab-body]');
    expect(body?.getAttribute('data-settings-tab-body')).toBe('quota-retention');
    expect(TestBed.inject(Router).url).toBe('/orgs/o1/settings/quota-retention');
  });

  it.each(refusedTabs(new Set(['audit:read'])).map((segment) => ({ segment })))(
    'does not match the $segment tab for a caller without its permission (AC1)',
    async ({ segment }) => {
      const harness = await setUp(new Set(['audit:read']));
      const router = TestBed.inject(Router);

      await harness.navigateByUrl(`/orgs/o1/settings/${segment}`);

      // Absent, not forbidden: the wildcard renders the not-found pattern and the URL is untouched.
      expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
      expect(router.url).toBe(`/orgs/o1/settings/${segment}`);
      expect(router.url).not.toBe('/forbidden');
    },
  );

  it.each([
    { segment: 'secrets', code: 'ingest_token:read' },
    { segment: 'agents', code: 'agent_pool:read' },
  ])(
    'does not match the $segment tab in the G1 build even with $code',
    async ({ segment, code }) => {
      const harness = await setUp(new Set([code]));
      const router = TestBed.inject(Router);

      await harness.navigateByUrl(`/orgs/o1/settings/${segment}`);

      // Phase hiding is not authorization, and it is not a teaser either: the route is as absent
      // as a segment nobody wrote.
      expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
      expect(router.url).toBe(`/orgs/o1/settings/${segment}`);
    },
  );

  it('answers a refused tab exactly as it answers an unknown segment', async () => {
    const harness = await setUp(new Set(['audit:read']));
    const router = TestBed.inject(Router);

    await harness.navigateByUrl('/orgs/o1/settings/members');
    const refused = {
      marker: renderedMarker(),
      component: document.querySelector('app-spec-not-found') !== null,
    };

    await harness.navigateByUrl('/orgs/o1/settings/no-such-tab');
    const unknown = {
      marker: renderedMarker(),
      component: document.querySelector('app-spec-not-found') !== null,
    };

    // Same component, same copy: only the URL a user typed differs (00 §9.2 — no existence leak).
    expect(refused).toEqual(unknown);
    expect(refused.component).toBe(true);
    // And neither attempt was rewritten: no `/forbidden`, no bounce to another path.
    expect(router.url).toBe('/orgs/o1/settings/no-such-tab');
  });
});
