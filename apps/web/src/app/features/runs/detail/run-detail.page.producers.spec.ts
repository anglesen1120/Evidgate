import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { EvidenceRevisionPage, RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import attemptsOk from '../../../../../../../fixtures/web-api/run-attempts/input.json';
import { ReleasesDataService } from '../../../core/data/releases-data.service';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { RunDataService } from '../data/run-data.service';
import { DENY_CANCEL_SESSION } from '../actions/cancel/cancel-run.test-support';
import { routes } from '../runs.routes';
import { attemptAnchorId, producerAnchorId } from './evidence/run-evidence-anchors';
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
    get
      .mockReset()
      .mockResolvedValue({ ...run, declared_producers: ['shard-9', 'shard-2'], producers: [] });
    listAttempts.mockReset().mockResolvedValue(attemptPage);
    listEvidenceRevisions.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    getDecision.mockReset().mockResolvedValue(pendingBody());
    TestBed.configureTestingModule({
      providers: [
        DENY_CANCEL_SESSION,
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'projects/:projectId/runs', children: routes }]),
        { provide: RunsDataService, useValue: { get, listAttempts, listEvidenceRevisions } },
        { provide: RunDataService, useValue: { getDecision } },
        { provide: ReleasesDataService, useValue: { get: vi.fn() } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function open(fragment = '') {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(
      `/projects/project-1/runs/${run.run_id}${fragment === '' ? '' : `#${fragment}`}`,
      RunDetailPage,
    );
    await harness.fixture.whenStable();
    const tick = deferred<void>();
    setTimeout(tick.resolve, 0);
    await tick.promise;
    await harness.fixture.whenStable();
    const root = harness.routeNativeElement;
    if (root === null) throw new Error('Run detail did not mount');
    return root;
  }

  it('mounts declared ids in shard order and leaves slots with no served facts unavailable', async () => {
    const root = await open();
    const region = root.querySelector('section#producers[data-producer-status]');
    expect(region?.querySelector('h2')?.textContent).toBe('Producers');
    const rows = [...root.querySelectorAll<HTMLElement>('tr[data-row-id]')];
    expect(rows.map((row) => row.dataset['rowId'])).toEqual(['shard-9', 'shard-2']);
    for (const row of rows) {
      expect(row.querySelectorAll('[data-fact][data-unavailable]')).toHaveLength(4);
      expect(row.querySelectorAll('[data-fact] app-status-pill')).toHaveLength(2);
      for (const fact of ['last-sequence', 'count']) {
        const cell = row.querySelector(`[data-fact="${fact}"]`);
        expect(cell?.textContent?.trim()).toBe('Not provided');
        expect(cell?.querySelector('app-status-pill')).toBeNull();
      }
      expect(row.querySelector('[data-producer-slot]')?.id).toBe(
        producerAnchorId(row.dataset['rowId'] ?? ''),
      );
    }
    expect(region?.querySelector('[data-count-mismatch]')).toBeNull();
    expect(region?.querySelector('[data-badge]')).toBeNull();
    expect(region?.querySelector('[data-completeness]')).toBeNull();
    expect(region?.textContent).not.toMatch(/PASS|FAIL|finalized/);
  });

  it('renders the served per-slot facts, with an open slot showing its count', async () => {
    const base = run.producers[0];
    if (base === undefined) throw new Error('run fixture must serve a producer slot');
    get.mockResolvedValue({
      ...run,
      declared_producers: ['shard-1', 'shard-2', 'shard-3'],
      producers: [
        { ...base, producer_id: 'shard-1' },
        { ...base, producer_id: 'shard-2', count: 7, stale_epoch_events: 2 },
        {
          ...base,
          producer_id: 'shard-3',
          identity_state: 'unbound',
          finalize_status: 'open',
          last_sequence: 4,
          count: 4,
          expected: null,
        },
      ],
    });
    const root = await open();
    const row = (id: string) => root.querySelector<HTMLElement>(`tr[data-row-id="${id}"]`);
    const text = (id: string, fact: string) =>
      row(id)?.querySelector(`[data-fact="${fact}"]`)?.textContent?.replace(/\s+/g, ' ').trim();
    expect(text('shard-1', 'identity')).toBe('Bound');
    expect(text('shard-1', 'finalize')).toBe('Finalized');
    expect(text('shard-1', 'last-sequence')).toBe(String(base.last_sequence));
    expect(text('shard-1', 'count')).toBe(`${base.count} / ${base.expected}`);
    expect(row('shard-1')?.querySelector('[data-count-mismatch], [data-badge]')).toBeNull();
    expect(text('shard-2', 'count')).toContain(`7 / ${base.expected}`);
    expect(row('shard-2')?.querySelector('[data-count-mismatch]')).not.toBeNull();
    expect(row('shard-2')?.querySelector('[data-badge="stale-epoch"]')?.textContent).toContain(
      '2 stale-epoch events',
    );
    expect(text('shard-3', 'identity')).toBe('Unbound');
    expect(text('shard-3', 'last-sequence')).toBe('4');
    const openCount = row('shard-3')?.querySelector('[data-fact="count"]');
    expect(openCount?.querySelector('app-number-value')?.textContent).toBe('4');
    expect(openCount?.querySelector('app-status-pill')?.textContent).toContain('Expected unset');
    expect(openCount?.getAttribute('aria-label')).toBe('4 served, expected count not set yet');
    expect(row('shard-3')?.querySelectorAll('[data-fact][data-unavailable]')).toHaveLength(0);
    expect(root.querySelector('#producers [data-completeness]')).toBeNull();
  });

  it('does not infer slots from shard_count or treat a complete run as finalized producers', async () => {
    get.mockResolvedValue({
      ...run,
      state: 'complete',
      declared_producers: [],
      producers: [],
      shard_count: 50,
    });
    const root = await open();
    expect(root.querySelectorAll('tr[data-row-id]')).toHaveLength(0);
    expect(root.querySelector('#producers [data-state="empty"]')).not.toBeNull();
    expect(root.querySelector('#producers')?.textContent).not.toContain('finalized');
  });

  it('shows the served run-level late count once while a slot with no served facts stays unavailable', async () => {
    get.mockResolvedValue({
      ...run,
      state: 'canceled',
      declared_producers: ['shard-1', 'shard-2'],
      producers: [],
      after_cancel_events: 3,
    });
    const root = await open();
    const region = root.querySelector('#producers');
    expect(region?.querySelectorAll('[data-badge="after-cancel"]')).toHaveLength(1);
    expect(region?.querySelector('[data-run-after-cancel]')?.textContent).toContain(
      '3 after-cancel events for this run',
    );
    expect(region?.querySelector('tbody [data-badge="after-cancel"]')).toBeNull();
    expect(region?.querySelectorAll('[data-fact][data-unavailable]')).toHaveLength(8);
    expect(root.querySelector('[data-step="complete"]')?.getAttribute('data-status')).toBe(
      'not-reached',
    );
  });

  it.each(['failed', 'loading'])(
    'lands a producer fragment while attempts are %s',
    async (status) => {
      if (status === 'failed') {
        listAttempts.mockRejectedValue(
          new ApiRequestError({
            kind: 'unavailable',
            status: 503,
            code: 'unavailable',
            message: '',
            requestId: null,
            retryable: true,
            details: [],
            retryAfterSeconds: null,
          }),
        );
      } else {
        listAttempts.mockReturnValue(deferred<RunAttemptPage>().promise);
      }
      const root = await open('producer-shard-2');
      const target = root.querySelector('#producer-shard-2');
      expect(target).not.toBeNull();
      expect(document.activeElement).toBe(target);
    },
  );

  it('lands a producer beyond the first bounded page even after attempts fail', async () => {
    get.mockResolvedValue({
      ...run,
      declared_producers: Array.from({ length: 256 }, (_, index) => `shard-${index}`),
      producers: [],
      shard_count: 256,
    });
    listAttempts.mockRejectedValue(new Error('attempts unavailable'));
    const root = await open('producer-shard-255');
    const target = root.querySelector('#producer-shard-255');
    expect(target).not.toBeNull();
    expect(document.activeElement).toBe(target);
    expect(root.querySelectorAll('#producers tr[data-row-id]')).toHaveLength(56);
  });

  it('keeps an attempt fragment waiting for the attempts response', async () => {
    const pending = deferred<RunAttemptPage>();
    listAttempts.mockReturnValue(pending.promise);
    const first = attemptPage.items[0];
    if (first === undefined) throw new Error('attempt fixture must have a positive control');
    const anchor = attemptAnchorId(first.execution_key, first.attempt_no);
    const root = await open(anchor);
    expect(root.querySelector(`#${anchor}`)).toBeNull();
    pending.resolve(attemptPage);
    await vi.waitFor(() => expect(document.activeElement?.id).toBe(anchor));
  });
});
