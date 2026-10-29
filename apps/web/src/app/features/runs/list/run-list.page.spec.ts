import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  Router,
  convertToParamMap,
  provideRouter,
  type ParamMap,
} from '@angular/router';
import type { RunSummary } from '@evidgate/sdk';
import { BehaviorSubject } from 'rxjs';
import type { ApiError } from '../../../core/http/api-error';
import { RunListPage } from './run-list.page';
import { RunListStore, type RunListFilters } from './run-list.store';

const emptyFilters: RunListFilters = { state: '', decision: '', scope: '', refName: '' };
const run: RunSummary = {
  run_id: '018f0000-0000-7000-8000-000000000021',
  state: 'complete',
  created_at: '2026-10-27T00:00:00Z',
  ref: {
    kind: 'branch',
    name: 'main',
    pr_number: null,
    head_sha: null,
    base_sha: null,
    trigger_event: 'push',
    is_fork: false,
  },
  unverified_ref: false,
  decision: null,
  completeness: 'complete',
  parent_run_id: null,
  rerun_scope: null,
};
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

function pageStore() {
  const filters = signal<RunListFilters>({ ...emptyFilters });
  const rows = signal<readonly RunSummary[]>([run]);
  return {
    filters,
    rows,
    visibleRows: rows,
    status: signal<'idle' | 'loading' | 'ready' | 'error'>('ready'),
    loadError: signal<ApiError | null>(null),
    pagingError: signal<ApiError | null>(null),
    loadingMore: signal(false),
    hasMore: signal(false),
    browseCapped: signal(false),
    autoLoadCapped: signal(false),
    refRejected: signal(false),
    loadedCount: computed(() => rows().length),
    isEmpty: computed(() => rows().length === 0),
    isFiltered: computed(() => Object.values(filters()).some(Boolean)),
    init: vi.fn((_project: string, query: Record<string, string | undefined>) => {
      filters.set({
        state: (query['state'] ?? '') as RunListFilters['state'],
        decision: (query['decision'] ?? '') as RunListFilters['decision'],
        scope: (query['scope'] ?? '') as RunListFilters['scope'],
        refName: query['ref'] ?? '',
      });
    }),
    setFilters: vi.fn((patch: Partial<RunListFilters>) =>
      filters.update((current) => ({ ...current, ...patch })),
    ),
    toQuery: () =>
      Object.fromEntries(
        Object.entries({
          state: filters().state,
          decision: filters().decision,
          scope: filters().scope,
          ref: filters().refName,
        }).filter(([, value]) => value !== ''),
      ),
    loadMore: vi.fn(),
    reload: vi.fn(),
  };
}

