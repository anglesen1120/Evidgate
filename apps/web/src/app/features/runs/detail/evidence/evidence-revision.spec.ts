import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { Location } from '@angular/common';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { EvidenceRevisionPage, RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import { RunsDataService } from '../../../../core/data/runs-data.service';
import { ReleasesDataService } from '../../../../core/data/releases-data.service';
import { RunDataService } from '../../data/run-data.service';
import {
  first as attemptPage,
  liveBody,
  refused,
  revision,
  revisionPage,
  run,
} from '../run-detail-store.fixtures';
import { routes } from '../../runs.routes';
import { RunDetailPage } from '../run-detail.page';
import { stubScopeBrowserApis } from '../../../../shell/layout/scope.fixture';
import { EvidenceRevisionSelectorHarness } from './evidence-revision-selector.test-support';

const newest = revision({
  revision_no: 4,
  completeness: 'incomplete',
  missing: ['missing_shard'],
  evaluation_id: 'evaluation-4',
});
const older = revision({
  revision_no: 3,
  completeness: 'complete',
  missing: [],
  evaluation_id: 'evaluation-3',
});
const oldest = revision({
  revision_no: 2,
  completeness: 'incomplete',
  missing: ['count_mismatch'],
  evaluation_id: null,
});

describe('Run detail evidence revision', () => {
  const get = vi.fn<(id: string) => Promise<RunStateDto>>();
  const listAttempts = vi.fn<(id: string, query?: object) => Promise<RunAttemptPage>>();
  const listEvidenceRevisions =
    vi.fn<(id: string, query?: object) => Promise<EvidenceRevisionPage>>();
  const getDecision = vi.fn<(id: string) => Promise<unknown>>();
  let restoreBrowserApis: () => void;

  beforeEach(() => {
    restoreBrowserApis = stubScopeBrowserApis();
    get.mockReset().mockResolvedValue(run);
    listAttempts.mockReset().mockResolvedValue(attemptPage);
    listEvidenceRevisions.mockReset().mockResolvedValue(revisionPage([newest, older, oldest]));
    getDecision.mockReset().mockResolvedValue(liveBody({ evidence_revision: 4 }));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'projects/:projectId/runs', children: routes }]),
        { provide: RunsDataService, useValue: { get, listAttempts, listEvidenceRevisions } },
        { provide: RunDataService, useValue: { getDecision } },
        { provide: ReleasesDataService, useValue: { get: vi.fn() } },
      ],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    restoreBrowserApis();
    vi.unstubAllGlobals();
  });

  async function open(query = '') {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/projects/project-1/runs/${run.run_id}${query}`, RunDetailPage);
    await settle(harness);
    return { harness, root: harness.routeNativeElement! };
  }

  async function settle(harness: RouterTestingHarness): Promise<void> {
    await harness.fixture.whenStable();
    // The pin is committed with a router navigation, which resolves after the render that asked
    // for it; one macrotask drains that follow-up before the DOM and URL are asserted.
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
  }

  function panel(root: HTMLElement): HTMLElement {
    return root.querySelector('app-evidence-panel')!;
  }

  async function selectRevision(harness: RouterTestingHarness, revisionNo: number): Promise<void> {
    const selector = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      EvidenceRevisionSelectorHarness,
    );
    await selector.choose(revisionNo);
  }

  it('selecting an older revision shows the banner and keeps that revision on screen', async () => {
    const { harness, root } = await open();
    // Without an explicit pin the accepted live decision's revision is used and written to the URL.
    expect(panel(root).textContent).toContain('Selected evidence revision: 4');
    expect(TestBed.inject(Location).path()).toContain('evidence_revision=4');

    await selectRevision(harness, 3);
    await settle(harness);

    expect(TestBed.inject(Location).path()).toContain('evidence_revision=3');
    const banner = panel(root).querySelector('[data-evidence-newer]');
    expect(banner?.textContent).toContain('A newer revision exists');
    expect(banner?.textContent).toContain('You are viewing revision 3. The newest revision is 4.');
    expect(panel(root).querySelector('[data-evidence-selected]')?.textContent).toContain(
      'Selected evidence revision: 3',
    );
    expect(panel(root).querySelector('[data-evidence-completeness]')?.textContent).toContain(
      'Evidence complete',
    );
    expect(panel(root).querySelector('.reason-empty')?.textContent?.trim()).toBe(
      'No missing evidence reasons reported',
    );
    expect(panel(root).querySelector('[data-evidence-evaluation]')?.textContent).toContain(
      'evaluation-3',
    );
    expect(panel(root).querySelector('[data-evidence-historical]')).not.toBeNull();

    // The decision region keeps its own latest-live revision and explains the mismatch.
    const decision = root.querySelector('app-decision-view')!;
    expect(decision.querySelector('[data-decision-revision] dd')?.textContent).toBe('4');
    expect(decision.querySelector('[data-decision-mismatch]')).not.toBeNull();

    (banner?.querySelector('button') as HTMLButtonElement).click();
    await settle(harness);
    expect(TestBed.inject(Location).path()).toContain('evidence_revision=4');
    expect(panel(root).querySelector('[data-evidence-newer]')).toBeNull();
    expect(panel(root).querySelector('[data-evidence-missing]')?.textContent).toContain(
      'missing_shard',
    );
  });

  it('a direct URL pin wins while pages load and resolves when the older page arrives', async () => {
    listEvidenceRevisions
      .mockReset()
      .mockResolvedValueOnce(revisionPage([newest, older], 'cursor-1'))
      .mockResolvedValueOnce(revisionPage([oldest], null));
    const { harness, root } = await open('?evidence_revision=2');

    expect(TestBed.inject(Location).path()).toContain('evidence_revision=2');
    expect(panel(root).querySelector('[data-evidence-search-older]')).not.toBeNull();
    // No data is invented for a pin that is not on a loaded page, even though the decision is.
    expect(panel(root).querySelector('[data-evidence-completeness]')?.textContent).toContain(
      'Evidence status unavailable',
    );

    (panel(root).querySelector('[data-evidence-load-older]') as HTMLButtonElement).click();
    await settle(harness);
    expect(listEvidenceRevisions).toHaveBeenLastCalledWith(run.run_id, {
      limit: 200,
      cursor: 'cursor-1',
    });
    expect(TestBed.inject(Location).path()).toContain('evidence_revision=2');
    expect(panel(root).querySelector('[data-evidence-search-older]')).toBeNull();
    expect(panel(root).querySelector('[data-evidence-selected]')?.textContent).toContain(
      'Selected evidence revision: 2',
    );
    expect(panel(root).querySelector('[data-evidence-missing]')?.textContent).toContain(
      'count_mismatch',
    );
  });

  it('says the pinned revision is unavailable once every page is loaded, without substituting', async () => {
    listEvidenceRevisions.mockReset().mockResolvedValue(revisionPage([newest, older]));
    const { root } = await open('?evidence_revision=9');
    expect(
      panel(root).querySelector('[data-evidence-selected-unavailable]')?.textContent,
    ).toContain('Selected revision unavailable');
    expect(panel(root).querySelector('[data-evidence-selected]')?.textContent).toContain(
      'Selected evidence revision: 9',
    );
    expect(panel(root).querySelector('[data-evidence-completeness]')?.textContent).toContain(
      'Evidence status unavailable',
    );
    // The newest revision is not substituted for the missing pin.
    expect(panel(root).querySelector('[data-evidence-missing]')?.textContent).toContain(
      'Missing evidence reasons unavailable',
    );
    expect(panel(root).querySelector('[data-evidence-missing]')?.textContent).not.toContain(
      'missing_shard',
    );
  });

  it('a failed revision list leaves the run, manifest, attempts and decision readable', async () => {
    listEvidenceRevisions.mockReset().mockRejectedValue(refused('unavailable'));
    const { root } = await open();
    expect(root.querySelector('app-run-header')).not.toBeNull();
    expect(root.querySelector('app-manifest-panel')).not.toBeNull();
    expect(root.querySelector('app-attempts-table')).not.toBeNull();
    expect(root.querySelector('app-decision-chip')?.textContent).toContain('FAIL');
    const evidence = panel(root);
    expect(evidence.querySelector('app-error-pattern')?.textContent).toContain('request-detail');
    // The decision's revision is still pinnable, so its own evidence stays available.
    expect(evidence.querySelector('[data-evidence-completeness]')?.textContent).toContain(
      'Evidence incomplete',
    );
    expect(evidence.querySelector('[data-evidence-missing]')?.textContent).toContain(
      'missing_shard',
    );
  });

  it('a URL pin change restores that revision and keeps unrelated query state', async () => {
    const { harness, root } = await open('?tab=attempts#evidence-missing');
    const path = `/projects/project-1/runs/${run.run_id}?tab=attempts`;
    await harness.navigateByUrl(`${path}&evidence_revision=3#evidence-missing`, RunDetailPage);
    await settle(harness);
    expect(panel(root).querySelector('[data-evidence-selected]')?.textContent).toContain(
      'Selected evidence revision: 3',
    );
    // The pin's write-back merges into the URL instead of replacing the other query state.
    expect(TestBed.inject(Location).path()).toContain('tab=attempts');
    expect(TestBed.inject(Router).routerState.snapshot.root.fragment).toBe('evidence-missing');

    // Back/Forward restore a pin the same way: the URL is the only source of an explicit pin.
    await harness.navigateByUrl(`${path}&evidence_revision=4#evidence-missing`, RunDetailPage);
    await settle(harness);
    expect(panel(root).querySelector('[data-evidence-selected]')?.textContent).toContain(
      'Selected evidence revision: 4',
    );
    expect(panel(root).querySelector('[data-evidence-newer]')).toBeNull();
  });

  it('keeps an explicit URL pin when the run read is retried', async () => {
    get.mockReset().mockRejectedValueOnce(refused('unavailable')).mockResolvedValue(run);
    const { harness, root } = await open('?evidence_revision=3');
    const retry = root.querySelector<HTMLButtonElement>('app-request-state button');
    expect(retry).not.toBeNull();
    retry!.click();
    await settle(harness);

    // The retried read must not let the accepted decision (revision 4) re-place the pin and rewrite
    // the URL: the explicit pin is the visitor's choice and outlives a reload.
    expect(panel(root).querySelector('[data-evidence-selected]')?.textContent).toContain(
      'Selected evidence revision: 3',
    );
    expect(TestBed.inject(Location).path()).toContain('evidence_revision=3');
    expect(panel(root).querySelector('[data-evidence-newer]')?.textContent).toContain(
      'You are viewing revision 3. The newest revision is 4.',
    );
  });

  it('ignores a query value that is not a positive revision number', async () => {
    const { harness, root } = await open('?evidence_revision=ev-3');
    expect(panel(root).querySelector('[data-evidence-selected-unavailable]')).toBeNull();
    expect(panel(root).querySelector('[data-evidence-selected]')?.textContent).toContain(
      'Selected evidence revision: 4',
    );
    expect(TestBed.inject(Location).path()).toContain('evidence_revision=4');
    expect(TestBed.inject(Location).path()).not.toContain('ev-3');
    expect(harness.routeNativeElement).not.toBeNull();
  });
});
