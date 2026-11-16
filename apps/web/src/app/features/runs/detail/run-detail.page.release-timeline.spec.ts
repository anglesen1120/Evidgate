import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { EvidenceRevisionPage, RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import attemptsOk from '../../../../../../../fixtures/web-api/run-attempts/input.json';
import { ReleasesDataService } from '../../../core/data/releases-data.service';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { RunDataService } from '../data/run-data.service';
import { routes } from '../runs.routes';
import { deferred, pendingBody, refused, run } from './run-detail-store.fixtures';
import { RunDetailPage } from './run-detail.page';

const attemptPage = attemptsOk.body as RunAttemptPage;

describe('RunDetailPage release and timeline regions', () => {
  provideLayoutTestStubs();
  const get = vi.fn<(id: string) => Promise<RunStateDto>>();
  const listAttempts = vi.fn<(id: string, query?: object) => Promise<RunAttemptPage>>();
  const listEvidenceRevisions =
    vi.fn<(id: string, query?: object) => Promise<EvidenceRevisionPage>>();
  const getDecision = vi.fn<(id: string) => Promise<unknown>>();
  const getRelease = vi.fn<(id: string) => Promise<unknown>>();
  beforeEach(() => {
    get.mockReset().mockResolvedValue(run);
    listAttempts.mockReset().mockResolvedValue(attemptPage);
    listEvidenceRevisions.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    getDecision.mockReset().mockResolvedValue(pendingBody());
    getRelease.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'projects/:projectId/runs', children: routes }]),
        { provide: RunsDataService, useValue: { get, listAttempts, listEvidenceRevisions } },
        { provide: RunDataService, useValue: { getDecision } },
        { provide: ReleasesDataService, useValue: { get: getRelease } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function open(id = run.run_id) {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/projects/project-1/runs/${id}`, RunDetailPage);
    await settle(harness);
    return { root: harness.routeNativeElement!, harness };
  }

  /** One macrotask after the stable fixture drains the pin write-back and the render handoffs. */
  async function settle(harness: RouterTestingHarness): Promise<void> {
    await harness.fixture.whenStable();
    const tick = deferred<void>();
    setTimeout(tick.resolve, 0);
    await tick.promise;
    await harness.fixture.whenStable();
  }

  function region(root: HTMLElement, selector: string): HTMLElement {
    const element = root.querySelector<HTMLElement>(selector);
    if (element === null) throw new Error(`the page renders no ${selector}`);
    return element;
  }

  it('shows a canceled run as an informational note that never marks completion reached', async () => {
    get.mockResolvedValue({ ...run, state: 'canceled', after_cancel_events: 3 });
    const { root } = await open();
    const timeline = region(root, 'app-run-timeline');

    const current = timeline.querySelectorAll<HTMLElement>('[aria-current="step"]');
    expect([...current].map((step) => step.dataset['step'])).toEqual(['canceled']);
    expect(timeline.querySelector<HTMLElement>('[data-step="complete"]')?.dataset['status']).toBe(
      'not-reached',
    );
    expect(
      [...timeline.querySelectorAll<HTMLElement>('[data-status="reached"]')].map(
        (step) => step.dataset['step'],
      ),
      'a canceled run claims no completed step',
    ).not.toContain('complete');

    const note = timeline.querySelector<HTMLElement>('[data-after-cancel-note]');
    expect(note?.textContent).toContain('never complete the run');
    expect(timeline.querySelector('[data-after-cancel-count]')?.textContent).toContain(
      '3 events arrived after cancel (informational)',
    );
  });

  it('passes served history to the timeline without deriving timestamps from run metadata', async () => {
    get.mockResolvedValue({
      ...run,
      state_history: [
        { from_state: 'queued', to_state: 'running', occurred_at: '2026-11-07T10:00:00Z' },
        { from_state: 'running', to_state: 'collecting', occurred_at: '2026-11-07T10:01:00Z' },
        { from_state: 'collecting', to_state: 'complete', occurred_at: '2026-11-07T10:02:00Z' },
      ],
    });
    const { root } = await open();
    const timeline = region(root, 'app-run-timeline');
    expect(
      [...timeline.querySelectorAll('[data-step-time] time')].map((time) =>
        time.getAttribute('datetime'),
      ),
    ).toEqual(['2026-11-07T10:00:00Z', '2026-11-07T10:01:00Z', '2026-11-07T10:02:00Z']);
    expect(timeline.querySelector('[data-step="created"] time')).toBeNull();
    expect(timeline.querySelector('[data-step-times]')).toBeNull();
  });

  it.each([
    { history: null, copy: 'History unavailable for this run' },
    { history: [], copy: 'No state changes recorded yet' },
  ])('distinguishes the served history note "$copy"', async ({ history, copy }) => {
    get.mockResolvedValue({ ...run, state_history: history });
    const { root } = await open();
    const timeline = region(root, 'app-run-timeline');
    expect(timeline.querySelector('[data-step-times]')?.textContent).toContain(copy);
    expect(timeline.querySelector('time')).toBeNull();
  });

  it('shows the unbound release state without making a release request', async () => {
    get.mockResolvedValue({ ...run, releases: [] });
    const { root } = await open();
    expect(region(root, '[data-release-unbound]').textContent).toContain(
      'No release bound to this run',
    );
    expect(getRelease).not.toHaveBeenCalled();
  });

  it('loads the bound release and switches between distinct release ids in served order', async () => {
    get.mockResolvedValue({
      ...run,
      releases: [
        { release_id: 'rel-1', release_name: 'Web', component_key: 'web' },
        { release_id: 'rel-1', release_name: 'Web', component_key: 'worker' },
        { release_id: 'rel-2', release_name: 'rel-2', component_key: 'api' },
      ],
    });
    getRelease.mockImplementation((id) =>
      Promise.resolve({
        release_id: id,
        name: id === 'rel-1' ? 'Web release' : 'API release',
        state: 'active',
        version: 2,
        components: [
          { component_key: 'web', digest: 'sha256:abc', environment_revision_id: 'env' },
        ],
        bound_runs: [{ run_id: run.run_id, component_key: 'web' }],
      }),
    );
    const { root, harness } = await open();
    expect(region(root, '[data-release-name]').textContent).toContain('Web release');
    root.querySelector<HTMLButtonElement>('app-release-panel [role="combobox"]')?.click();
    await settle(harness);
    const options = document.querySelectorAll<HTMLElement>('[role="option"]');
    expect(options).toHaveLength(2);
    expect(options[1]?.textContent).toContain('rel-2');
    options[1]?.click();
    await settle(harness);
    expect(getRelease.mock.calls.map(([id]) => id)).toEqual(['rel-1', 'rel-2']);
    expect(region(root, '[data-release-name]').textContent).toContain('API release');
  });

  it.each(['forbidden', 'not_found'] as const)(
    'renders a %s release failure inside a ready run',
    async (kind) => {
      get.mockResolvedValue({
        ...run,
        releases: [{ release_id: 'rel-1', release_name: 'Web', component_key: 'web' }],
      });
      getRelease.mockRejectedValueOnce(refused(kind));
      const { root } = await open();
      expect(root.getAttribute('data-run-detail-status')).toBe('ready');
      expect(
        region(root, 'app-release-panel').querySelector(
          kind === 'forbidden' ? 'app-forbidden-pattern' : 'app-not-found-pattern',
        ),
      ).not.toBeNull();
      expect(root.querySelector('app-manifest-panel')).not.toBeNull();
    },
  );

  it('retries the selected release through its own error region', async () => {
    get.mockResolvedValue({
      ...run,
      releases: [{ release_id: 'rel-1', release_name: 'Web', component_key: 'web' }],
    });
    getRelease.mockRejectedValueOnce(refused('unavailable')).mockResolvedValueOnce({
      release_id: 'rel-1',
      name: 'Recovered release',
      state: 'active',
      version: 1,
      components: [],
      bound_runs: [{ run_id: run.run_id, component_key: 'web' }],
    });
    const { root, harness } = await open();
    const retry = root.querySelector<HTMLButtonElement>(
      'app-release-panel app-error-pattern button',
    );
    expect(retry).not.toBeNull();
    expect(retry?.disabled).toBe(false);
    retry?.click();
    await settle(harness);
    expect(getRelease.mock.calls.map(([id]) => id)).toEqual(['rel-1', 'rel-1']);
    expect(region(root, '[data-release-name]').textContent).toContain('Recovered release');
    expect(root.getAttribute('data-run-detail-status')).toBe('ready');
  });

  it('clears the previous components when the next release fails and retries that selection', async () => {
    get.mockResolvedValue({
      ...run,
      releases: [
        { release_id: 'rel-1', release_name: 'Web', component_key: 'web' },
        { release_id: 'rel-2', release_name: 'API', component_key: 'api' },
      ],
    });
    getRelease
      .mockResolvedValueOnce({
        release_id: 'rel-1',
        name: 'Web release',
        state: 'active',
        version: 1,
        components: [
          { component_key: 'web', digest: 'sha256:abc', environment_revision_id: 'env' },
        ],
        bound_runs: [{ run_id: run.run_id, component_key: 'web' }],
      })
      .mockRejectedValueOnce(refused('unavailable'))
      .mockResolvedValueOnce({
        release_id: 'rel-2',
        name: 'Recovered API release',
        state: 'active',
        version: 2,
        components: [],
        bound_runs: [{ run_id: run.run_id, component_key: 'api' }],
      });
    const { root, harness } = await open();
    expect(root.querySelectorAll('[data-release-component]')).toHaveLength(1);
    root.querySelector<HTMLButtonElement>('app-release-panel [role="combobox"]')?.click();
    await settle(harness);
    document.querySelectorAll<HTMLElement>('[role="option"]')[1]?.click();
    await settle(harness);
    expect(root.querySelectorAll('[data-release-component]')).toHaveLength(0);
    expect(region(root, 'app-release-panel app-error-pattern')).not.toBeNull();
    expect(root.getAttribute('data-run-detail-status')).toBe('ready');
    root.querySelector<HTMLButtonElement>('app-release-panel app-error-pattern button')?.click();
    await settle(harness);
    expect(getRelease.mock.calls.map(([id]) => id)).toEqual(['rel-1', 'rel-2', 'rel-2']);
    expect(region(root, '[data-release-name]').textContent).toContain('Recovered API release');
    expect(root.querySelector('[data-release-unbound]')).toBeNull();
    expect(root.querySelector('[data-release-no-components]')).not.toBeNull();
  });

  it('keeps one current step and no after-cancel note for a complete run', async () => {
    const { root } = await open();
    const timeline = region(root, 'app-run-timeline');

    const current = timeline.querySelectorAll<HTMLElement>('[aria-current="step"]');
    expect([...current].map((step) => step.dataset['step'])).toEqual(['complete']);
    expect(timeline.querySelector('[data-after-cancel-note]')).toBeNull();
  });
  it('uses one shared page heading and places timeline and context as peers after evidence', async () => {
    const { root } = await open();
    const titles = root.querySelectorAll('h1');
    expect(titles).toHaveLength(1);
    expect(titles[0]?.getAttribute('data-title-size')).toBe('page');
    expect(titles[0]?.textContent).toBe('Run detail');
    const pair = root.querySelector('[data-run-history-context]');
    expect([...(pair?.children ?? [])].map((node) => node.tagName.toLowerCase())).toEqual([
      'app-run-timeline',
      'app-run-header',
    ]);
    expect(pair?.classList.contains('min-[1280px]:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]')).toBe(
      true,
    );
    expect(pair?.previousElementSibling?.querySelector('app-evidence-panel')).not.toBeNull();
    expect(pair?.nextElementSibling?.tagName.toLowerCase()).toBe('app-manifest-panel');
    for (const heading of root.querySelectorAll('h2[hlmCardTitle]')) {
      expect(heading.getAttribute('data-title-size')).toBe('section');
    }
  });
});
