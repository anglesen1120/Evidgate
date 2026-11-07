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
import { producerAnchorId } from './evidence/run-evidence-anchors';
import { deferred, pendingBody, run } from './run-detail-store.fixtures';
import { RunDetailPage } from './run-detail.page';

const attemptPage = attemptsOk.body as RunAttemptPage;

describe('RunDetailPage producer region', () => {
  provideLayoutTestStubs();
  const get = vi.fn<(id: string) => Promise<RunStateDto>>();
  const listAttempts = vi.fn<(id: string, query?: object) => Promise<RunAttemptPage>>();
  const listEvidenceRevisions =
    vi.fn<(id: string, query?: object) => Promise<EvidenceRevisionPage>>();
  const getDecision = vi.fn<(id: string) => Promise<unknown>>();

  beforeEach(() => {
    get.mockReset().mockResolvedValue({ ...run, declared_producers: ['shard-9', 'shard-2'] });
    listAttempts.mockReset().mockResolvedValue(attemptPage);
    listEvidenceRevisions.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    getDecision.mockReset().mockResolvedValue(pendingBody());
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
  afterEach(() => TestBed.resetTestingModule());

  async function open() {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/projects/project-1/runs/${run.run_id}`, RunDetailPage);
    await harness.fixture.whenStable();
    const tick = deferred<void>();
    setTimeout(tick.resolve, 0);
    await tick.promise;
    await harness.fixture.whenStable();
    const root = harness.routeNativeElement;
    if (root === null) throw new Error('Run detail did not mount');
    return root;
  }

  it('mounts declared ids in shard order and leaves all unserved facts unavailable', async () => {
    const root = await open();
    const region = root.querySelector('section#producers[data-producer-status]');
    expect(region?.querySelector('h2')?.textContent).toBe('Producers');
    const rows = [...root.querySelectorAll<HTMLElement>('tr[data-producer-slot]')];
    expect(rows.map((row) => row.dataset['producerSlot'])).toEqual(['shard-9', 'shard-2']);
    for (const row of rows) {
      expect(row.querySelectorAll('td[data-fact][data-unavailable]')).toHaveLength(4);
      expect(row.querySelectorAll('td[data-fact] app-status-pill')).toHaveLength(4);
      expect(row.querySelector('th')?.id).toBe(producerAnchorId(row.dataset['producerSlot'] ?? ''));
    }
    expect(region?.querySelector('[data-count-mismatch]')).toBeNull();
    expect(region?.querySelector('[data-badge]')).toBeNull();
    expect(region?.querySelector('[data-completeness]')).toBeNull();
    expect(region?.textContent).not.toMatch(/PASS|FAIL|finalized/);
  });

  it('does not infer slots from shard_count or treat a complete run as finalized producers', async () => {
    get.mockResolvedValue({ ...run, state: 'complete', declared_producers: [], shard_count: 50 });
    const root = await open();
    expect(root.querySelectorAll('tr[data-producer-slot]')).toHaveLength(0);
    expect(root.querySelector('#producers [data-state="empty"]')).not.toBeNull();
    expect(root.querySelector('#producers')?.textContent).not.toContain('finalized');
  });
});
