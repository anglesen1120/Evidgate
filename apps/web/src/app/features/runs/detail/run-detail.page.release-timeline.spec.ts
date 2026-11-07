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
import { deferred, pendingBody, run } from './run-detail-store.fixtures';
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
    return { root: harness.routeNativeElement! };
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
    get.mockResolvedValue({ ...run, state: 'canceled' });
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
    // The response serves no after-cancel count, so the region says nothing about one.
    expect(note?.textContent).not.toMatch(/\d+ events arrived after cancel/);
  });

  it('shows the release region as unavailable instead of bound to no release', async () => {
    const { root } = await open();
    const release = region(root, 'app-release-panel');

    expect(release.querySelector('[data-release-unavailable]')?.textContent).toContain(
      'does not name a release',
    );
    expect(release.querySelector('[data-release-unbound]')).toBeNull();
    expect(root.textContent ?? '').not.toContain('No release bound to this run');
    // No release id is served, so the page never reads a release it cannot address.
    expect(getRelease).not.toHaveBeenCalled();
  });

  it('keeps one current step and no after-cancel note for a complete run', async () => {
    const { root } = await open();
    const timeline = region(root, 'app-run-timeline');

    const current = timeline.querySelectorAll<HTMLElement>('[aria-current="step"]');
    expect([...current].map((step) => step.dataset['step'])).toEqual(['complete']);
    expect(timeline.querySelector('[data-after-cancel-note]')).toBeNull();
  });
});
