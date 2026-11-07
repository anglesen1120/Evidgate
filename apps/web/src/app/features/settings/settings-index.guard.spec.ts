// 09.7.1-T1.2 (plan 09.7.1 §5 route table; docs/design/web/settings.md `empty` state): the shell's
// landing decision, exercised through a real `Router` on the real table (`settings-shell.routes.ts`)
// with only its page components (the shell, the landing state, the placeholder and the tab pages)
// replaced by markers — the redirect target is a URL, so a unit call to the guard could not prove
// it.
//
// Three behaviours: the first available tab in strip order is the target; a later tab never wins
// over an earlier one the caller may use; no available tab leaves the index route to render the
// "no settings destinations" state instead of redirecting nowhere.

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
import { SettingsNoTabsPage } from './settings-no-tabs.page';
import { SettingsShellPage } from './settings-shell.page';
import { SettingsTabPlaceholderPage } from './settings-tab-placeholder.page';
import { routes as shellRoutes } from './settings-shell.routes';

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
 * Where the production table mounts the shell: `/orgs/:orgId/settings`. The org id is never read
 * here — the shell's own guards do not depend on it, and the parent scope guard is exercised by
 * `app.routes.spec.ts`.
 */
const TABLE: Routes = [{ path: 'orgs/:orgId/settings', children: withPageStubs(shellRoutes) }];

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
    imports: [SettingsShellStub, SettingsTabStub, SettingsNoTabsStub],
    providers: [provideRouter(TABLE), { provide: SessionStore, useValue: session }],
  }).compileComponents();
  return RouterTestingHarness.create();
}

/** The id the rendered tab body carries, or `null` when no tab body is rendered. */
function renderedTabBody(): string | null {
  return (
    document.querySelector('[data-settings-tab-body]')?.getAttribute('data-settings-tab-body') ??
    null
  );
}

describe('settingsIndexGuard (09.7.1-T1.2)', () => {
  it('redirects the shell index to the first available tab (audit:read only)', async () => {
    const harness = await setUp(new Set(['audit:read']));
    const router = TestBed.inject(Router);

    await harness.navigateByUrl('/orgs/o1/settings');

    // Only Audit is available: Members needs `membership:read`, Quota & Retention `usage:read`.
    expect(router.url).toBe('/orgs/o1/settings/audit');
    expect(renderedTabBody()).toBe('audit');
  });

  it('keeps strip order: members wins over a later available tab', async () => {
    const harness = await setUp(new Set(['membership:read', 'audit:read']));
    const router = TestBed.inject(Router);

    await harness.navigateByUrl('/orgs/o1/settings');

    expect(router.url).toBe('/orgs/o1/settings/members');
    expect(renderedTabBody()).toBe('members');
  });

  it('renders the no-tabs state when no tab is available', async () => {
    const harness = await setUp(new Set<string>());
    const router = TestBed.inject(Router);

    await harness.navigateByUrl('/orgs/o1/settings');

    // No redirect at all: the URL stays on the shell and the empty state answers it.
    expect(router.url).toBe('/orgs/o1/settings');
    expect(document.querySelector('.marker')?.textContent?.trim()).toBe('no-tabs-stub');
    expect(renderedTabBody()).toBeNull();
  });
});
