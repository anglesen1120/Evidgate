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

  it.each(['current', 'earlier'] as const)(
    'retains the successful page when a response repeats the %s request cursor',
    async (repeated) => {
      data.all = rows(80);
      await store.init('p-1');
      await store.next();
      const earlier = data.calls.at(-1)?.request.cursor;
      await store.next();
      const current = data.calls.at(-1)?.request.cursor;
      const retained = store.rows();
      const query = store.toQuery();
      const cursor = repeated === 'current' ? current : earlier;
      if (!cursor) throw new Error('Expected a visited request cursor');
      data.manual = true;
      const next = store.next();
      data.pending[0]?.resolve({ items: [run(999)], next_cursor: cursor });
      await next;
      expect(store.page()).toBe(3);
      expect(store.rows()).toBe(retained);
      expect(store.toQuery()).toEqual(query);
      expect(store.pagingError()?.code).toBe('request_unresolved');
      const retry = store.retryPage();
      data.pending[1]?.resolve({ items: [run(60)], next_cursor: null });
      await retry;
      expect(store.page()).toBe(4);
      expect(store.rows()).toEqual([run(60)]);
      expect(store.pagingError()).toBeNull();
    },
  );

  it('rejects a continuation equal to its own target and retries the exact request', async () => {
    data.all = rows(60);
    await store.init('p-1');
    const retained = store.rows();
    const query = store.toQuery();
    data.manual = true;
    const next = store.next();
    const target = data.calls.at(-1)?.request;
    if (!target?.cursor) throw new Error('Expected a next request cursor');
    data.pending[0]?.resolve({ items: [run(999)], next_cursor: target.cursor });
    await next;
    expect(store.rows()).toBe(retained);
    expect(store.page()).toBe(1);
    expect(store.toQuery()).toEqual(query);
    expect(store.pagingError()?.code).toBe('request_unresolved');
    const retry = store.retryPage();
    expect(data.calls.at(-1)?.request).toEqual(target);
    data.pending[1]?.resolve({ items: [run(20)], next_cursor: null });
    await retry;
    expect(store.page()).toBe(2);
    expect(store.rows()).toEqual([run(20)]);
    expect(store.hasNext()).toBe(false);
  });

  it('rejects a restored first-page cursor loop without publishing its rows', async () => {
    data.manual = true;
    const request = store.init('p-1', { cursor: 'restored' });
    data.pending[0]?.resolve({ items: [run(999)], next_cursor: 'restored' });
    await request;
    expect(store.status()).toBe('error');
    expect(store.rows()).toEqual([]);
    expect(store.loadError()?.code).toBe('request_unresolved');
    const retry = store.retryPage();
    expect(data.calls.at(-1)?.request.cursor).toBe('restored');
    data.pending[1]?.resolve({ items: [run(1)], next_cursor: null });
    await retry;
    expect(store.status()).toBe('ready');
    expect(store.rows()).toEqual([run(1)]);
  });

  it('allows Previous to return the cursor of a discarded later page', async () => {
    data.all = rows(80);
    await store.init('p-1');
    await store.next();
    await store.next();
    const discardedCursor = data.calls.at(-1)?.request.cursor;
    await store.previous();
    expect(store.page()).toBe(2);
    expect(store.nextCursor()).toBe(discardedCursor);
    expect(store.pagingError()).toBeNull();
    await store.next();
    expect(store.page()).toBe(3);
    expect(store.rows()[0]?.run_id).toBe('run-40');
  });

  it.each(['clear', 'reload'] as const)(
    '%s recovers a failed restored first cursor without reusing it',
    async (action) => {
      data.failure = typedFailure();
      await store.init('p-1', { cursor: 'expired', pageSize: '50' });
      expect(store.toQuery()['cursor']).toBe('expired');
      data.failure = null;
      data.all = rows(60);
      if (action === 'clear') {
        await store.setFilters({ state: '', scope: '', decision: '', refName: '' });
      } else {
        await store.reload();
      }
      expect(data.calls.at(-1)?.request).toEqual({ limit: 50 });
      expect(store.toQuery()).toEqual({ pageSize: '50' });
      expect(store.status()).toBe('ready');
      expect(store.rows()).toHaveLength(50);
      expect(store.loadError()).toBeNull();
    },
  );

  it('keeps the expired cursor dropped when recovery fails and retries the null request', async () => {
    data.failure = typedFailure();
    await store.init('p-1', { cursor: 'expired', pageSize: '100' });
    await store.reload();
    expect(store.status()).toBe('error');
    expect(store.toQuery()).toEqual({ pageSize: '100' });
    expect(data.calls.at(-1)?.request).toEqual({ limit: 100 });
    data.failure = null;
    data.all = [run(1)];
    await store.retryPage();
    expect(data.calls.at(-1)?.request).toEqual({ limit: 100 });
    expect(store.rows()).toEqual([run(1)]);
  });

  it('reloads a successfully loaded current cursor rather than resetting to the first dataset page', async () => {
    data.all = rows(60);
    await store.init('p-1');
    await store.next();
    const current = data.calls.at(-1)?.request;
    const retained = store.rows();
    await store.reload();
    expect(data.calls.at(-1)?.request).toEqual(current);
    expect(store.rows()).toEqual(retained);
    expect(store.toQuery()['cursor']).toBe(current?.cursor);
  });

  it.each(['state', 'scope'] as const)(
    'retains first-page rows when only local %s changes and clears',
    async (filter) => {
      data.all = [run(0), run(1, { state: 'running', rerun_label: 'retry' })];
      await store.init('p-1');
      const retained = store.rows();
      await store.setFilters(filter === 'state' ? { state: 'running' } : { scope: 'rerun' });
      expect(store.rows()).toBe(retained);
      expect(store.visibleRows()).toEqual([data.all[1]]);
      expect(data.calls).toHaveLength(1);
      await store.setFilters({ [filter]: '' });
      expect(store.rows()).toBe(retained);
      expect(store.visibleRows()).toEqual(data.all);
      expect(data.calls).toHaveLength(1);
    },
  );

  it.each(['state', 'scope'] as const)(
    'restarts later pages when local %s changes',
    async (filter) => {
      data.all = rows(60);
      await store.init('p-1');
      await store.next();
      await store.setFilters(filter === 'state' ? { state: 'running' } : { scope: 'rerun' });
      expect(data.calls).toHaveLength(3);
      expect(data.calls.at(-1)?.request.cursor).toBeUndefined();
      expect(store.page()).toBe(1);
      expect(store.rows()).toEqual(rows(20));
    },
  );

  it('does not refetch or clear the cursor when a restored known first page changes local state', async () => {
    data.manual = true;
    const first = store.init('p-1', { cursor: 'restored' });
    data.pending[0]?.resolve({ items: [run(1)], next_cursor: null });
    await first;
    const retained = store.rows();
    await store.setFilters({ state: 'running' });
    expect(data.calls).toHaveLength(1);
    expect(store.rows()).toBe(retained);
    expect(store.visibleRows()).toEqual([]);
    expect(store.toQuery()).toEqual({ cursor: 'restored', state: 'running' });
  });

  it('refetches first-page rows when a local filter and a server filter both change', async () => {
    data.all = rows(30);
    await store.init('p-1');
    await store.setFilters({ state: 'running', decision: 'PASS' });
    expect(data.calls).toHaveLength(2);
    expect(data.calls.at(-1)?.request).toEqual({ limit: 20, decision: 'PASS' });
  });

  it('restarts a failed first load when a local filter changes', async () => {
    data.failure = typedFailure();
    await store.init('p-1');
    data.failure = null;
    data.all = [run(1, { state: 'running' })];
    await store.setFilters({ state: 'running' });
    expect(data.calls).toHaveLength(2);
    expect(store.visibleRows()).toEqual(data.all);
    expect(store.status()).toBe('ready');
  });

  it('abandons a failed Next target after successful Previous and does not retry it', async () => {
    data.all = rows(60);
    await store.init('p-1');
    await store.next();
    data.failure = typedFailure();
    await store.next();
    expect(store.page()).toBe(2);
    data.failure = null;
    await store.previous();
    expect(store.page()).toBe(1);
    const calls = data.calls.length;
    await store.retryPage();
    expect(data.calls).toHaveLength(calls);
    expect(store.rows()).toEqual(rows(20));
    await store.next();
    expect(store.page()).toBe(2);
    expect(store.rows()[0]?.run_id).toBe('run-20');
  });
});
