import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { EvidenceRevisionPage, RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import { ReleasesDataService } from '../../../core/data/releases-data.service';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { RunDataService } from '../data/run-data.service';
import { manifestFromRun, UNAVAILABLE_MANIFEST } from './manifest-summary';
import { ATTEMPTS_PAGE_LIMIT, RunDetailStore } from './run-detail.store';
import { first, pendingBody, refused, run, second, deferred } from './run-detail-store.fixtures';

describe('RunDetailStore', () => {
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
        { provide: ReleasesDataService, useValue: { get: vi.fn() } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  it('maps the stored response and its manifest members verbatim', async () => {
    get.mockResolvedValue(run);
    const store = TestBed.inject(RunDetailStore);
    expect(store.status()).toBe('idle');
    const loading = store.load(run.run_id);
    expect(store.status()).toBe('loading');
    expect(store.run()).toBeNull();
    await loading;
    expect(get).toHaveBeenCalledWith(run.run_id);
    expect(store.status()).toBe('ready');
    expect(store.run()).toEqual(run);
    expect(store.manifest()).toEqual({
      manifestHash: run.manifest_hash,
      suiteRevisionId: run.suite_revision_id,
      policyRevisionId: run.policy_revision_id,
      shardCount: run.shard_count,
      producers: run.declared_producers,
    });
    expect(store.manifest()).toEqual(manifestFromRun(run));
  });
  it('foreign and missing ids produce identical not-found state without retaining run data', async () => {
    const store = TestBed.inject(RunDetailStore);
    const states = [];
    for (const id of ['foreign-id', 'missing-id']) {
      get.mockResolvedValueOnce(run);
      await store.load(run.run_id);
      get.mockRejectedValueOnce(refused('not_found'));
      await store.load(id);
      states.push({ status: store.status(), run: store.run(), manifest: store.manifest() });
      expect(store.status()).toBe('not-found');
      expect(store.run()).toBeNull();
    }
    expect(states[0]).toEqual(states[1]);
  });
  it('403 yields forbidden, never not-found, and clears previously loaded data', async () => {
    const store = TestBed.inject(RunDetailStore);
    get.mockResolvedValueOnce(run);
    await store.load(run.run_id);
    get.mockRejectedValueOnce(refused('forbidden'));
    await store.load('denied');
    expect(store.status()).toBe('forbidden');
    expect(store.run()).toBeNull();
    expect(store.manifest()).toEqual(UNAVAILABLE_MANIFEST);
  });
  it.each(['success', 'failure'] as const)(
    'ignores stale %s after route identity changes',
    async (result) => {
      const old = deferred<RunStateDto>();
      get.mockReturnValueOnce(old.promise).mockResolvedValueOnce({ ...run, version: 8 });
      const store = TestBed.inject(RunDetailStore);
      const previous = store.load('old');
      await store.load('new');
      if (result === 'success') old.resolve(run);
      else old.reject(refused('not_found'));
      await previous;
      expect(store.status()).toBe('ready');
      expect(store.run()?.version).toBe(8);
      expect(store.loadError()).toBeNull();
    },
  );
  it('clears data synchronously when a new request starts and surfaces unexpected failures', async () => {
    get.mockResolvedValueOnce(run).mockRejectedValueOnce(new Error('private diagnostic'));
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    const loading = store.load('next');
    expect(store.run()).toBeNull();
    expect(store.status()).toBe('loading');
    await loading;
    expect(store.status()).toBe('error');
    expect(store.loadError()?.kind).toBe('unexpected');
    expect(store.loadError()?.message).not.toContain('private diagnostic');
  });
  describe('attempts', () => {
    it('reads the first page at the bounded limit after the run and maps rows verbatim', async () => {
      get.mockResolvedValue(run);
      listAttempts.mockResolvedValue(first);
      const store = TestBed.inject(RunDetailStore);
      expect(store.attempts()).toBeNull();
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      expect(listAttempts).toHaveBeenCalledExactlyOnceWith(run.run_id, {
        limit: ATTEMPTS_PAGE_LIMIT,
      });
      const wire = first.items[3]!;
      expect(store.attempts()).toHaveLength(first.items.length);
      expect(store.attempts()![3]).toEqual({
        executionKey: wire.execution_key,
        attemptNo: wire.attempt_no,
        rawStatus: wire.raw_status,
        normalisedStatus: wire.normalized_status,
        durationMs: wire.duration_ms,
        artifactCount: wire.artifact_count,
        healingCandidate: null,
      });
      expect(store.hasMoreAttempts()).toBe(true);
    });
    it('keeps blocked, timed_out and error distinct from passed', async () => {
      get.mockResolvedValue(run);
      listAttempts.mockResolvedValue({
        items: [...first.items, ...second.items],
        next_cursor: null,
      });
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      const byStatus = store.attempts()!.map((attempt) => attempt.normalisedStatus);
      expect(byStatus).toEqual(expect.arrayContaining(['blocked', 'timed_out', 'error']));
      expect(
        store
          .attempts()!
          .filter((a) => a.normalisedStatus === 'passed' && a.rawStatus !== 'passed'),
      ).toEqual([]);
      expect(store.hasMoreAttempts()).toBe(false);
    });
    it('accepts an empty first page without inventing a total or allowing navigation', async () => {
      get.mockResolvedValue(run);
      listAttempts.mockResolvedValue({ items: [], next_cursor: null });
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      expect(store.attempts()).toEqual([]);
      expect(store.attemptsPage()).toBe(1);
      expect(store.attemptsStatus()).toBe('ready');
      await store.nextAttempts();
      await store.previousAttempts();
      expect(listAttempts).toHaveBeenCalledTimes(1);
    });
    it('replaces pages, returns to the first page and refuses to advance past the last', async () => {
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce(first)
        .mockResolvedValueOnce(second)
        .mockResolvedValueOnce(first);
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      expect(store.attemptsPage()).toBe(1);
      expect(store.attemptsPageSize()).toBe(20);
      await store.previousAttempts();
      expect(listAttempts).toHaveBeenCalledTimes(1);
      await store.nextAttempts();
      expect(store.attemptsPage()).toBe(2);
      expect(store.attempts()).toHaveLength(second.items.length);
      expect(store.attempts()?.[0]?.executionKey).toBe(second.items[0]?.execution_key);
      expect(store.hasMoreAttempts()).toBe(false);
      await store.nextAttempts();
      expect(listAttempts).toHaveBeenCalledTimes(2);
      await store.previousAttempts();
      expect(listAttempts).toHaveBeenLastCalledWith(run.run_id, { limit: 20 });
      expect(store.attemptsPage()).toBe(1);
      expect(store.attempts()).toHaveLength(first.items.length);
    });
    it('retains rows and the committed page on error, retrying the failed previous intent', async () => {
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce(first)
        .mockResolvedValueOnce(second)
        .mockRejectedValueOnce(new Error('offline'))
        .mockResolvedValueOnce(first);
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await store.nextAttempts();
      const rows = store.attempts();
      await store.previousAttempts();
      expect(store.attempts()).toBe(rows);
      expect(store.attemptsPage()).toBe(2);
      expect(store.attemptsStatus()).toBe('ready');
      expect(store.attemptsError()).not.toBeNull();
      await store.retryAttempts();
      expect(listAttempts).toHaveBeenLastCalledWith(run.run_id, { limit: 20 });
      expect(store.attemptsPage()).toBe(1);
      expect(store.attemptsError()).toBeNull();
    });
    it('retries a failed next intent without appending or refetching run and revisions', async () => {
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce(first)
        .mockRejectedValueOnce(new Error('offline'))
        .mockResolvedValueOnce(second);
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      const runData = store.run();
      const revisionData = store.revisions();
      const rows = store.attempts();
      await store.nextAttempts();
      expect(store.attempts()).toBe(rows);
      expect(store.attemptsPage()).toBe(1);
      await store.retryAttempts();
      expect(listAttempts).toHaveBeenLastCalledWith(run.run_id, {
        limit: 20,
        cursor: first.next_cursor,
      });
      expect(store.attempts()).toHaveLength(second.items.length);
      expect(store.run()).toBe(runData);
      expect(store.revisions()).toBe(revisionData);
      expect(get).toHaveBeenCalledTimes(1);
      expect(listEvidenceRevisions).toHaveBeenCalledTimes(1);
    });
    it('rejects a cursor cycle while keeping the accepted page', async () => {
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce(first)
        .mockResolvedValueOnce({ items: second.items, next_cursor: first.next_cursor });
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      const rows = store.attempts();
      await store.nextAttempts();
      expect(store.attempts()).toBe(rows);
      expect(store.attemptsError()?.code).toBe('attempts_cursor_repeated');
      expect(store.hasMoreAttempts()).toBe(false);
    });
    it('accepts an empty terminal page and can return from it', async () => {
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce(first)
        .mockResolvedValueOnce({ items: [], next_cursor: null })
        .mockResolvedValueOnce(first);
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await store.nextAttempts();
      expect(store.attempts()).toEqual([]);
      expect(store.attemptsPage()).toBe(2);
      expect(store.hasMoreAttempts()).toBe(false);
      await store.previousAttempts();
      expect(store.attempts()).toHaveLength(first.items.length);
    });
    it.each([50, 100])('resets cursor history when selecting %s rows', async (size) => {
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce(first)
        .mockResolvedValueOnce(second)
        .mockResolvedValueOnce({ items: [], next_cursor: null });
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await store.nextAttempts();
      await store.setAttemptsPageSize(size);
      expect(listAttempts).toHaveBeenLastCalledWith(run.run_id, { limit: size });
      expect(store.attemptsPageSize()).toBe(size);
      expect(store.attemptsPage()).toBe(1);
      expect(store.attempts()).toEqual([]);
      await store.previousAttempts();
      await store.setAttemptsPageSize(200);
      expect(listAttempts).toHaveBeenCalledTimes(3);
    });
    it('resets page and size on run identity changes and ignores an older page response', async () => {
      get.mockResolvedValue(run);
      const old = deferred<RunAttemptPage>();
      listAttempts
        .mockResolvedValueOnce(first)
        .mockResolvedValueOnce(first)
        .mockReturnValueOnce(old.promise)
        .mockResolvedValueOnce(second);
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await store.setAttemptsPageSize(50);
      const next = store.nextAttempts();
      get.mockResolvedValueOnce({ ...run, run_id: 'another-run' });
      await store.load('another-run');
      old.resolve(first);
      await next;
      expect(listAttempts).toHaveBeenLastCalledWith('another-run', { limit: 20 });
      expect(store.attemptsPage()).toBe(1);
      expect(store.attemptsPageSize()).toBe(20);
      expect(store.attempts()).toHaveLength(second.items.length);
    });
    it('ignores repeated navigation while a page is loading', async () => {
      get.mockResolvedValue(run);
      const pending = deferred<RunAttemptPage>();
      listAttempts.mockResolvedValueOnce(first).mockReturnValueOnce(pending.promise);
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      const next = store.nextAttempts();
      await store.nextAttempts();
      await store.previousAttempts();
      expect(listAttempts).toHaveBeenCalledTimes(2);
      pending.resolve(second);
      await next;
      expect(store.attemptsPage()).toBe(2);
    });
    it('a first-page failure is its own error state and retry re-reads only attempts', async () => {
      get.mockResolvedValue(run);
      listAttempts.mockRejectedValueOnce(refused('forbidden')).mockResolvedValueOnce(first);
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('error'));
      expect(store.status()).toBe('ready');
      expect(store.attempts()).toBeNull();
      expect(store.attemptsError()?.kind).toBe('forbidden');
      await store.retryAttempts();
      expect(get).toHaveBeenCalledTimes(1);
      expect(store.attemptsStatus()).toBe('ready');
      expect(store.attemptsError()).toBeNull();
    });
    it('drops a late attempts page after the run changes', async () => {
      const old = deferred<RunAttemptPage>();
      get.mockResolvedValue(run);
      listAttempts.mockReturnValueOnce(old.promise).mockResolvedValueOnce(second);
      const store = TestBed.inject(RunDetailStore);
      await store.load('old');
      await store.load('new');
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      old.resolve(first);
      await Promise.resolve();
      expect(store.attempts()).toHaveLength(second.items.length);
    });
    it('clears attempts when the run read fails', async () => {
      get.mockResolvedValueOnce(run).mockRejectedValueOnce(refused('not_found'));
      listAttempts.mockResolvedValue(first);
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      await store.load('missing');
      expect(store.attempts()).toBeNull();
      expect(store.attemptsStatus()).toBe('idle');
      expect(store.hasMoreAttempts()).toBe(false);
    });
  });
});
