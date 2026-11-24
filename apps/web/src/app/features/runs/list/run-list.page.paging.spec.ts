import { BreakpointObserver } from '@angular/cdk/layout';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import type { ApiError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import {
  pageStore,
  emptyFilters,
  runCreatePageProviders,
} from '../actions/create/create-run-list.test-support';
import { RunListPage } from './run-list.page';
import { RunListStore } from './run-list.store';

const failure: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'run_list_unavailable',
  message: 'Run list is unavailable.',
  requestId: 'request-1',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
};

class RunPagerHarness extends ComponentHarness {
  static hostSelector = 'app-data-table-pager';
  async press(label: string): Promise<void> {
    const direction = label === 'Next' ? 'next' : 'previous';
    await (await this.locatorFor(`[data-page="${direction}"]`)()).click();
  }
  async chooseSize(size: number): Promise<void> {
    const trigger = await this.locatorFor('hlm-select-trigger button')();
    await trigger.click();
    await trigger.focus();
    await (
      await this.documentRootLocatorFactory().locatorFor(`[role="option"][data-value="${size}"]`)()
    ).click();
  }
}

class RunListPageHarness extends ComponentHarness {
  static hostSelector = 'app-run-list-page';
  readonly retry = this.locatorFor('[data-run-paging-error] button');
  readonly reload = this.locatorFor('app-error-pattern button');
  readonly clear = this.locatorFor('[data-run-clear]');
}

describe('RunListPage paging recovery', () => {
  provideLayoutTestStubs();
  let store = pageStore();
  let query = new BehaviorSubject(convertToParamMap({}));
  let navigate = vi.fn<Router['navigate']>().mockResolvedValue(true);

  beforeEach(() => {
    store = pageStore();
    query = new BehaviorSubject(convertToParamMap({}));
    navigate = vi.fn<Router['navigate']>().mockResolvedValue(true);
    const route = {
      pathFromRoot: [
        { paramMap: new BehaviorSubject(convertToParamMap({ projectId: 'project-1' })) },
      ],
      queryParamMap: query,
      snapshot: undefined as unknown,
    };
    TestBed.configureTestingModule({
      imports: [RunListPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        ...runCreatePageProviders().providers,
        { provide: ActivatedRoute, useValue: route },
        {
          provide: BreakpointObserver,
          useValue: { observe: () => new BehaviorSubject({ matches: true, breakpoints: {} }) },
        },
      ],
    });
    TestBed.overrideComponent(RunListPage, {
      set: { providers: [{ provide: RunListStore, useValue: store }] },
    });
    const router = TestBed.inject(Router);
    route.snapshot = router.routerState.snapshot.root;
    vi.spyOn(router, 'navigate').mockImplementation((commands, extras) =>
      navigate(commands, extras),
    );
  });

  afterEach(() => TestBed.resetTestingModule());

  async function render() {
    const fixture = TestBed.createComponent(RunListPage);
    await fixture.whenStable();
    return { fixture };
  }
  it.each([
    { retryable: false, retryAfterSeconds: null },
    { retryable: true, retryAfterSeconds: 60 },
  ])('keeps Previous and Next usable when retry is blocked by %j', async (eligibility) => {
    store.page.set(2);
    store.hasNext.set(true);
    store.pagingError.set({ ...failure, ...eligibility });
    const { fixture } = await render();
    if (eligibility.retryable) {
      const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunListPageHarness);
      const retry = await page.retry();
      expect(await retry.getAttribute('aria-disabled')).toBe('true');
      await retry.click();
    }
    const pager = await TestbedHarnessEnvironment.loader(fixture).getHarness(RunPagerHarness);
    await pager.press('Previous');
    expect(store.previous).toHaveBeenCalledTimes(1);
    await pager.press('Next');
    expect(store.next).toHaveBeenCalledTimes(1);
    expect(store.retryPage).not.toHaveBeenCalled();
  });

  it.each(['next', 'previous', 'retryPage'] as const)(
    'does not update the URL or focus after destroyed %s settles',
    async (direction) => {
      store.page.set(2);
      store.hasNext.set(true);
      if (direction === 'retryPage') store.pagingError.set(failure);
      let settle: () => void = () => undefined;
      const request = new Promise<void>((resolve) => {
        settle = resolve;
      });
      store[direction].mockReturnValue(request);
      const { fixture } = await render();
      const loader = TestbedHarnessEnvironment.loader(fixture);
      if (direction === 'retryPage') {
        const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunListPageHarness);
        const retry = await page.retry();
        await retry.focus();
        await retry.click();
      } else {
        const pager = await loader.getHarness(RunPagerHarness);
        await pager.press(direction === 'next' ? 'Next' : 'Previous');
      }
      expect(store[direction]).toHaveBeenCalledTimes(1);
      fixture.destroy();
      const focus = vi.spyOn(HTMLElement.prototype, 'focus');
      settle();
      await request;
      await Promise.resolve();
      expect(navigate).not.toHaveBeenCalled();
      expect(focus).not.toHaveBeenCalled();
      focus.mockRestore();
    },
  );

  it('does not restore focus when a size request settles after destruction', async () => {
    let settle: () => void = () => undefined;
    const request = new Promise<void>((resolve) => {
      settle = resolve;
    });
    store.setPageSize.mockReturnValue(request);
    const { fixture } = await render();
    store.hasNext.set(true);
    const pager = await TestbedHarnessEnvironment.loader(fixture).getHarness(RunPagerHarness);
    await pager.chooseSize(50);
    expect(store.setPageSize).toHaveBeenCalledWith(50);
    const calls = navigate.mock.calls.length;
    fixture.destroy();
    const focus = vi.spyOn(HTMLElement.prototype, 'focus');
    settle();
    await request;
    await Promise.resolve();
    expect(navigate).toHaveBeenCalledTimes(calls);
    expect(focus).not.toHaveBeenCalled();
    focus.mockRestore();
  });

  it.each(['clear', 'reload'] as const)(
    'drops an invalid restored cursor from the URL on explicit %s',
    async (action) => {
      query.next(convertToParamMap({ cursor: 'expired', pageSize: '50' }));
      store.status.set('error');
      store.loadError.set(failure);
      if (action === 'reload') {
        store.reload.mockImplementation(() => {
          store.init('project-1', { pageSize: '50' });
        });
      }
      const { fixture } = await render();
      const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunListPageHarness);
      await (await page[action]()).click();
      expect(navigate).toHaveBeenLastCalledWith(
        [],
        expect.objectContaining({
          queryParams: expect.objectContaining({ cursor: null, pageSize: '50' }) as unknown,
        }),
      );
      if (action === 'reload') expect(store.reload).toHaveBeenCalledTimes(1);
      else expect(store.setFilters).toHaveBeenCalledWith(emptyFilters);
    },
  );

  it('updates the URL after successful live navigation', async () => {
    store.hasNext.set(true);
    store.next.mockImplementation(async () => {
      await Promise.resolve();
      store.init('project-1', { cursor: 'following', pageSize: '50' });
    });
    const { fixture } = await render();
    const pager = await TestbedHarnessEnvironment.loader(fixture).getHarness(RunPagerHarness);
    await pager.press('Next');
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: expect.objectContaining({ cursor: 'following', pageSize: '50' }) as unknown,
      }),
    );
  });
});
