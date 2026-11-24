import { TestBed } from '@angular/core/testing';
import { RunDataService } from '../data/run-data.service';
import { EMPTY_RUN_LIST_FILTERS, RUN_LIST_PAGE_LIMIT, RunListStore } from './run-list.store';
import { FakeRunDataService, rows, run, typedFailure } from './run-list-store.fixtures';

describe('RunListStore (09.3.1-T1)', () => {
  let data: FakeRunDataService;
  let store: InstanceType<typeof RunListStore>;

  beforeEach(() => {
    data = new FakeRunDataService();
    TestBed.configureTestingModule({
      providers: [RunListStore, { provide: RunDataService, useValue: data }],
    });
    store = TestBed.inject(RunListStore);
  });

  describe('init', () => {
    it('reads the first page at the page limit and offers the next one', async () => {
      data.all = rows(120);
      expect(store.status()).toBe('idle');

      await store.init('p-1');

      expect(store.status()).toBe('ready');
      expect(store.rows()).toHaveLength(RUN_LIST_PAGE_LIMIT);
      expect(store.loadedCount()).toBe(RUN_LIST_PAGE_LIMIT);
      expect(store.visibleRows()).toHaveLength(RUN_LIST_PAGE_LIMIT);
      expect(store.hasNext()).toBe(true);
      expect(store.page()).toBe(1);
      expect(store.loadError()).toBeNull();
      expect(data.calls).toEqual([{ projectId: 'p-1', request: { limit: RUN_LIST_PAGE_LIMIT } }]);
    });

    it('reads URL filters and sends decision and ref filters to the server', async () => {
      data.all = rows(3);

      await store.init('p-1', {
        state: 'complete',
        decision: 'PASS',
        scope: 'rerun',
        ref: 'refs/heads/main',
      });

      expect(store.filters()).toEqual({
        state: 'complete',
        decision: 'PASS',
        scope: 'rerun',
        refName: 'refs/heads/main',
      });
      // State and scope remain local; the server owns decision and ref filtering.
      expect(data.calls[0]?.request).toEqual({
        limit: RUN_LIST_PAGE_LIMIT,
        refName: 'refs/heads/main',
        decision: 'PASS',
      });
    });

    it('drops unknown and invalid URL values instead of sending them', async () => {
      data.all = rows(1);

      await store.init('p-1', {
        state: 'exploded',
        decision: 'pass',
        scope: 'all',
        ref: 'line\nbreak',
      });

      expect(store.filters()).toEqual(EMPTY_RUN_LIST_FILTERS);
      expect(data.calls[0]?.request).toEqual({ limit: RUN_LIST_PAGE_LIMIT });

      await store.init('p-1', { ref: 'x'.repeat(256) });

      expect(store.filters().refName).toBe('');
      expect(data.calls[1]?.request).toEqual({ limit: RUN_LIST_PAGE_LIMIT });
    });

    it('keeps a ref name that carries a Unicode format character', async () => {
      data.all = rows(1);

      // U+202E is a legal `ref_name` (the server refuses control characters only, plan Note 226).
      await store.init('p-1', { ref: 'feature/\u202Eexe.txt' });

      expect(store.filters().refName).toBe('feature/\u202Eexe.txt');
      expect(data.calls[0]?.request.refName).toBe('feature/\u202Eexe.txt');
    });

    it('round-trips its filters through toQuery', async () => {
      data.all = rows(3);
      const query = {
        state: 'running',
        scope: 'original',
        ref: 'refs/tags/v1',
      };

      await store.init('p-1', query);

      expect(store.toQuery()).toEqual(query);

      const applied = store.filters();
      await store.init('p-1', {});

      // A second read from the serialised query reproduces the same filter set and the same wire
      // filters — nothing is lost between the URL and the request.
      await store.init('p-1', { ...query, bogus: 'dropped' });
      expect(store.filters()).toEqual(applied);
      expect(data.calls.at(-1)?.request).toEqual({
        limit: RUN_LIST_PAGE_LIMIT,
        refName: 'refs/tags/v1',
      });
    });

    it('serialises only the non-empty filters', async () => {
      await store.init('p-1', {});

      expect(store.toQuery()).toEqual({});
    });

    it('is the error state with the server’s failure, and holds no rows', async () => {
      const failure = typedFailure();
      data.failure = failure;

      await store.init('p-1');

      expect(store.status()).toBe('error');
      expect(store.loadError()).toBe(failure.error);
      expect(store.rows()).toEqual([]);
      expect(store.hasNext()).toBe(false);
    });

    it('reports a rejection that is not the server’s answer as a fixed failure', async () => {
      data.failure = new Error('adapter cancelled the request for project p-1');

      await store.init('p-1');

      expect(store.status()).toBe('error');
      expect(store.loadError()).toMatchObject({ kind: 'unexpected', code: 'request_unresolved' });
      expect(store.loadError()?.message).not.toContain('p-1');
    });
  });

  describe('client-side filters', () => {
    it('filters only the current page and never automatically fetches matches', async () => {
      data.all = rows(1000, (index) => ({ state: index < 20 ? 'queued' : 'running' }));
      await store.init('p-1', { state: 'running' });
      expect(data.calls).toHaveLength(1);
      expect(store.rows()).toHaveLength(20);
      expect(store.visibleRows()).toEqual([]);
      expect(store.isEmpty()).toBe(true);
      expect(store.hasNext()).toBe(true);
      await store.next();
      expect(data.calls).toHaveLength(2);
      expect(store.visibleRows()).toHaveLength(20);
      expect(store.page()).toBe(2);
    });

    it.each(['pending', 'PASS', 'FAIL', 'INCONCLUSIVE'] as const)(
      'round-trips and requests the %s decision filter',
      async (decision) => {
        data.all = [
          run(1, { decision: 'PASS' }),
          run(2, { decision: null }),
          run(3, { decision: 'INCONCLUSIVE' }),
          run(4, { decision: 'FAIL' }),
        ];
        await store.init('p-1', { decision });
        expect(store.visibleRows()).toEqual(
          data.all.filter((row) => row.decision === (decision === 'pending' ? null : decision)),
        );
        expect(store.toQuery()).toEqual({ decision });
        expect(data.calls[0]?.request).toEqual({ limit: RUN_LIST_PAGE_LIMIT, decision });
        await store.setFilters({ decision: '' });
        expect(data.calls.at(-1)?.request).toEqual({ limit: RUN_LIST_PAGE_LIMIT });
        expect(store.visibleRows()).toHaveLength(4);
        expect(store.toQuery()).toEqual({});
      },
    );

    it('follows empty decision-filtered pages only on explicit navigation', async () => {
      data.pageSizeOverride = 1;
      data.all = rows(3, (index) => ({ decision: index === 1 ? 'PASS' : 'FAIL' }));
      await store.init('p-1', { decision: 'FAIL', ref: 'main' });
      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-0']);
      await store.next();
      expect(store.rows()).toEqual([]);
      expect(store.page()).toBe(2);
      expect(store.hasNext()).toBe(true);
      await store.next();
      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-2']);
      expect(store.hasNext()).toBe(false);
      expect(data.calls).toHaveLength(3);
      expect(
        data.calls.every(
          ({ request }) => request.decision === 'FAIL' && request.refName === 'main',
        ),
      ).toBe(true);
    });

    it('discards an in-flight page when the decision filter changes', async () => {
      data.manual = true;
      const previous = store.init('p-1', { decision: 'PASS' });
      const current = store.setFilters({ decision: 'pending' });
      expect(data.calls.map(({ request }) => request.decision)).toEqual(['PASS', 'pending']);
      data.pending[1]?.resolve({ items: [run(2)], next_cursor: null });
      await current;
      data.pending[0]?.resolve({ items: [run(1, { decision: 'PASS' })], next_cursor: null });
      await previous;
      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-2']);
      expect(store.toQuery()).toEqual({ decision: 'pending' });
    });

    it('classifies rerun, reprocess and original scope from the labels', async () => {
      data.all = [
        run(1),
        run(2, { parent_run_id: 'run-1', rerun_label: 'subset:smoke' }),
        run(3, { parent_run_id: 'run-1', reprocess_label: 'evidence:reprocess' }),
        run(4, { parent_run_id: 'run-1' }),
        // Both labels: the two scopes are independent, so this row is in each of them.
        run(5, {
          parent_run_id: 'run-1',
          rerun_label: 'subset:smoke',
          reprocess_label: 'evidence:reprocess',
        }),
      ];

      await store.init('p-1');
      await store.setFilters({ scope: 'rerun' });
      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-2', 'run-5']);

      await store.setFilters({ scope: 'reprocess' });
      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-3', 'run-5']);

      await store.setFilters({ scope: 'original' });
      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-1']);

      await store.setFilters({ scope: '' });
      expect(store.visibleRows()).toHaveLength(5);
    });

    it('restarts page one when a local filter changes', async () => {
      data.all = rows(10, (index) => ({ state: index === 0 ? 'canceled' : 'complete' }));
      await store.init('p-1');
      expect(data.calls).toHaveLength(1);

      await store.setFilters({ state: 'canceled' });

      expect(data.calls).toHaveLength(2);
      expect(store.loadedCount()).toBe(10);
      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-0']);
      expect(store.isFiltered()).toBe(true);
      expect(store.isEmpty()).toBe(false);
    });

    it('restarts page one when the ref filter changes', async () => {
      data.all = rows(4);
      await store.init('p-1');
      await store.setFilters({ state: 'complete' });
      expect(data.calls).toHaveLength(2);

      await store.setFilters({ refName: 'refs/heads/main' });

      expect(data.calls).toHaveLength(3);
      expect(data.calls[2]?.request).toEqual({
        limit: RUN_LIST_PAGE_LIMIT,
        refName: 'refs/heads/main',
      });
      expect(store.rows()).toHaveLength(4);
      expect(store.filters()).toEqual({
        ...EMPTY_RUN_LIST_FILTERS,
        state: 'complete',
        refName: 'refs/heads/main',
      });
    });

    it('reload re-reads the first page of the current filters', async () => {
      data.all = rows(4);
      await store.init('p-1', { state: 'complete', ref: 'refs/heads/main' });

      await store.reload();

      expect(data.calls).toHaveLength(2);
      expect(data.calls[1]?.request).toEqual({
        limit: RUN_LIST_PAGE_LIMIT,
        refName: 'refs/heads/main',
      });
      expect(store.filters()).toEqual({
        state: 'complete',
        decision: '',
        scope: '',
        refName: 'refs/heads/main',
      });
    });
  });
});
