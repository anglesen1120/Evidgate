import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { RunDataService } from '../data/run-data.service';
import { manifestFromRun, UNAVAILABLE_MANIFEST } from './manifest-summary';
import { ATTEMPTS_PAGE_LIMIT, RunDetailStore } from './run-detail.store';
import { first, pendingBody, refused, run, second, deferred } from './run-detail-store.fixtures';

describe('RunDetailStore', () => {
  const get = vi.fn<(id: string) => Promise<RunStateDto>>();
  const listAttempts =
    vi.fn<(id: string, query?: { cursor?: string; limit?: number }) => Promise<RunAttemptPage>>();
  const getDecision = vi.fn<(id: string) => Promise<unknown>>();
  beforeEach(() => {
    get.mockReset();
    listAttempts.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    getDecision.mockReset().mockResolvedValue(pendingBody());
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        RunDetailStore,
        { provide: RunsDataService, useValue: { get, listAttempts } },
        { provide: RunDataService, useValue: { getDecision } },
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
    it('follows next_cursor once per call, appends in order and keeps earlier rows', async () => {
      get.mockResolvedValue(run);
      listAttempts.mockResolvedValueOnce(first).mockResolvedValueOnce(second);
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      const head = store.attempts()![0];
      await store.loadMoreAttempts();
      expect(listAttempts).toHaveBeenLastCalledWith(run.run_id, {
        limit: ATTEMPTS_PAGE_LIMIT,
        cursor: first.next_cursor,
      });
      expect(store.attempts()).toHaveLength(first.items.length + second.items.length);
      expect(store.attempts()![0]).toBe(head);
      expect(store.hasMoreAttempts()).toBe(false);
      await store.loadMoreAttempts();
      expect(listAttempts).toHaveBeenCalledTimes(2);
    });
    it('refuses a repeated cursor with typed copy and ends the walk with its rows kept', async () => {
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce(first)
        .mockResolvedValueOnce({ items: second.items, next_cursor: first.next_cursor });
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      await store.loadMoreAttempts();
      expect(store.attempts()).toHaveLength(first.items.length);
      // A plain Error would reach the panel as UNRESOLVED_FAILURE: blank message and code.
      expect(store.attemptsError()).toMatchObject({
        kind: 'unexpected',
        code: 'attempts_cursor_repeated',
        message: 'The attempts list returned the same page cursor again.',
        retryable: false,
      });
      expect(store.attemptsStatus()).toBe('ready');
      expect(store.hasMoreAttempts()).toBe(false);
      // The refused cursor is gone, so Load more cannot ask for it again.
      await store.loadMoreAttempts();
      expect(listAttempts).toHaveBeenCalledTimes(2);
    });
    it('drops the rows of a page a cursor cycle serves again (A → B → A) and ends the walk', async () => {
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce({ items: first.items, next_cursor: 'cycle-b' })
        .mockResolvedValueOnce({ items: second.items, next_cursor: 'cycle-c' })
        .mockResolvedValueOnce({ items: first.items, next_cursor: 'cycle-d' });
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      await store.loadMoreAttempts();
      expect(store.attempts()).toHaveLength(first.items.length + second.items.length);
      await store.loadMoreAttempts();
      expect(store.attempts()).toHaveLength(first.items.length + second.items.length);
      expect(store.attempts()!.map((row) => `${row.executionKey}/${row.attemptNo}`)).toEqual(
        [...first.items, ...second.items].map((item) => `${item.execution_key}/${item.attempt_no}`),
      );
      expect(store.attemptsError()).toMatchObject({
        code: 'attempts_page_without_new_rows',
        message: 'The attempts list returned a follow-up page with no new attempts.',
        retryable: false,
      });
      expect(store.hasMoreAttempts()).toBe(false);
      await store.loadMoreAttempts();
      expect(listAttempts).toHaveBeenCalledTimes(3);
    });
    it('ends the walk quietly on an empty terminal page', async () => {
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce({ items: first.items, next_cursor: 'tail' })
        .mockResolvedValueOnce({ items: [], next_cursor: null });
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      await store.loadMoreAttempts();
      expect(store.attemptsError()).toBeNull();
      expect(store.hasMoreAttempts()).toBe(false);
      expect(store.attempts()).toHaveLength(first.items.length);
    });
    it('appends only the attempts a follow-up page has not shown yet', async () => {
      const freshRow = {
        ...first.items[0]!,
        attempt_id: '018f0000-0000-7000-8000-0000000c1009',
        execution_key: 'f'.repeat(64),
        attempt_no: 9,
      };
      get.mockResolvedValue(run);
      listAttempts
        .mockResolvedValueOnce({ items: first.items, next_cursor: 'overlap-b' })
        .mockResolvedValueOnce({
          items: [first.items[1]!, freshRow, { ...freshRow }],
          next_cursor: null,
        });
      const store = TestBed.inject(RunDetailStore);
      await store.load(run.run_id);
      await vi.waitFor(() => expect(store.attemptsStatus()).toBe('ready'));
      await store.loadMoreAttempts();
      expect(store.attempts()).toHaveLength(first.items.length + 1);
      expect(store.attempts()!.at(-1)).toMatchObject({
        executionKey: freshRow.execution_key,
        attemptNo: 9,
      });
      expect(store.attemptsError()).toBeNull();
      expect(store.hasMoreAttempts()).toBe(false);
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
