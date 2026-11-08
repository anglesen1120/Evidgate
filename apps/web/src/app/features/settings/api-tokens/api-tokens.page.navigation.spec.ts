// The URL half of the page's behaviour, driven through a real `Router`: the write the page makes
// must land on the route the page is on, and never on the one a transition is leaving.
//
// Both cases render the window an organization switch opens — the store's scope is already
// dropped while the transition is still in flight — because that window is the only place the
// page's own write can disagree with the URL.

import { InteractivityChecker } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, signal, type WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, type CanActivateFn, type Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { ApiTokenListResponse, ProjectDto } from '@evidgate/sdk';
import { ApiTokensService } from '../../../core/data/api-tokens-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { ApiTokensPage } from './api-tokens.page';
import { ROW, project } from './api-tokens.test-support';

/** Where an organization switch lands; the page under test is left behind, as in the app. */
@Component({
  selector: 'app-spec-org-home',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class OrgHomeStub {}

/** The `SessionStore` surface the page and its store read, as signals a case can move. */
interface SessionFixture {
  readonly currentOrg: WritableSignal<{ id: string } | null>;
  readonly projects: WritableSignal<readonly ProjectDto[]>;
  readonly currentProject: WritableSignal<ProjectDto | undefined>;
  readonly permissions: WritableSignal<ReadonlySet<string>>;
  readonly has: (code: string) => boolean;
  readonly bootstrap: () => Promise<void>;
  readonly onSessionEnd: () => () => void;
}

/** One real-router mount: the session signals plus the data seam the store reads. */
interface World {
  readonly org: WritableSignal<{ id: string } | null>;
  readonly listed: WritableSignal<readonly ProjectDto[]>;
  readonly currentProject: WritableSignal<ProjectDto | undefined>;
  readonly session: SessionFixture;
  readonly list: () => Promise<ApiTokenListResponse>;
}

function createWorld(projects: readonly ProjectDto[]): World {
  const org = signal<{ id: string } | null>({ id: 'org-1' });
  const listed = signal<readonly ProjectDto[]>([...projects]);
  const currentProject = signal<ProjectDto | undefined>(projects[0]);
  const permissions = signal<ReadonlySet<string>>(
    new Set(['api_token:read', 'api_token:write', 'run:read']),
  );
  const list = vi.fn(() =>
    Promise.resolve<ApiTokenListResponse>({ items: [ROW], next_cursor: null }),
  );
  const session: SessionFixture = {
    currentOrg: org,
    projects: listed,
    currentProject,
    permissions,
    has: (code: string) => permissions().has(code),
    bootstrap: () => Promise.resolve(undefined),
    onSessionEnd: () => () => undefined,
  };
  return { org, listed, currentProject, session, list };
}

/**
 * A guard that holds one navigation open, so a case can render the window between the scope being
 * replaced and the new route activating. `onEnter` runs inside the guard, in that window.
 */
class HeldTransition {
  onEnter: (() => void) | null = null;
  private pending: (() => void) | null = null;
  private arrived: (() => void) | null = null;
  private readonly reached = new Promise<void>((resolve) => (this.arrived = resolve));
  private holding = false;

  hold(): void {
    this.holding = true;
  }

  waitUntilHeld(): Promise<void> {
    return this.reached;
  }

  release(): void {
    this.pending?.();
  }

  readonly guard: CanActivateFn = () => {
    if (!this.holding) return true;
    this.holding = false;
    this.onEnter?.();
    this.arrived?.();
    return new Promise<boolean>((resolve) => {
      this.pending = () => resolve(true);
    });
  };
}

async function mountAt(routes: Routes, world: World): Promise<RouterTestingHarness> {
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      { provide: SessionStore, useValue: world.session },
      {
        provide: ApiTokensService,
        useValue: { list: world.list, create: vi.fn(), revoke: vi.fn() },
      },
    ],
  });
  vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  return RouterTestingHarness.create();
}

/**
 * Renders until `done()` holds. A fixed number of ticks would race the router: the page's write is
 * a navigation of its own, and its effect only runs on a change-detection pass after it commits.
 */
async function until(harness: RouterTestingHarness, done: () => boolean): Promise<void> {
  for (let round = 0; round < 20 && !done(); round += 1) {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
    harness.detectChanges();
  }
  expect(done()).toBe(true);
}

/** The project ids the picker offers, in render order; empty while the page renders no picker. */
function offeredProjects(): readonly string[] {
  return Array.from(
    document.querySelectorAll<HTMLOptionElement>('#api-tokens-project option'),
    (option) => option.value,
  );
}

describe('ApiTokensPage URL writes across navigation', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('leaves an organization switch alone instead of writing the scope that switch emptied', async () => {
    const world = createWorld([project('project-1'), project('project-2', 'Other')]);
    const held = new HeldTransition();
    // Where the org guard stands today: the new org is published and the old org's projects are
    // gone before the new route activates.
    held.onEnter = () => {
      world.org.set({ id: 'org-2' });
      world.listed.set([]);
      world.currentProject.set(undefined);
    };
    const harness = await mountAt(
      [
        { path: 'orgs/:orgId/settings/api-tokens', component: ApiTokensPage },
        { path: 'orgs/:orgId', component: OrgHomeStub, canActivate: [held.guard] },
      ],
      world,
    );
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    await harness.navigateByUrl('/orgs/org-1/settings/api-tokens?project=project-1');
    await until(harness, () => offeredProjects().includes('project-1'));

    held.hold();
    const switching = harness.navigateByUrl('/orgs/org-2');
    await held.waitUntilHeld();
    // The page keeps rendering while the new org's grants are read. The picker leaving the render
    // proves this pass saw the emptied scope, so a write would have been attempted here.
    await until(harness, () => offeredProjects().length === 0);
    expect(navigate).not.toHaveBeenCalled();

    held.release();
    await switching;
    await until(harness, () => router.url === '/orgs/org-2');
  });

  it('repairs the URL after a held transition lands instead of writing during it', async () => {
    const world = createWorld([project('project-1'), project('project-2', 'Other')]);
    const held = new HeldTransition();
    const harness = await mountAt(
      [
        {
          path: 'orgs/:orgId/settings/api-tokens',
          component: ApiTokensPage,
          canActivate: [held.guard],
          // The page's own query changes must not run the guard; only the case navigates.
          runGuardsAndResolvers: 'always',
        },
      ],
      world,
    );
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    await harness.navigateByUrl('/orgs/org-1/settings/api-tokens?project=project-2');
    await until(harness, () => offeredProjects().join() === 'project-1,project-2');
    expect(navigate).not.toHaveBeenCalled();

    // The list read by the held transition no longer holds the project the URL names.
    held.onEnter = () => world.listed.set([project('project-1')]);
    held.hold();
    const landing = harness.navigateByUrl(
      '/orgs/org-1/settings/api-tokens?project=project-2&ref=1',
    );
    await held.waitUntilHeld();
    // The picker follows the store, so this render saw the shortened list; the URL is still the
    // one the transition is landing on, and nothing may be written into it yet.
    await until(harness, () => offeredProjects().join() === 'project-1');
    expect(navigate).not.toHaveBeenCalled();

    held.release();
    await landing;
    await until(harness, () => router.parseUrl(router.url).queryParams['project'] === 'project-1');
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-1', cursor: null },
        // No click is behind it: the repair replaces the entry the user is on.
        replaceUrl: true,
      }),
    );
  });
});
