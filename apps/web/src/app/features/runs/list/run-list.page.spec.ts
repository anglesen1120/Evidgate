import { BreakpointObserver } from '@angular/cdk/layout';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import {
  ActivatedRoute,
  Router,
  convertToParamMap,
  provideRouter,
  type ParamMap,
} from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import type { ApiError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { HeaderHarness } from '../../../shared/ui/page-header/header.harness';
import { RunListPage } from './run-list.page';
import { RunListStore } from './run-list.store';
import {
  emptyFilters,
  pageStore,
  run,
  runCreatePageProviders,
} from '../actions/create/create-run-list.test-support';

const failure: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'run_list_unavailable',
  message: 'Run list <b>is unavailable</b>.',
  requestId: 'request-1',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
};

class RunPagerHarness extends ComponentHarness {
  static hostSelector = 'app-data-table-pager';
  private readonly buttons = this.locatorForAll('button');
  async press(label: string): Promise<void> {
    for (const button of await this.buttons()) {
      if ((await button.text()).includes(label)) {
        await button.click();
        return;
      }
    }
    throw new Error(`Missing pager action ${label}`);
  }
}

class RunListPageHarness extends ComponentHarness {
  static hostSelector = 'app-run-list-page';
  readonly view = this.locatorFor('button[aria-haspopup="dialog"]');
  readonly dialog = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  readonly close = this.documentRootLocatorFactory().locatorFor('[role="dialog"] [hlmDialogClose]');
}

