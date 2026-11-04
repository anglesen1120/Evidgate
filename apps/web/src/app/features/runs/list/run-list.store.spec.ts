import { TestBed } from '@angular/core/testing';
import { RunDataService } from '../data/run-data.service';
import {
  EMPTY_RUN_LIST_FILTERS,
  RUN_LIST_AUTO_PAGES,
  RUN_LIST_PAGE_LIMIT,
  RunListStore,
} from './run-list.store';
import { FakeRunDataService, flush, rows, run, typedFailure } from './run-list-store.fixtures';

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
      expect(store.hasMore()).toBe(true);
      expect(store.autoLoadCapped()).toBe(false);
      expect(store.loadError()).toBeNull();
      expect(data.calls).toEqual([{ projectId: 'p-1', request: { limit: RUN_LIST_PAGE_LIMIT } }]);
    });

    it('reads the filters the URL carries and sends only the ref filter', async () => {
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
      // `state`, `decision` and `scope` are client-side; the wire carries the page size and `ref`.
      expect(data.calls[0]?.request).toEqual({
        limit: RUN_LIST_PAGE_LIMIT,
        refName: 'refs/heads/main',
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
      expect(store.hasMore()).toBe(false);
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
    it('tops up pages until the filtered view holds one page', async () => {
      // The first page is all `complete`; the runs the filter is about start on page two.
      data.all = rows(200, (index) => ({ state: index < 50 ? 'complete' : 'running' }));

      await store.init('p-1', { state: 'running' });

      expect(data.calls).toHaveLength(2);
      expect(store.loadedCount()).toBe(100);
      expect(store.visibleRows()).toHaveLength(RUN_LIST_PAGE_LIMIT);
      expect(store.visibleRows().every((row) => row.state === 'running')).toBe(true);
      expect(store.autoLoadCapped()).toBe(false);
      expect(store.hasMore()).toBe(true);
      // No row is served twice by the walk, so none is rendered twice.
      expect(new Set(store.rows().map((row) => row.run_id)).size).toBe(store.rows().length);
    });

    it('stops at the trigger bound when no row matches, and says so', async () => {
      data.all = rows(1000, () => ({ state: 'queued' }));

      await store.init('p-1', { state: 'complete' });

      // One first page plus the bound's pages, no matter how many the server still names.
      expect(data.calls).toHaveLength(1 + RUN_LIST_AUTO_PAGES);
      expect(store.autoLoadCapped()).toBe(true);
      expect(store.browseCapped()).toBe(true);
      expect(store.hasMore()).toBe(true);
      expect(store.visibleRows()).toEqual([]);
      expect(store.isEmpty()).toBe(true);
      expect(store.isFiltered()).toBe(true);
      expect(store.loadedCount()).toBe(RUN_LIST_PAGE_LIMIT * (1 + RUN_LIST_AUTO_PAGES));
    });

    it('ignores a stale pending URL filter and only matches explicit verdicts', async () => {
      data.all = [
        run(1, { decision: 'PASS' }),
        run(2, { decision: null }),
        run(3),
        run(4, { decision: 'FAIL' }),
      ];

      await store.init('p-1', { decision: 'pending' });
      expect(store.visibleRows()).toHaveLength(4);
      expect(store.filters().decision).toBe('');
      expect(store.toQuery()).not.toHaveProperty('decision');

      await store.setFilters({ decision: 'PASS' });

      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-1']);
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

    it('re-filters the loaded rows without a request when no page is needed', async () => {
      data.all = rows(10, (index) => ({ state: index === 0 ? 'canceled' : 'complete' }));
      await store.init('p-1');
      expect(data.calls).toHaveLength(1);

      await store.setFilters({ state: 'canceled' });

      expect(data.calls).toHaveLength(1);
      expect(store.loadedCount()).toBe(10);
      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-0']);
      expect(store.isFiltered()).toBe(true);
      expect(store.isEmpty()).toBe(false);
    });

    it('restarts the walk when the ref filter changes', async () => {
      data.all = rows(4);
      await store.init('p-1');
      await store.setFilters({ state: 'complete' });
      expect(data.calls).toHaveLength(1);

      await store.setFilters({ refName: 'refs/heads/main' });

      expect(data.calls).toHaveLength(2);
      expect(data.calls[1]?.request).toEqual({
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

  describe('paging', () => {
    it('appends the next page and stops at a null cursor', async () => {
      data.all = rows(120);
      await store.init('p-1');

      await store.loadMore();

      expect(store.loadedCount()).toBe(100);
      expect(store.visibleRows()).toHaveLength(100);
      expect(store.autoLoadCapped()).toBe(false);
      expect(new Set(store.rows().map((row) => row.run_id)).size).toBe(100);

      await store.loadMore();

      expect(store.loadedCount()).toBe(120);
      expect(store.hasMore()).toBe(false);

      // No cursor left: a further click reads nothing.
      await store.loadMore();
      expect(data.calls).toHaveLength(3);
    });

    it('does not report the list empty while a top-up page is still in flight', async () => {
      data.manual = true;
      // Page 1 holds no row the filter matches, so the store publishes `ready` and immediately
      // starts the top-up walk. The empty pattern must not flash for that window.
      const read = store.init('p-1', { state: 'running' });
      data.pending[0]?.resolve({
        items: rows(RUN_LIST_PAGE_LIMIT, () => ({ state: 'complete' })),
        next_cursor: '50',
      });
      await flush();

      expect(store.status()).toBe('ready');
      expect(store.loadingMore()).toBe(true);
      expect(store.visibleRows()).toEqual([]);
      expect(store.isEmpty()).toBe(false);

      data.pending[1]?.resolve({
        items: rows(3, (index) => ({ run_id: `match-${index}`, state: 'running' })),
        next_cursor: null,
      });
      await read;

      expect(store.loadingMore()).toBe(false);
      expect(store.isEmpty()).toBe(false);
      expect(store.visibleRows()).toHaveLength(3);
    });

    it('does nothing before the first read', async () => {
      await store.loadMore();

      expect(data.calls).toEqual([]);
    });

    it('refuses a repeated cursor as a contract violation', async () => {
      // The filter matches none of the pages, so the walk keeps following — and the fake names the
      // same cursor every time.
      data.all = rows(120, () => ({ state: 'queued' }));
      data.repeatCursor = true;

      await expect(store.init('p-1', { state: 'running' })).rejects.toThrow(
        /repeated cursor "same"/,
      );
    });

    it('keeps the rows and reports a typed paging failure, then retries the same cursor', async () => {
      data.all = rows(120);
      await store.init('p-1');
      const failure = typedFailure();
      data.failure = failure;
      data.failOnCall = 2;

      await store.loadMore();

      expect(store.pagingError()).toBe(failure.error);
      expect(store.loadedCount()).toBe(RUN_LIST_PAGE_LIMIT);
      expect(store.loadingMore()).toBe(false);
      expect(store.status()).toBe('ready');

      // A refused cursor was never followed, so the control that shows the failure can retry it.
      data.failure = null;
      await store.loadMore();

      expect(store.pagingError()).toBeNull();
      expect(store.loadedCount()).toBe(100);
      expect(data.calls[2]?.request.cursor).toBe(data.calls[1]?.request.cursor);
    });

    it('lets the newest read win when two answers arrive out of order', async () => {
      data.manual = true;

      const first = store.init('p-1');
      const second = store.init('p-2');
      expect(data.pending).toHaveLength(2);

      data.pending[1]?.resolve({ items: [run(2)], next_cursor: null });
      await second;
      data.pending[0]?.resolve({ items: [run(1), run(10)], next_cursor: null });
      await first;

      expect(store.visibleRows().map((row) => row.run_id)).toEqual(['run-2']);
      expect(data.calls.map((call) => call.projectId)).toEqual(['p-1', 'p-2']);
    });

    it('does not start a second walk while a page is in flight', async () => {
      data.manual = true;
      // The filter matches none of page 1, so a top-up for the next cursor is in flight.
      const read = store.init('p-1', { state: 'running' });
      data.pending[0]?.resolve({
        items: rows(RUN_LIST_PAGE_LIMIT, () => ({ state: 'complete' })),
        next_cursor: '50',
      });
      await flush();
      expect(data.pending).toHaveLength(2);
      expect(data.calls).toHaveLength(2);

      await store.setFilters({ state: 'complete' });

      // The chip tap applies the filters but leaves the running walk alone: no cursor is requested
      // twice and no page can be appended twice.
      expect(data.calls).toHaveLength(2);
      expect(store.filters().state).toBe('complete');

      data.pending[1]?.resolve({
        items: rows(RUN_LIST_PAGE_LIMIT, (index) => ({ run_id: `page-2-${index}` })),
        next_cursor: null,
      });
      await flush();
      await read;

      expect(data.calls).toHaveLength(2);
      expect(store.loadedCount()).toBe(2 * RUN_LIST_PAGE_LIMIT);
    });

    it('drops a page of a superseded read instead of appending it', async () => {
      data.all = rows(120);
      await store.init('p-1');
      data.manual = true;

      const slow = store.loadMore();
      const restart = store.reload();
      expect(data.pending).toHaveLength(2);

      // The restart's first page answers while the earlier follow is still in flight.
      data.pending[1]?.resolve({ items: [run(0)], next_cursor: null });
      await restart;
      expect(store.loadedCount()).toBe(1);

      data.pending[0]?.resolve({
        items: rows(50, (index) => ({ run_id: `stale-${index}` })),
        next_cursor: null,
      });
      await slow;

      expect(store.rows().map((row) => row.run_id)).toEqual(['run-0']);
    });
  });
});
