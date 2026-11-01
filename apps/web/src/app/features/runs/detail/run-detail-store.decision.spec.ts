import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { EvidenceRevisionPage, RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { RunDataService } from '../data/run-data.service';
import { RunDetailStore } from './run-detail.store';
import { deferred, first, liveBody, pendingBody, refused, run } from './run-detail-store.fixtures';

describe('RunDetailStore decision', () => {
  const get = vi.fn<(id: string) => Promise<RunStateDto>>();
  const listAttempts =
    vi.fn<(id: string, query?: { cursor?: string; limit?: number }) => Promise<RunAttemptPage>>();
  const listEvidenceRevisions =
    vi.fn<
      (id: string, query?: { cursor?: string; limit?: number }) => Promise<EvidenceRevisionPage>
    >();
  const getDecision = vi.fn<(id: string) => Promise<unknown>>();
  beforeEach(() => {
    get.mockReset();
    listAttempts.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    listEvidenceRevisions.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    getDecision.mockReset().mockResolvedValue(pendingBody());
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        RunDetailStore,
        { provide: RunsDataService, useValue: { get, listAttempts, listEvidenceRevisions } },
        { provide: RunDataService, useValue: { getDecision } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it('reads the run and the decision alongside each other and keeps pending as pending', async () => {
    get.mockResolvedValue(run);
    getDecision.mockResolvedValue(pendingBody());
    const store = TestBed.inject(RunDetailStore);
    expect(store.decision()).toBeNull();
    const loading = store.load(run.run_id);
    expect(store.decision()).toBeNull();
    expect(store.decisionStatus()).toBe('loading');
    await loading;
    expect(get).toHaveBeenCalledExactlyOnceWith(run.run_id);
    expect(getDecision).toHaveBeenCalledExactlyOnceWith(run.run_id);
    expect(store.decision()).toEqual({ kind: 'pending' });
    expect(store.decisionStatus()).toBe('ready');
    expect(store.decisionError()).toBeNull();
    expect(store.shadowIgnored()).toBe(false);
  });
  it('accepts an evaluated body member for member and never computes a verdict', async () => {
    get.mockResolvedValue(run);
    getDecision.mockResolvedValue(liveBody());
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    expect(store.decision()).toEqual({
      kind: 'evaluated',
      decision: 'FAIL',
      evaluationId: 'evaluation-1',
      evidenceRevision: 4,
      completeness: 'incomplete',
      outcome: 'has_failures',
      missing: ['missing_shard'],
      reasons: ['critical_not_executed'],
      policyHash: 'sha256:policy',
      manifestHash: 'sha256:manifest',
      evaluatorVersion: '1.4.0',
      mode: 'live',
    });
  });
  it('shadow evaluation never fills the live slot', async () => {
    get.mockResolvedValue(run);
    getDecision.mockResolvedValueOnce(liveBody()).mockResolvedValueOnce(
      liveBody({
        decision: 'PASS',
        outcome: 'all_passed',
        completeness: 'complete',
        evidence_revision: 5,
        mode: 'shadow',
      }),
    );
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    const accepted = store.decision();
    await store.refreshDecision();
    expect(store.decision()).toBe(accepted);
    expect(store.decision()).toMatchObject({ kind: 'evaluated', decision: 'FAIL' });
    expect(store.shadowIgnored()).toBe(true);
    expect(store.decisionStatus()).toBe('ready');
  });
  it('reports an unavailable live decision when a shadow arrives first', async () => {
    get.mockResolvedValue(run);
    getDecision.mockResolvedValue(liveBody({ mode: 'shadow' }));
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    expect(store.decision()).toEqual({ kind: 'unavailable', cause: 'shadow-ignored' });
    expect(store.shadowIgnored()).toBe(true);
  });
  it('reports a contract-invalid body as unavailable, never as pending or a verdict', async () => {
    get.mockResolvedValue(run);
    getDecision.mockResolvedValue(liveBody({ missing: null }));
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    expect(store.decision()).toEqual({ kind: 'unavailable', cause: 'contract' });
    expect(store.decisionStatus()).toBe('ready');
    expect(store.decisionError()).toBeNull();
  });
  it('keeps the run and the attempts when the decision read fails', async () => {
    get.mockResolvedValue(run);
    listAttempts.mockResolvedValue(first);
    getDecision.mockRejectedValue(refused('forbidden'));
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
    expect(store.status()).toBe('ready');
    expect(store.run()).toEqual(run);
    expect(store.attempts()).toHaveLength(first.items.length);
    expect(store.decisionStatus()).toBe('error');
    expect(store.decision()).toEqual({ kind: 'unavailable', cause: 'error' });
    expect(store.decisionError()?.kind).toBe('forbidden');
  });
  it('keeps the accepted body through a transient refresh failure and marks it stale', async () => {
    get.mockResolvedValue(run);
    getDecision
      .mockResolvedValueOnce(liveBody())
      .mockRejectedValueOnce(refused('unavailable', 503));
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    const accepted = store.decision();
    await store.refreshDecision();
    expect(store.decision()).toBe(accepted);
    expect(store.decisionStatus()).toBe('error');
    expect(store.decisionError()?.kind).toBe('unavailable');
    expect(get).toHaveBeenCalledTimes(1);
  });
  it.each([
    ['forbidden', 403],
    ['unauthenticated', 401],
    ['not_found', 404],
  ] as const)(
    'erases the accepted body when a refresh is refused with %s, keeping the run',
    async (kind, status) => {
      get.mockResolvedValue(run);
      listAttempts.mockResolvedValue(first);
      getDecision.mockResolvedValueOnce(liveBody()).mockRejectedValueOnce(refused(kind, status));
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      expect(store.decision()).toMatchObject({ kind: 'evaluated', decision: 'FAIL' });
      await store.refreshDecision();
      expect(store.decision()).toEqual({ kind: 'unavailable', cause: 'error' });
      expect(store.decisionStatus()).toBe('error');
      expect(store.decisionError()?.kind).toBe(kind);
      expect(store.status()).toBe('ready');
      expect(store.run()).toEqual(run);
      expect(store.attempts()).toHaveLength(first.items.length);
    },
  );
  it('re-reads only the decision on refresh, without the run or the attempts', async () => {
    get.mockResolvedValue(run);
    getDecision.mockResolvedValue(pendingBody());
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
    await store.refreshDecision();
    expect(get).toHaveBeenCalledTimes(1);
    expect(listAttempts).toHaveBeenCalledTimes(1);
    expect(getDecision).toHaveBeenCalledTimes(2);
  });
  it.each(['success', 'failure'] as const)(
    'drops a late decision %s after the run changes',
    async (result) => {
      const old = deferred<unknown>();
      get.mockResolvedValue(run);
      getDecision
        .mockReturnValueOnce(old.promise)
        .mockResolvedValueOnce(liveBody({ decision: 'PASS', evidence_revision: 9 }));
      const store = TestBed.inject(RunDetailStore);
      const previous = store.load('old');
      await store.load('new');
      if (result === 'success') old.resolve(liveBody({ decision: 'FAIL' }));
      else old.reject(refused('forbidden'));
      await previous;
      expect(store.decisionStatus()).toBe('ready');
      expect(store.decision()).toMatchObject({ kind: 'evaluated', decision: 'PASS' });
      expect(store.decisionError()).toBeNull();
    },
  );
  it('clears the decision synchronously when a new run request starts', async () => {
    get.mockResolvedValue(run);
    getDecision.mockResolvedValue(liveBody());
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    const loading = store.load('next');
    expect(store.decision()).toBeNull();
    expect(store.decisionStatus()).toBe('loading');
    expect(store.decisionError()).toBeNull();
    expect(store.shadowIgnored()).toBe(false);
    await loading;
  });
});