describe('RunListPage', () => {
  provideLayoutTestStubs();
  let store = pageStore();
  let query: BehaviorSubject<ParamMap>;
  let project: BehaviorSubject<ParamMap>;
  let navigate = vi.fn<Router['navigate']>().mockResolvedValue(true);
  const desktop = new BehaviorSubject({ matches: true, breakpoints: {} });
  beforeEach(() => {
    desktop.next({ matches: true, breakpoints: {} });
    store = pageStore();
    query = new BehaviorSubject(convertToParamMap({}));
    project = new BehaviorSubject(convertToParamMap({ projectId: 'project-1' }));
    navigate = vi.fn<Router['navigate']>().mockResolvedValue(true);
    const route = {
      pathFromRoot: [
        { paramMap: project },
        { paramMap: new BehaviorSubject(convertToParamMap({})) },
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
        { provide: BreakpointObserver, useValue: { observe: () => desktop.asObservable() } },
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
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });
  async function render() {
    const fixture = TestBed.createComponent(RunListPage);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  function change(root: HTMLElement, name: string, value: string) {
    const field = root.querySelector<HTMLInputElement>(`input[name="${name}"]`);
    if (!field) throw new Error(`Missing input ${name}`);
    field.value = value;
    field.dispatchEvent(new Event('input', { bubbles: true }));
  }
  async function choose(
    fixture: ComponentFixture<RunListPage>,
    root: HTMLElement,
    name: string,
    value: string,
  ) {
    root.querySelector<HTMLButtonElement>(`[name="${name}"] button`)?.click();
    await fixture.whenStable();
    const option = document.querySelector<HTMLElement>(`[role="option"][data-value="${value}"]`);
    if (!option) throw new Error(`Missing ${name} option ${value}`);
    option.click();
    await fixture.whenStable();
  }
  it('collapses mobile filters, counts applied filters and expands at the desktop breakpoint', async () => {
    desktop.next({ matches: false, breakpoints: {} });
    const { fixture, root } = await render();
    const disclosure = root.querySelector<HTMLDetailsElement>('[data-run-filter-disclosure]');
    expect(disclosure?.open).toBe(false);
    expect(root.querySelector('summary')?.textContent).toContain('Active filters: 0');
    root.querySelector('summary')?.click();
    expect(disclosure?.open).toBe(true);
    query.next(convertToParamMap({ decision: 'PASS', ref: 'main' }));
    await fixture.whenStable();
    expect(root.querySelector('summary')?.textContent).toContain('Active filters: 2');
    root.querySelector('summary')?.click();
    desktop.next({ matches: true, breakpoints: {} });
    await fixture.whenStable();
    expect(disclosure?.open).toBe(true);
  });
  it('initializes from ancestor project context and URL filters with one page h1', async () => {
    query.next(
      convertToParamMap({
        state: 'complete',
        decision: 'PASS',
        scope: 'rerun',
        ref: 'release/việt',
      }),
    );
    const { root } = await render();
    expect(store.init).toHaveBeenCalledWith('project-1', {
      state: 'complete',
      decision: 'PASS',
      scope: 'rerun',
      ref: 'release/việt',
      cursor: undefined,
      pageSize: undefined,
    });
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect((root.querySelector('#run-ref') as HTMLInputElement).value).toBe('release/việt');
    // The URL narrows the list, so the count says so and the element reports the filtered mode.
    const loaded = root.querySelector('[data-run-loaded-count]')!;
    expect(loaded.textContent).toContain('On this page before filtering by scope: 1');
    expect(loaded.getAttribute('data-run-loaded-filtered')).toBe('true');
    expect(root.querySelector('[name="completeness"]')).toBeNull();
  });
  it('summarizes only loaded runs, keeping stored FAIL separate from collection state', async () => {
    store.rows.set([
      { ...run, state: 'collecting', decision: null },
      { ...run, run_id: 'second', state: 'complete', decision: 'FAIL', rerun_label: 'retry' },
      { ...run, run_id: 'third', state: 'incomplete', decision: 'INCONCLUSIVE' },
    ]);
    const { fixture, root } = await render();
    const header = await TestbedHarnessEnvironment.loader(fixture).getHarness(HeaderHarness);
    expect(await header.getDescription()).toBe('Runs on this page: 3 · Active filters: 0');
    const metrics = root.querySelector('[data-run-metrics]')!;
    const values = [...metrics.querySelectorAll('dd > app-number-value')];
    expect(values.map((cell) => cell.textContent?.trim())).toEqual(['3', '1', '1', '1']);
    expect(root.querySelector('[data-run-loaded-count]')?.textContent).toContain('Showing 3');
    root.querySelector<HTMLButtonElement>('#run-state')?.click();
    await fixture.whenStable();
    const running = document.querySelector('[role="option"][data-value="running"]');
    expect(running?.textContent?.trim()).toBe('Running');
    root.querySelector<HTMLButtonElement>('#run-state')?.click();
    store.status.set('loading');
    await fixture.whenStable();
    expect(root.querySelector('[data-run-metrics]')).toBeNull();
    store.status.set('error');
    store.loadError.set(failure);
    await fixture.whenStable();
    expect(root.querySelector('[data-run-metrics]')).toBeNull();
    expect(await header.getDescription()).not.toContain('Runs on this page:');
  });
  it('offers not evaluated separately from verdicts and explains server filtering', async () => {
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('#run-decision')?.click();
    await fixture.whenStable();
    expect(
      document.querySelector('[role="option"][data-value="pending"]')?.textContent?.trim(),
    ).toBe('Not evaluated');
    expect(document.querySelector('[role="listbox"]')?.getAttribute('aria-label')).toBe('Decision');
    root.querySelector<HTMLButtonElement>('#run-decision')?.click();
    await fixture.whenStable();
    expect(root.querySelector('#run-decision')?.getAttribute('aria-describedby')).toBe(
      'run-decision-hint',
    );
    expect(root.querySelector('#run-decision-hint')?.textContent).toContain(
      'Not evaluated means no live evaluation yet.',
    );
    expect(root.querySelector('[data-run-decision]')?.textContent).toContain('Not evaluated');
  });
  it('uses the responsive page title scale rather than a section-heading size', async () => {
    const { root } = await render();
    const title = root.querySelector('h1');
    expect(title).not.toBeNull();
    if (title) {
      expect(title.getAttribute('data-title-size')).toBe('page');
      expect(title.textContent?.replace(/\s+/g, ' ').trim()).toBe('Runs');
    }
  });
  it('restores a pending filter from the URL without rewriting its context', async () => {
    query.next(convertToParamMap({ decision: 'pending', ref: 'main' }));
    const { root } = await render();
    expect(root.querySelector('#run-decision hlm-select-value')?.textContent).toContain(
      'Not evaluated',
    );
    expect(navigate).not.toHaveBeenCalled();
    expect(store.init).toHaveBeenCalledWith(
      'project-1',
      expect.objectContaining({
        decision: 'pending',
        ref: 'main',
      }),
    );
  });
  it.each([{}, { state: 'complete', decision: 'PASS', ref: 'main' }])(
    'does not describe a server result count as locally filtered: %j',
    async (params) => {
      query.next(convertToParamMap(params));
      const { root } = await render();
      const loaded = root.querySelector('[data-run-loaded-count]');
      expect(loaded?.textContent).toContain('Returned on this page: 1');
      expect(loaded?.getAttribute('data-run-loaded-filtered')).toBe('false');
    },
  );
  it('marks a refused ref name as invalid and explains it inline', async () => {
    const { fixture, root } = await render();
    const refInput = root.querySelector<HTMLInputElement>('#run-ref')!;
    expect(refInput.getAttribute('aria-invalid')).toBeNull();
    expect(root.querySelector('#run-ref-error')).toBeNull();

    // The store refuses the value: the filter was not applied, and the page must say so rather
    // than leave the field looking accepted. A 256-code-point name is the over-long case; the
    // input's `maxlength` stops it arriving by typing or pasting, not by any other route.
    const overLong = 'x'.repeat(256);
    store.refRejected.set(true);
    change(root, 'refName', overLong);
    await fixture.whenStable();
    expect(refInput.value).toBe(overLong);
    expect(refInput.maxLength).toBe(255);
    expect(refInput.getAttribute('aria-invalid')).toBe('true');
    expect(refInput.getAttribute('aria-describedby')).toBe('run-ref-error');
    const error = root.querySelector('#run-ref-error')!;
    expect(error.textContent).toContain('not applied');
    expect(error.textContent).toContain('255');

    // A submission keeps the typed value in the field, so the operator can correct it.
    root
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    expect(store.setFilters).toHaveBeenLastCalledWith(
      expect.objectContaining({ refName: overLong }),
    );
    expect(refInput.value).toBe(overLong);

    // Accepting the next value clears both the message and the invalid state.
    store.refRejected.set(false);
    await fixture.whenStable();
    expect(root.querySelector('#run-ref-error')).toBeNull();
    expect(refInput.getAttribute('aria-invalid')).toBeNull();
    expect(refInput.getAttribute('aria-describedby')).toBeNull();
  });
  it('applies filters to the store and merges URL state while clearing obsolete filter keys', async () => {
    query.next(convertToParamMap({ decision: 'pending' }));
    const { fixture, root } = await render();
    await choose(fixture, root, 'state', 'running');
    await choose(fixture, root, 'scope', 'original');
    await choose(fixture, root, 'decision', 'PASS');
    expect(store.setFilters).not.toHaveBeenCalled();
    change(root, 'refName', 'main');
    root
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    expect(store.setFilters).toHaveBeenLastCalledWith({
      state: 'running',
      decision: 'PASS',
      scope: 'original',
      refName: 'main',
    });
    expect(root.querySelector('summary')?.textContent).toContain('Active filters: 4');
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParamsHandling: 'merge',
        queryParams: {
          state: 'running',
          decision: 'PASS',
          scope: 'original',
          ref: 'main',
          cursor: null,
          pageSize: null,
        },
      }),
    );
    const calls = store.init.mock.calls.length;
    query.next(
      convertToParamMap({ state: 'running', decision: 'PASS', scope: 'original', ref: 'main' }),
    );
    await fixture.whenStable();
    expect(store.init).toHaveBeenCalledTimes(calls);
    root.querySelector<HTMLButtonElement>('[data-run-clear]')!.click();
    await fixture.whenStable();
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: {
          state: null,
          decision: null,
          scope: null,
          ref: null,
          cursor: null,
          pageSize: null,
        },
      }),
    );
    expect(root.querySelector('summary')?.textContent).toContain('Active filters: 0');
  });
  it('restores cursor and page size from the URL without walking earlier pages', async () => {
    query.next(convertToParamMap({ cursor: 'opaque+/=', pageSize: '100', decision: 'FAIL' }));
    const { root } = await render();
    expect(store.init).toHaveBeenCalledTimes(1);
    expect(store.init).toHaveBeenLastCalledWith(
      'project-1',
      expect.objectContaining({
        cursor: 'opaque+/=',
        pageSize: '100',
        decision: 'FAIL',
      }),
    );
    expect(store.next).not.toHaveBeenCalled();
    expect(root.querySelector('app-data-table-pager')?.textContent).toContain('100');
    expect(navigate).not.toHaveBeenCalled();
  });
  it('restores back/forward query changes and reloads when the project changes', async () => {
    const { fixture, root } = await render();
    query.next(convertToParamMap({ decision: 'FAIL' }));
    await fixture.whenStable();
    expect(root.querySelector('#run-decision hlm-select-value')?.textContent).toContain('FAIL');
    query.next(convertToParamMap({}));
    await fixture.whenStable();
    expect(root.querySelector('#run-decision hlm-select-value')?.textContent).toContain('All');
    project.next(convertToParamMap({ projectId: 'project-2' }));
    await fixture.whenStable();
    expect(store.init).toHaveBeenLastCalledWith('project-2', {
      state: undefined,
      decision: undefined,
      scope: undefined,
      ref: undefined,
      cursor: undefined,
      pageSize: undefined,
    });
  });
  it('keeps Next reachable when no rows on this page match local filters', async () => {
    store.rows.set([]);
    store.visibleRows = signal(store.rows());
    store.isEmpty = computed(() => store.visibleRows().length === 0);
    const { fixture, root } = await render();
    expect(root.textContent).toContain('No runs recorded');
    store.filters.set({ ...emptyFilters, scope: 'rerun' });
    store.rows.set([run]);
    store.hasNext.set(true);
    await fixture.whenStable();
    expect(root.textContent).toContain('No matching runs on this page');
    expect(root.textContent).toContain('Check another page if available');
    const pager = await TestbedHarnessEnvironment.loader(fixture).getHarness(RunPagerHarness);
    await pager.press('Next');
    expect(store.next).toHaveBeenCalledTimes(1);
  });
  it.each(['', 'rerun'])('does not blame scope %j for an empty server response', async (scope) => {
    query.next(convertToParamMap({ state: 'queued', ref: 'main', scope }));
    store.rows.set([]);
    const { root } = await render();
    const empty = root.querySelector('app-empty-pattern');
    expect(empty?.textContent).toContain('No runs on this page');
    expect(empty?.textContent).toContain('Change or clear the filters');
    expect(empty?.textContent).not.toContain('selected run scope');
  });

  it('keeps pager focus through pending and terminal-page results', async () => {
    store.hasNext.set(true);
    const { fixture, root } = await render();
    const pager = await TestbedHarnessEnvironment.loader(fixture).getHarness(RunPagerHarness);
    const next = [...root.querySelectorAll<HTMLButtonElement>('app-data-table-pager button')].find(
      (button) => button.textContent?.includes('Next'),
    );
    next?.focus();
    store.next.mockImplementation(async () => {
      store.busy.set(true);
      await Promise.resolve();
      store.page.set(2);
      store.hasNext.set(false);
      store.busy.set(false);
    });
    await pager.press('Next');
    await fixture.whenStable();
    expect(document.activeElement).toBe(next);
    expect(next?.getAttribute('aria-disabled')).toBe('true');
  });

  it('does not steal focus from another control after navigation', async () => {
    store.hasNext.set(true);
    const { fixture, root } = await render();
    const clear = root.querySelector<HTMLButtonElement>('[data-run-clear]');
    clear?.focus();
    const pager = await TestbedHarnessEnvironment.loader(fixture).getHarness(RunPagerHarness);
    await pager.press('Next');
    expect(document.activeElement).toBe(clear);
  });

  it('preserves page and page size while a row View dialog opens and closes', async () => {
    store.page.set(3);
    store.pageSize.set(50);
    const { fixture } = await render();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunListPageHarness);
    await (await page.view()).click();
    expect(await page.dialog()).not.toBeNull();
    await (await page.close()).click();
    expect(await page.dialog()).toBeNull();
    expect(store.page()).toBe(3);
    expect(store.pageSize()).toBe(50);
    expect(store.init).toHaveBeenCalledTimes(1);
    expect(store.next).not.toHaveBeenCalled();
  });

  it('renders exactly one error surface per failure kind, with no duplicate page banner', async () => {
    store.status.set('loading');
    const { fixture, root } = await render();
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
    store.status.set('error');
    store.loadError.set(failure);
    await fixture.whenStable();
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    // The generic pattern already announces the failure and carries its message and code, so the
    // page adds nothing beside it: one alert, one copy of the text, no injection.
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(1);
    expect(root.textContent?.split(failure.message)).toHaveLength(2);
    expect(root.querySelector('b')).toBeNull();
    expect(root.querySelector('.banner.failure')).toBeNull();
    root.querySelector<HTMLButtonElement>('app-error-pattern button')!.click();
    expect(store.reload).toHaveBeenCalledTimes(1);
    for (const [kind, status, selector] of [
      ['forbidden', 403, 'app-forbidden-pattern'],
      ['not_found', 404, 'app-not-found-pattern'],
      ['rate_limited', 429, 'app-retry-after'],
      ['unavailable', 503, 'app-retry-after'],
    ] as const) {
      store.loadError.set({
        ...failure,
        kind,
        status,
        retryAfterSeconds: kind === 'unavailable' ? 30 : null,
        retryable: kind === 'unavailable',
      });
      await fixture.whenStable();
      // The shared pattern is the single surface for the kind; the page renders no second alert.
      expect(root.querySelector(selector)).not.toBeNull();
      expect(root.querySelectorAll('[role="alert"]')).toHaveLength(0);
      expect(root.querySelector('.banner.failure')).toBeNull();
    }
  });
  it('retains loaded rows on paging failure and allows only an eligible retry', async () => {
    store.hasNext.set(true);
    store.pagingError.set(failure);
    const { fixture, root } = await render();
    expect(root.querySelectorAll('[data-run-row]')).toHaveLength(1);
    expect(root.querySelector('[data-run-paging-error]')?.textContent).toContain(failure.code);
    root.querySelector<HTMLButtonElement>('[data-run-paging-error] button')!.click();
    expect(store.retryPage).toHaveBeenCalledTimes(1);
    store.pagingError.set({ ...failure, kind: 'forbidden', status: 403, retryable: false });
    await fixture.whenStable();
    expect(root.querySelector('[data-run-paging-error] button')).toBeNull();
  });
});
