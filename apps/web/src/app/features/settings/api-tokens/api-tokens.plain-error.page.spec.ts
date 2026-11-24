import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { ApiTokensPage } from './api-tokens.page';
import { ApiTokensStore } from './api-tokens.store';
import { createWorld, mountAt, project, ROW, until } from './api-tokens.test-support';

class TokenPagingHarness extends ComponentHarness {
  static hostSelector = 'app-api-tokens-page';

  async next(): Promise<void> {
    await (await this.locatorFor('app-data-table-pager [data-page="next"]')()).click();
  }

  async retry(): Promise<void> {
    const button = await this.locatorFor('app-error-pattern button')();
    expect(await button.getProperty<boolean>('disabled')).toBe(false);
    await button.click();
  }

  async errorText(): Promise<string | null> {
    const error = await this.locatorForOptional('app-error-pattern')();
    return error ? error.text() : null;
  }

  async text(): Promise<string> {
    return (await this.host()).text();
  }
}

describe('ApiTokensPage plain later-page errors', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('keeps metadata and offers a safe Retry without changing URL scope or unrelated parameters', async () => {
    const world = createWorld([project('project-1')]);
    const list = vi.mocked(world.list);
    list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    const harness = await mountAt(
      [{ path: 'orgs/:orgId/settings/api-tokens', component: ApiTokensPage }],
      world,
    );
    await harness.navigateByUrl('/orgs/org-1/settings/api-tokens?project=project-1&marker=keep');
    const store = harness.routeDebugElement?.injector.get(ApiTokensStore);
    if (!store) throw new Error('The page did not mount');
    await until(harness, () => store.phase() === 'success');
    const page = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      TokenPagingHarness,
    );
    list.mockRejectedValueOnce(new Error('private adapter request details'));
    await page.next();
    await until(harness, () => store.loadError() !== null);
    const router = TestBed.inject(Router);
    await until(harness, () => router.parseUrl(router.url).queryParams['cursor'] === 'middle');
    expect(await page.text()).toContain(ROW.name);
    expect(await page.errorText()).not.toBeNull();
    expect(await page.errorText()).not.toContain('private adapter request details');
    expect(await page.errorText()).toContain('Retry');
    expect(router.parseUrl(router.url).queryParams).toEqual({
      project: 'project-1',
      cursor: 'middle',
      marker: 'keep',
    });
    const failedUrl = router.url;
    list.mockResolvedValueOnce({
      items: [{ ...ROW, name: 'second-page-reader' }],
      next_cursor: null,
    });
    await page.retry();
    await until(harness, () => store.page() === 2 && store.loadError() === null);
    expect(await page.text()).toContain('second-page-reader');
    expect(await page.errorText()).toBeNull();
    expect(router.url).toBe(failedUrl);
    expect(list).toHaveBeenLastCalledWith('project-1', 'middle', expect.any(AbortSignal), 20);
  });
});
