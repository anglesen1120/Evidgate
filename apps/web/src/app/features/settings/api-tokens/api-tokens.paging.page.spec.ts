import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { mount, refusal, ROW } from './api-tokens.test-support';

class TokenPagingHarness extends ComponentHarness {
  static hostSelector = 'app-api-tokens-page';
  private readonly summary = this.locatorFor('app-data-table-pager [role="status"]');
  private readonly nextButton = this.locatorFor('app-data-table-pager [data-page="next"]');
  private readonly previousButton = this.locatorFor('app-data-table-pager [data-page="previous"]');
  private readonly sizeButton = this.locatorFor('app-data-table-pager hlm-select-trigger button');

  async text(): Promise<string> { return (await this.summary()).text(); }
  async next(): Promise<void> { await (await this.nextButton()).click(); }
  async previous(): Promise<void> { await (await this.previousButton()).click(); }
  async chooseSize(size: number): Promise<void> {
    await (await this.sizeButton()).click();
    const options = await this.documentRootLocatorFactory().locatorForAll('[role="option"]')();
    for (const option of options) {
      if ((await option.text()).trim() === String(size)) {
        await option.click();
        return;
      }
    }
    throw new Error('Missing page-size option');
  }
}

describe('API tokens shared pager', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows honest page numbers, navigates back and retains URL ownership', async () => {
    const h = mount(undefined, { query: { project: 'project-1', marker: 'keep' } });
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    await h.settle();
    const pager = await TestbedHarnessEnvironment.loader(h.fixture).getHarness(TokenPagingHarness);
    expect(await pager.text()).toBe('Page 1');
    await pager.next();
    await h.settle();
    expect(await pager.text()).toBe('Page 2');
    expect(h.navigate).toHaveBeenLastCalledWith([], expect.objectContaining({
      queryParams: { project: 'project-1', cursor: 'middle' },
      queryParamsHandling: 'merge', replaceUrl: false,
    }));
    await pager.previous();
    await h.settle();
    expect(await pager.text()).toBe('Page 1');
    expect(h.navigate).toHaveBeenLastCalledWith([], expect.objectContaining({
      queryParams: { project: 'project-1', cursor: null }, replaceUrl: false,
    }));
  });

  it('changes the request size through Helm and resets the cursor', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    await h.settle();
    const pager = await TestbedHarnessEnvironment.loader(h.fixture).getHarness(TokenPagingHarness);
    await pager.next();
    await h.settle();
    await pager.chooseSize(50);
    await h.settle();
    expect(await pager.text()).toBe('Page 1');
    expect(h.list).toHaveBeenLastCalledWith('project-1', undefined, expect.any(AbortSignal), 50);
  });

  it('keeps rows and the displayed page during a later failure with visible recovery', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    await h.settle();
    const pager = await TestbedHarnessEnvironment.loader(h.fixture).getHarness(TokenPagingHarness);
    h.list.mockRejectedValueOnce(refusal('network'));
    await pager.next();
    await h.settle();
    expect(await pager.text()).toBe('Page 1');
    expect(h.store.rows()).toEqual([ROW]);
    expect(h.root.textContent).toContain(ROW.name);
    expect(h.root.querySelector('app-error-pattern')).not.toBeNull();
  });
});