describe('RunListPage', () => {
  let store = pageStore();
  let query: BehaviorSubject<ParamMap>;
  let project: BehaviorSubject<ParamMap>;
  let navigate = vi.fn<Router['navigate']>().mockResolvedValue(true);
  beforeEach(() => {
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
        { provide: ActivatedRoute, useValue: route },
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
    const field = root.querySelector<HTMLInputElement | HTMLSelectElement>(`[name="${name}"]`)!;
    field.value = value;
    field.dispatchEvent(
      new Event(field instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }),
    );
  }
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
    });
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect((root.querySelector('#run-ref') as HTMLInputElement).value).toBe('release/việt');
    // The URL narrows the list, so the count says so and the element reports the filtered mode.
    const loaded = root.querySelector('[data-run-loaded-count]')!;
    expect(loaded.textContent).toContain('Filtered from 1 loaded runs');
    expect(loaded.getAttribute('data-run-loaded-filtered')).toBe('true');
    expect(root.querySelector('[name="completeness"]')).toBeNull();
  });
  it('renders a plain loaded count when nothing is filtered', async () => {
    const { root } = await render();

    const loaded = root.querySelector('[data-run-loaded-count]')!;
    expect(loaded.textContent).toContain('Loaded 1 runs.');
    expect(loaded.textContent).not.toContain('Filtered');
    expect(loaded.getAttribute('data-run-loaded-filtered')).toBe('false');
  });
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
    const { fixture, root } = await render();
    change(root, 'state', 'running');
    change(root, 'decision', 'pending');
    change(root, 'scope', 'original');
    change(root, 'refName', 'main');
    root
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
    expect(store.setFilters).toHaveBeenLastCalledWith({
      state: 'running',
      decision: 'pending',
      scope: 'original',
      refName: 'main',
    });
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParamsHandling: 'merge',
        queryParams: { state: 'running', decision: 'pending', scope: 'original', ref: 'main' },
      }),
    );
    const calls = store.init.mock.calls.length;
    query.next(
      convertToParamMap({ state: 'running', decision: 'pending', scope: 'original', ref: 'main' }),
    );
    await fixture.whenStable();
    expect(store.init).toHaveBeenCalledTimes(calls);
    root.querySelector<HTMLButtonElement>('[data-run-clear]')!.click();
    await fixture.whenStable();
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: { state: null, decision: null, scope: null, ref: null },
      }),
    );
  });
  it('restores back/forward query changes and reloads when the project changes', async () => {
    const { fixture, root } = await render();
    query.next(convertToParamMap({ decision: 'FAIL' }));
    await fixture.whenStable();
    expect((root.querySelector('#run-decision') as HTMLSelectElement).value).toBe('FAIL');
    query.next(convertToParamMap({}));
    await fixture.whenStable();
    expect((root.querySelector('#run-decision') as HTMLSelectElement).value).toBe('');
    project.next(convertToParamMap({ projectId: 'project-2' }));
    await fixture.whenStable();
    expect(store.init).toHaveBeenLastCalledWith('project-2', {
      state: undefined,
      decision: undefined,
      scope: undefined,
      ref: undefined,
    });
  });
  it('distinguishes truly empty from filtered loaded rows and keeps the capped pager reachable', async () => {
    store.rows.set([]);
    const { fixture, root } = await render();
    expect(root.textContent).toContain('No runs recorded');
    store.filters.set({ ...emptyFilters, state: 'queued' });
    store.hasMore.set(true);
    store.autoLoadCapped.set(true);
    await fixture.whenStable();
    expect(root.textContent).toContain('No matching runs in the loaded rows');
    expect(root.textContent).toContain('not the entire project');
    expect(root.querySelector('[data-run-browse-capped]')).not.toBeNull();
    root.querySelector<HTMLButtonElement>('[data-run-load-more]')!.click();
    expect(store.loadMore).toHaveBeenCalledTimes(1);
    store.loadingMore.set(true);
    await fixture.whenStable();
    // The follow it started does not disable the control that holds focus: `disabled` would drop
    // the visitor's place to `body` while the rows were still loading.
    expect(root.querySelector<HTMLButtonElement>('[data-run-load-more]')!.disabled).toBe(false);
  });
  it('moves focus to the first appended row when the follow removes Load more', async () => {
    const appended: RunSummary = {
      ...run,
      run_id: '018f0000-0000-7000-8000-000000000022',
    };
    store.hasMore.set(true);
    const { fixture, root } = await render();
    const more = root.querySelector<HTMLButtonElement>('[data-run-load-more]')!;
    more.focus();
    expect(document.activeElement).toBe(more);
    // The follow appends a page and the cursor ends with it, so the control that carried focus is
    // removed by the render that follows. Its place belongs to the row it brought in.
    store.loadMore.mockImplementation(() => {
      store.loadingMore.set(true);
      store.rows.set([run, appended]);
      store.loadingMore.set(false);
      store.hasMore.set(false);
      return Promise.resolve();
    });
    more.click();
    await fixture.whenStable();
    const rows = root.querySelectorAll('[data-run-row]');
    expect(rows).toHaveLength(2);
    expect(document.activeElement).toBe(rows[1]!.querySelector('a'));
  });

  it('moves focus to the paging retry when the follow replaces Load more with a failure', async () => {
    store.hasMore.set(true);
    const { fixture, root } = await render();
    const more = root.querySelector<HTMLButtonElement>('[data-run-load-more]')!;
    more.focus();
    store.loadMore.mockImplementation(() => {
      store.loadingMore.set(true);
      store.loadingMore.set(false);
      store.pagingError.set(failure);
      return Promise.resolve();
    });
    more.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-run-load-more]')).toBeNull();
    expect(document.activeElement).toBe(root.querySelector('[data-run-paging-error] button'));
  });

  it('keeps the place in the paging region when the retry is still waiting its Retry-After', async () => {
    store.hasMore.set(true);
    const { fixture, root } = await render();
    const more = root.querySelector<HTMLButtonElement>('[data-run-load-more]')!;
    more.focus();
    store.loadMore.mockImplementation(() => {
      store.loadingMore.set(true);
      store.loadingMore.set(false);
      store.pagingError.set({ ...failure, retryAfterSeconds: 30 });
      return Promise.resolve();
    });
    more.click();
    await fixture.whenStable();
    fixture.detectChanges();

    // The retry replaced the button that held focus and is disabled for the wait, so the place
    // moves to the paging region - never to `body`.
    const waiting = root.querySelector<HTMLButtonElement>('[data-run-paging-error] button');
    expect(waiting?.disabled).toBe(true);
    const paging = root.querySelector<HTMLElement>('.pagination');
    expect(document.activeElement).toBe(paging);
    expect(paging?.getAttribute('tabindex')).toBe('-1');
  });

  it('leaves focus alone when a follow it did not own removes Load more', async () => {
    store.hasMore.set(true);
    const { fixture, root } = await render();
    const clear = root.querySelector<HTMLButtonElement>('[data-run-clear]')!;
    clear.focus();
    store.loadMore.mockImplementation(() => {
      store.rows.set([run, { ...run, run_id: '018f0000-0000-7000-8000-000000000023' }]);
      store.hasMore.set(false);
      return Promise.resolve();
    });
    root.querySelector<HTMLButtonElement>('[data-run-load-more]')!.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-run-load-more]')).toBeNull();
    expect(document.activeElement).toBe(clear);
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
    store.hasMore.set(true);
    store.pagingError.set(failure);
    const { fixture, root } = await render();
    expect(root.querySelectorAll('[data-run-row]')).toHaveLength(1);
    expect(root.querySelector('[data-run-paging-error]')?.textContent).toContain(failure.code);
    root.querySelector<HTMLButtonElement>('[data-run-paging-error] button')!.click();
    expect(store.loadMore).toHaveBeenCalledTimes(1);
    store.pagingError.set({ ...failure, kind: 'forbidden', status: 403, retryable: false });
    await fixture.whenStable();
    expect(root.querySelector('[data-run-paging-error] button')).toBeNull();
  });
});
