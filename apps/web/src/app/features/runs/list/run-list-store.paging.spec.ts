import { TestBed } from '@angular/core/testing';
import { RunDataService } from '../data/run-data.service';
import { RunListStore } from './run-list.store';
import { FakeRunDataService, rows, run, typedFailure } from './run-list-store.fixtures';

let data: FakeRunDataService;
let store: InstanceType<typeof RunListStore>;
beforeEach(() => {
  data = new FakeRunDataService();
  TestBed.configureTestingModule({
    providers: [RunListStore, { provide: RunDataService, useValue: data }],
  });
  store = TestBed.inject(RunListStore);
});

it('ignores superseded paging failures without clearing the current busy request', async () => {
  data.all = rows(60);
  await store.init('p-1');
  data.manual = true;
  const stale = store.next();
  const current = store.setFilters({ state: 'running' });
  data.pending[0]?.reject(typedFailure());
  await stale;
  expect(store.busy()).toBe(true);
  expect(store.pagingError()).toBeNull();
  expect(store.loadError()).toBeNull();
  data.pending[1]?.resolve({ items: [run(7, { state: 'running' })], next_cursor: null });
  await current;
  expect(store.rows().map((row) => row.run_id)).toEqual(['run-7']);
});
describe('cursor paging', () => {
  it('replaces first, middle and last pages and revisits recorded request cursors', async () => {
    data.all = rows(45);
    await store.init('p-1');
    await store.previous();
    expect(data.calls).toHaveLength(1);
    await store.next();
    const middleCursor = data.calls.at(-1)?.request.cursor;
    expect(store.page()).toBe(2);
    expect(store.rows().map((row) => row.run_id)).toEqual(
      rows(20, (i) => ({ run_id: `run-${i + 20}` })).map((row) => row.run_id),
    );
    await store.next();
    expect(store.page()).toBe(3);
    expect(store.rows()).toHaveLength(5);
    expect(store.hasNext()).toBe(false);
    await store.next();
    expect(data.calls).toHaveLength(3);
    await store.previous();
    expect(store.page()).toBe(2);
    expect(data.calls.at(-1)?.request.cursor).toBe(middleCursor);
    await store.previous();
    expect(store.page()).toBe(1);
    expect(data.calls.at(-1)?.request.cursor).toBeUndefined();
    expect(store.rows()[0]?.run_id).toBe('run-0');
  });

  it.each([0, 1, 20])('does not request another page for %i terminal rows', async (count) => {
    data.all = rows(count);
    await store.init('p-1');
    await store.next();
    await store.previous();
    expect(store.rows()).toHaveLength(count);
    expect(store.hasNext()).toBe(false);
    expect(data.calls).toHaveLength(1);
  });

  it.each(['next', 'previous'] as const)(
    'retains page and rows after failed %s and retries its target',
    async (direction) => {
      data.all = rows(60);
      await store.init('p-1');
      await store.next();
      const before = store.rows();
      const query = store.toQuery();
      const failure = typedFailure();
      data.failure = failure;
      await store[direction]();
      const failedCursor = data.calls.at(-1)?.request.cursor;
      expect(store.rows()).toBe(before);
      expect(store.page()).toBe(2);
      expect(store.toQuery()).toEqual(query);
      expect(store.status()).toBe('ready');
      expect(store.pagingError()).toBe(failure.error);
      expect(store.busy()).toBe(false);
      data.failure = null;
      await store.retryPage();
      expect(data.calls.at(-1)?.request.cursor).toBe(failedCursor);
      expect(store.page()).toBe(direction === 'next' ? 3 : 1);
      expect(store.pagingError()).toBeNull();
    },
  );

  it('uses a fixed failure for a rejected later request without leaking adapter text', async () => {
    data.all = rows(21);
    await store.init('p-1');
    data.failure = new Error('private adapter details');
    await store.next();
    expect(store.rows()).toHaveLength(20);
    expect(store.pagingError()?.code).toBe('request_unresolved');
    expect(store.pagingError()?.message).not.toContain('private');
  });

  it('discards a pending answer when page size changes', async () => {
    data.all = rows(120);
    await store.init('p-1');
    data.manual = true;
    const stale = store.next();
    const current = store.setPageSize(100);
    data.pending[1]?.resolve({ items: [run(100)], next_cursor: null });
    await current;
    data.pending[0]?.resolve({ items: [run(20)], next_cursor: 'stale' });
    await stale;
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(100);
    expect(store.rows().map((row) => row.run_id)).toEqual(['run-100']);
  });

  it.each([20, 50, 100])('resets to the first page with page size %i', async (size) => {
    data.all = rows(250);
    await store.init('p-1', { pageSize: '50', ref: 'main', decision: 'pending' });
    await store.next();
    await store.setPageSize(size);
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(size);
    expect(store.rows()).toHaveLength(size);
    expect(data.calls.at(-1)?.request).toEqual({
      limit: size,
      refName: 'main',
      decision: 'pending',
    });
    await store.previous();
    expect(store.page()).toBe(1);
  });

  it('rejects unsupported sizes without changing the page', async () => {
    data.all = rows(60);
    await store.init('p-1', { pageSize: '21' });
    await store.next();
    await store.setPageSize(21);
    expect(store.pageSize()).toBe(20);
    expect(store.page()).toBe(2);
    expect(data.calls).toHaveLength(2);
  });

  it('restores an opaque URL cursor directly without inventing previous pages', async () => {
    data.manual = true;
    const cursor = 'opaque+/=cursor';
    const request = store.init('p-1', { cursor, pageSize: '100' });
    expect(data.calls[0]?.request).toEqual({ cursor, limit: 100 });
    data.pending[0]?.resolve({ items: [run(7)], next_cursor: 'another-opaque' });
    await request;
    expect(store.toQuery()).toEqual({ cursor, pageSize: '100' });
    expect(store.page()).toBe(1);
    await store.previous();
    expect(data.calls).toHaveLength(1);
    const next = store.next();
    data.pending[1]?.resolve({ items: [run(8)], next_cursor: null });
    await next;
    const previous = store.previous();
    expect(data.calls.at(-1)?.request.cursor).toBe(cursor);
    data.pending[2]?.resolve({ items: [run(7)], next_cursor: 'another-opaque' });
    await previous;
    expect(store.page()).toBe(1);
  });

  it('blocks duplicate navigation and retry requests while a page is pending', async () => {
    data.all = rows(60);
    await store.init('p-1');
    data.manual = true;
    const next = store.next();
    await store.next();
    await store.previous();
    await store.retryPage();
    expect(data.calls).toHaveLength(2);
    expect(store.busy()).toBe(true);
    data.pending[0]?.resolve({ items: [run(20)], next_cursor: null });
    await next;
    expect(store.busy()).toBe(false);
  });

  it.each(['state', 'scope', 'decision', 'refName'] as const)(
    'discards pending pages when %s changes',
    async (filter) => {
      data.all = rows(60);
      await store.init('p-1');
      data.manual = true;
      const stale = store.next();
      const patch = {
        state: 'running',
        scope: 'rerun',
        decision: 'PASS',
        refName: 'main',
      } as const;
      const current = store.setFilters({ [filter]: patch[filter] });
      data.pending[1]?.resolve({ items: [run(7)], next_cursor: null });
      await current;
      data.pending[0]?.resolve({ items: [run(20)], next_cursor: 'stale' });
      await stale;
      expect(store.rows().map((row) => row.run_id)).toEqual(['run-7']);
      expect(store.page()).toBe(1);
      expect(store.toQuery()['cursor']).toBeUndefined();
      expect(store.hasNext()).toBe(false);
    },
  );

  it('discards a pending page when project scope changes', async () => {
    data.all = rows(60);
    await store.init('p-1');
    data.manual = true;
    const stale = store.next();
    const current = store.init('p-2');
    data.pending[1]?.resolve({ items: [run(2)], next_cursor: null });
    await current;
    data.pending[0]?.resolve({ items: [run(1)], next_cursor: 'stale' });
    await stale;
    expect(store.rows().map((row) => row.run_id)).toEqual(['run-2']);
    expect(store.page()).toBe(1);
  });
});
