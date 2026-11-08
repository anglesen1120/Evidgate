// What the store loads is the page the URL names. Only the page's own page move leads it, and only
// while that move's navigation is in flight: a move whose navigation never landed must lose, or a
// landing that already names a page gets rewritten to the page the user left.

import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import type { ApiTokenListResponse } from '@evidgate/sdk';
import { ApiTokensPage } from './api-tokens.page';
import { ApiTokensStore } from './api-tokens.store';
import {
  HeldTransition,
  ROW,
  createWorld,
  mountAt,
  offeredProjects,
  project,
  settleRouter,
  until,
} from './api-tokens.test-support';

const PAGE_ROUTE = 'orgs/:orgId/settings/api-tokens';
const PAGE_URL = '/orgs/org-1/settings/api-tokens';

/** A list whose first page offers another, so the page renders its next control. */
const withNextPage = (): Promise<ApiTokenListResponse> =>
  Promise.resolve({ items: [ROW], next_cursor: 'page-3' });

/** The page's next control, clicked as the user would. */
function clickNext(): void {
  const next = document.querySelector<HTMLButtonElement>('[data-api-next]');
  if (!next) throw new Error('Missing next-page control');
  next.click();
}

describe('ApiTokensPage cursor readback', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('drops a page move its navigation never landed, so a Back to the same cursor stays put', async () => {
    const world = createWorld([project('project-1')], withNextPage);
    const held = new HeldTransition();
    const harness = await mountAt(
      [
        {
          path: PAGE_ROUTE,
          component: ApiTokensPage,
          canActivate: [held.guard],
          runGuardsAndResolvers: 'always',
        },
      ],
      world,
    );
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    // `ref` stands in for any parameter that differs between two entries naming the same page: it
    // makes the entry the case returns to a URL the router activates instead of one it skips.
    await harness.navigateByUrl(`${PAGE_URL}?project=project-1&cursor=page-2&ref=1`);
    await until(harness, () => offeredProjects().join() === 'project-1');
    // The control exists only once the list read that offers a second page has landed.
    await until(harness, () => document.querySelector('[data-api-next]') !== null);
    const store = harness.routeDebugElement?.injector.get(ApiTokensStore);
    if (!store) throw new Error('The page did not mount');
    expect(store.cursor()).toBe('page-2');
    expect(navigate).not.toHaveBeenCalled();

    // Next is a click, so its write opens a new entry; the guard holds that navigation open.
    held.hold();
    clickNext();
    await until(harness, () => navigate.mock.calls.length === 1);
    await held.waitUntilHeld();
    expect(store.cursor()).toBe('page-3');

    // The user goes back to the entry that names page-2. Its cursor is the one the store last read
    // from the URL, so the readback repeats it while the store still holds the unlanded page.
    await harness.navigateByUrl(`${PAGE_URL}?project=project-1&cursor=page-2`);
    await settleRouter(harness);

    // The landed entry wins: page-2 is what the user asked for, and the page owes it no write.
    expect(store.cursor()).toBe('page-2');
    expect(router.parseUrl(router.url).queryParams['cursor']).toBe('page-2');
    expect(navigate).toHaveBeenCalledTimes(1);
  });

  it('lands a page move whose navigation completes', async () => {
    const world = createWorld([project('project-1')], withNextPage);
    const harness = await mountAt([{ path: PAGE_ROUTE, component: ApiTokensPage }], world);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    await harness.navigateByUrl(`${PAGE_URL}?project=project-1`);
    await until(harness, () => offeredProjects().join() === 'project-1');
    await until(harness, () => document.querySelector('[data-api-next]') !== null);
    const store = harness.routeDebugElement?.injector.get(ApiTokensStore);
    if (!store) throw new Error('The page did not mount');

    clickNext();
    await settleRouter(harness);

    expect(store.cursor()).toBe('page-3');
    expect(router.parseUrl(router.url).queryParams['cursor']).toBe('page-3');
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-1', cursor: 'page-3' },
        // A click is a place the user can return to, so it gets its own entry.
        replaceUrl: false,
      }),
    );
  });
});
