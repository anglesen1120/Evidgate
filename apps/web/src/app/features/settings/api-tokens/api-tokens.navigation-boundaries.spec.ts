import { TestBed } from '@angular/core/testing';
import { Router, type CanActivateFn } from '@angular/router';
import type { ApiTokenListResponse } from '@evidgate/sdk';
import type { Mock } from 'vitest';
import { ApiTokensPage } from './api-tokens.page';
import { ApiTokensStore } from './api-tokens.store';
import {
  HeldTransition,
  ROW,
  createWorld,
  mountAt,
  project,
  settleRouter,
  until,
} from './api-tokens.test-support';

const PAGE_URL = '/orgs/org-1/settings/api-tokens';

function clickNext(): void {
  const button = document.querySelector<HTMLButtonElement>('[data-api-next]');
  if (!button) throw new Error('Missing next-page control');
  button.click();
}

function chooseProject(id: string): void {
  const picker = document.querySelector<HTMLSelectElement>('#api-tokens-project');
  if (!picker) throw new Error('Missing project picker');
  picker.value = id;
  picker.dispatchEvent(new Event('change', { bubbles: true }));
}

async function boot(guard?: CanActivateFn) {
  const list = vi.fn<(projectId?: string, cursor?: string) => Promise<ApiTokenListResponse>>(
    (projectId) =>
      Promise.resolve({
        items: [ROW],
        next_cursor: projectId === 'project-a' ? 'a-page-3' : 'b-page-2',
      }),
  );
  const world = createWorld([project('project-a'), project('project-b')], list);
  const harness = await mountAt(
    [
      {
        path: 'orgs/:orgId/settings/api-tokens',
        component: ApiTokensPage,
        ...(guard ? { canActivate: [guard], runGuardsAndResolvers: 'always' as const } : {}),
      },
    ],
    world,
  );
  const router = TestBed.inject(Router);
  await harness.navigateByUrl(`${PAGE_URL}?project=project-a&cursor=a-page-2`);
  await until(harness, () => document.querySelector('[data-api-next]') !== null);
  const store = harness.routeDebugElement?.injector.get(ApiTokensStore);
  if (!store) throw new Error('Missing mounted token store');
  const navigate = vi.spyOn(router, 'navigate');
  return { list, world, harness, router, store, navigate };
}

function expectNoForeignCursor(
  list: Mock<(projectId?: string, cursor?: string) => Promise<ApiTokenListResponse>>,
): void {
  expect(
    list.mock.calls.filter(
      ([projectId, cursor]) => projectId === 'project-b' && cursor?.startsWith('a-'),
    ),
  ).toEqual([]);
}

describe('ApiTokensPage navigation boundaries', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('keeps the original URL and store through two consecutive rejected Next clicks', async () => {
    let rejected = 0;
    const h = await boot(() => {
      const rejecting =
        TestBed.inject(Router).getCurrentNavigation()?.extractedUrl.queryParams['cursor'] ===
        'a-page-3';
      if (rejecting) rejected += 1;
      return !rejecting;
    });
    const original = h.router.url;
    for (let attempt = 1; attempt <= 2; attempt += 1) {
      clickNext();
      await until(h.harness, () => rejected === attempt && h.store.phase() === 'success');
      await settleRouter(h.harness);
      expect(h.store.cursor()).toBe('a-page-2');
      expect(h.router.url).toBe(original);
      expect(h.navigate).toHaveBeenCalledTimes(attempt);
    }
  });

  it('starts the new project on page one after rejecting Next', async () => {
    const held = new HeldTransition();
    const h = await boot(held.guard);
    held.hold();
    clickNext();
    await held.waitUntilHeld();
    held.release(false);
    await settleRouter(h.harness);
    chooseProject('project-b');
    await until(
      h.harness,
      () => h.router.parseUrl(h.router.url).queryParams['project'] === 'project-b',
    );
    await settleRouter(h.harness);
    expect(h.store.cursor()).toBeUndefined();
    expect(h.router.parseUrl(h.router.url).queryParams['cursor']).toBeUndefined();
    expect(h.navigate).toHaveBeenCalledTimes(2);
    expectNoForeignCursor(h.list);
  });

  it('lands Next normally and still lands it after a previous rejection', async () => {
    const held = new HeldTransition();
    const h = await boot(held.guard);
    held.hold();
    clickNext();
    await held.waitUntilHeld();
    held.release(false);
    await settleRouter(h.harness);
    await until(h.harness, () => h.store.phase() === 'success');
    clickNext();
    await until(
      h.harness,
      () => h.router.parseUrl(h.router.url).queryParams['cursor'] === 'a-page-3',
    );
    await settleRouter(h.harness);
    expect(h.store.cursor()).toBe('a-page-3');
    expect(h.navigate).toHaveBeenCalledTimes(2);
  });

  it.each(['picker', 'route', 'project-list-fallback'] as const)(
    'never sends the old cursor to a new project via %s while Next is pending',
    async (path) => {
      const held = new HeldTransition();
      const h = await boot(held.guard);
      held.hold();
      clickNext();
      await held.waitUntilHeld();
      if (path === 'picker') chooseProject('project-b');
      else if (path === 'route') await h.harness.navigateByUrl(`${PAGE_URL}?project=project-b`);
      else h.world.listed.set([project('project-b')]);
      await until(
        h.harness,
        () => h.store.selectedProject() === 'project-b' && h.store.phase() === 'success',
      );
      held.release(false);
      await settleRouter(h.harness);
      expect(h.store.selectedProject()).toBe('project-b');
      expect(h.store.cursor()).toBeUndefined();
      expect(h.router.parseUrl(h.router.url).queryParams['project']).toBe('project-b');
      expect(h.router.parseUrl(h.router.url).queryParams['cursor']).toBeUndefined();
      expectNoForeignCursor(h.list);
    },
  );

  it('lands the new project Next cursor after a normal picker switch', async () => {
    const h = await boot();
    chooseProject('project-b');
    await until(
      h.harness,
      () =>
        h.router.parseUrl(h.router.url).queryParams['project'] === 'project-b' &&
        h.store.phase() === 'success',
    );
    expect(h.store.cursor()).toBeUndefined();
    clickNext();
    await until(
      h.harness,
      () => h.router.parseUrl(h.router.url).queryParams['cursor'] === 'b-page-2',
    );
    await settleRouter(h.harness);
    expect(h.store.cursor()).toBe('b-page-2');
    expectNoForeignCursor(h.list);
  });

  it('never restores the old project cursor when Next is clicked in the new project before the old guard rejects', async () => {
    const held = new HeldTransition();
    const h = await boot(held.guard);
    held.hold();
    clickNext();
    await held.waitUntilHeld();
    chooseProject('project-b');
    await until(
      h.harness,
      () => h.store.selectedProject() === 'project-b' && h.store.phase() === 'success',
    );
    expect(h.store.cursor()).toBeUndefined();
    clickNext();
    await until(h.harness, () => h.store.cursor() === 'b-page-2');
    held.release(false);
    await settleRouter(h.harness);
    await until(h.harness, () => h.store.phase() === 'success');
    expectNoForeignCursor(h.list);
    expect(h.store.cursor()).not.toBe('a-page-2');
    expect(h.router.parseUrl(h.router.url).queryParams['cursor']).not.toBe('a-page-2');
  });
});
