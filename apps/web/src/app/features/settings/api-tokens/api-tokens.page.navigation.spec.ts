// The URL half of the page's behaviour, driven through a real `Router`: the write the page makes
// must land on the route the page is on, and never on the one a transition is leaving.
//
// Both cases render the window an organization switch opens — the store's scope is already
// dropped while the transition is still in flight — because that window is the only place the
// page's own write can disagree with the URL.

import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { ApiTokensPage } from './api-tokens.page';
import { ApiTokensStore } from './api-tokens.store';
import {
  HeldTransition,
  OrgHomeStub,
  createWorld,
  mountAt,
  offeredProjects,
  project,
  until,
} from './api-tokens.test-support';

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

  it('keeps a retired cursor retired when a same-page navigation lands after the list restarted', async () => {
    const world = createWorld([project('project-1')]);
    const held = new HeldTransition();
    const harness = await mountAt(
      [
        {
          path: 'orgs/:orgId/settings/api-tokens',
          component: ApiTokensPage,
          canActivate: [held.guard],
          runGuardsAndResolvers: 'always',
        },
      ],
      world,
    );
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    await harness.navigateByUrl('/orgs/org-1/settings/api-tokens?project=project-1&cursor=page-2');
    await until(harness, () => offeredProjects().join() === 'project-1');
    const store = harness.routeDebugElement?.injector.get(ApiTokensStore);
    if (!store) throw new Error('The page did not mount');
    expect(store.cursor()).toBe('page-2');

    held.hold();
    const landing = harness.navigateByUrl(
      '/orgs/org-1/settings/api-tokens?project=project-1&cursor=page-2&ref=1',
    );
    await held.waitUntilHeld();
    // A create finishing now sends the list back to page one while the transition is in flight.
    store.reloadFirst();
    expect(store.cursor()).toBeUndefined();
    held.release();
    await landing;

    // The landed URL still names the retired cursor; the repair has to remove it, not restore it.
    await until(harness, () => router.parseUrl(router.url).queryParams['cursor'] === undefined);
    expect(store.cursor()).toBeUndefined();
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({ queryParams: { project: 'project-1', cursor: null } }),
    );
  });
});
