import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, type ParamMap } from '@angular/router';
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
  let navigate = vi.fn().mockResolvedValue(true);
  beforeEach(() => {
    store = pageStore();
    query = new BehaviorSubject(convertToParamMap({}));
    project = new BehaviorSubject(convertToParamMap({ projectId: 'project-1' }));
    navigate = vi.fn().mockResolvedValue(true);
    const route = {
      pathFromRoot: [
        { paramMap: project },
        { paramMap: new BehaviorSubject(convertToParamMap({})) },
      ],
      queryParamMap: query,
    };
    TestBed.configureTestingModule({
      imports: [RunListPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ActivatedRoute, useValue: route },
        { provide: Router, useValue: { navigate } },
      ],
    });
    TestBed.overrideComponent(RunListPage, {
      set: { providers: [{ provide: RunListStore, useValue: store }] },
    });
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
    expect(root.querySelector('[data-run-loaded-count]')?.textContent).toContain('1 loaded runs');
    expect(root.querySelector('[name="completeness"]')).toBeNull();
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
    expect(root.querySelector<HTMLButtonElement>('[data-run-load-more]')!.disabled).toBe(true);
  });
  it('renders the shared loading, generic error, forbidden and not-found patterns without HTML injection', async () => {
    store.status.set('loading');
    const { fixture, root } = await render();
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
    store.status.set('error');
    store.loadError.set(failure);
    await fixture.whenStable();
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    expect(root.textContent).toContain(failure.message);
    expect(root.querySelector('b')).toBeNull();
    root.querySelector<HTMLButtonElement>('app-error-pattern button')!.click();
    expect(store.reload).toHaveBeenCalledTimes(1);
    for (const [kind, status, selector] of [
      ['forbidden', 403, 'app-forbidden-pattern'],
      ['not_found', 404, 'app-not-found-pattern'],
    ] as const) {
      store.loadError.set({ ...failure, kind, status, retryable: false });
      await fixture.whenStable();
      expect(root.querySelector(selector)).not.toBeNull();
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
