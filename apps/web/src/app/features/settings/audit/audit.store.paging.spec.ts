import { TestBed } from '@angular/core/testing';
import type { AuditEntryDto, AuditListResponse } from '@evidgate/sdk';
import { AuditDataService } from '../../../core/data/audit-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { AuditStore, EMPTY_AUDIT_FILTERS } from './audit.store';

function entry(seq: number): AuditEntryDto {
  return {
    id: `event-${seq}`,
    seq,
    actor_type: 'user',
    actor_id: 'member',
    action: 'auth.login',
    prev_hash: 'previous',
    entry_hash: 'entry',
    created_at: '2026-10-25T00:00:00Z',
  };
}

function deferred() {
  let resolve: (value: AuditListResponse) => void = () => undefined;
  let reject: (reason: Error) => void = () => undefined;
  const promise = new Promise<AuditListResponse>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}

const refusal = new ApiRequestError({
  kind: 'unavailable',
  status: 503,
  code: 'unavailable',
  message: 'Try later',
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: 3,
});

describe('AuditStore cursor paging', () => {
  const list = vi.fn<AuditDataService['list']>();
  let store: InstanceType<typeof AuditStore>;
  let entries: AuditEntryDto[];

  beforeEach(() => {
    entries = Array.from({ length: 45 }, (_, index) => entry(index + 1));
    list.mockReset().mockImplementation((_org, query, cursor) => {
      const start = Number(cursor ?? 0);
      const end = start + (query.limit ?? 20);
      return Promise.resolve({
        items: entries.slice(start, end),
        next_cursor: end < entries.length ? String(end) : null,
      });
    });
    TestBed.configureTestingModule({
      providers: [AuditStore, { provide: AuditDataService, useValue: { list } }],
    });
    store = TestBed.inject(AuditStore);
  });
  afterEach(() => TestBed.resetTestingModule());

  it('replaces first, middle and last pages and refetches Previous with its original cursor', async () => {
    await store.load('org');
    expect(store.pageSize()).toBe(20);
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual(entries.slice(0, 20));
    await store.next('org');
    expect(store.page()).toBe(2);
    expect(store.rows()).toEqual(entries.slice(20, 40));
    await store.next('org');
    expect(store.page()).toBe(3);
    expect(store.rows()).toEqual(entries.slice(40));
    expect(store.hasNext()).toBe(false);
    await store.next('org');
    expect(list).toHaveBeenCalledTimes(3);
    await store.previous('org');
    expect(store.rows()).toEqual(entries.slice(20, 40));
    expect(list).toHaveBeenLastCalledWith('org', { limit: 20 }, '20');
    await store.previous('org');
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual(entries.slice(0, 20));
    expect(list).toHaveBeenLastCalledWith('org', { limit: 20 });
    await store.previous('org');
    expect(list).toHaveBeenCalledTimes(5);
    await store.next('org');
    expect(store.rows()).toEqual(entries.slice(20, 40));
  });

  it.each([0, 1, 20])('has no next page for %s entries', async (count) => {
    entries = entries.slice(0, count);
    await store.load('org');
    expect(store.rows()).toHaveLength(count);
    expect(store.page()).toBe(1);
    expect(store.hasNext()).toBe(false);
    await store.next('org');
    await store.previous('org');
    expect(list).toHaveBeenCalledTimes(1);
  });

  it.each([20, 50, 100])('resets cursor history when changing page size to %s', async (size) => {
    await store.load('org');
    await store.next('org');
    if (size === 20) await store.setPageSize('org', 50);
    await store.setPageSize('org', size);
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(size);
    expect(list).toHaveBeenLastCalledWith('org', { limit: size });
    expect(store.rows()).toEqual(entries.slice(0, size));
    await store.previous('org');
    expect(store.page()).toBe(1);
  });

  it('rejects unsupported page sizes and navigation for another scope', async () => {
    await store.load('org');
    await store.setPageSize('org', 25);
    await store.setPageSize('other', 100);
    await store.next('other');
    await store.previous('other');
    expect(store.pageSize()).toBe(20);
    expect(list).toHaveBeenCalledTimes(1);
  });

  it('resets to page one with the selected size and new filters', async () => {
    await store.load('org');
    await store.next('org');
    await store.load('org', { ...EMPTY_AUDIT_FILTERS, action: 'run.create' });
    expect(store.page()).toBe(1);
    expect(list).toHaveBeenLastCalledWith('org', { limit: 20, action: 'run.create' });
    await store.previous('org');
    expect(list).toHaveBeenCalledTimes(3);
  });

  it.each(['next', 'previous'] as const)(
    'retains rows and retries the exact failed %s target',
    async (direction) => {
      await store.load('org');
      await store.next('org');
      const rows = store.rows();
      list.mockRejectedValueOnce(refusal);
      await store[direction]('org');
      expect(store.rows()).toBe(rows);
      expect(store.page()).toBe(2);
      expect(store.pagingError()).toBe(refusal.error);
      expect(store.paging()).toBe(false);
      await store.retryPage('org');
      expect(store.page()).toBe(direction === 'next' ? 3 : 1);
      expect(store.rows()).toEqual(direction === 'next' ? entries.slice(40) : entries.slice(0, 20));
      expect(store.pagingError()).toBeNull();
    },
  );

  it('retains successful rows for an unexpected navigation failure without leaking its message', async () => {
    await store.load('org');
    list.mockRejectedValueOnce(new Error('private request data'));
    await store.next('org');
    expect(store.rows()).toEqual(entries.slice(0, 20));
    expect(store.page()).toBe(1);
    expect(store.pagingError()?.code).toBe('request_unresolved');
    expect(store.pagingError()?.message).not.toContain('private');
  });

  it('ignores duplicate navigation while a page is pending', async () => {
    await store.load('org');
    const pending = deferred();
    list.mockReturnValueOnce(pending.promise);
    const next = store.next('org');
    await store.next('org');
    await store.previous('org');
    expect(list).toHaveBeenCalledTimes(2);
    expect(store.page()).toBe(1);
    pending.resolve({ items: entries.slice(20, 40), next_cursor: '40' });
    await next;
    expect(store.page()).toBe(2);
  });

  it.each(['scope', 'filter', 'size', 'clear'] as const)(
    'discards pending page success after %s reset',
    async (reset) => {
      await store.load('org');
      const pending = deferred();
      list.mockReturnValueOnce(pending.promise);
      const next = store.next('org');
      if (reset === 'scope') await store.load('other');
      if (reset === 'filter')
        await store.load('org', { ...EMPTY_AUDIT_FILTERS, action: 'run.create' });
      if (reset === 'size') await store.setPageSize('org', 50);
      if (reset === 'clear') store.clearScope();
      const retained = store.rows();
      pending.resolve({ items: [entry(999)], next_cursor: 'stale' });
      await next;
      expect(store.rows()).toBe(retained);
      expect(store.page()).toBe(1);
      expect(store.paging()).toBe(false);
    },
  );

  it('discards an older scope failure after a new page succeeds', async () => {
    await store.load('org');
    const pending = deferred();
    list.mockReturnValueOnce(pending.promise);
    const next = store.next('org');
    await store.load('other');
    pending.reject(refusal);
    await next;
    expect(store.pagingError()).toBeNull();
    expect(store.rows()).toEqual(entries.slice(0, 20));
  });

  it('refuses a repeated forward cursor while permitting intentional Previous navigation', async () => {
    await store.load('org');
    list.mockResolvedValueOnce({ items: entries.slice(20, 40), next_cursor: '20' });
    await store.next('org');
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual(entries.slice(0, 20));
    expect(store.pagingError()?.code).toBe('request_unresolved');
  });

  it('keeps export independent from current-page navigation', async () => {
    await store.load('org');
    await store.next('org');
    const retained = store.rows();
    const csv = await store.exportCsv('org');
    expect(csv?.split('\r\n')).toHaveLength(47);
    expect(store.rows()).toBe(retained);
    expect(store.page()).toBe(2);
    expect(list).toHaveBeenLastCalledWith('org', { limit: 200 });
  });

  it('finishes a running export after a browse-only page-size reset', async () => {
    await store.load('org');
    const pending = deferred();
    list.mockReturnValueOnce(pending.promise);
    const exporting = store.exportCsv('org');
    await store.setPageSize('org', 50);
    expect(store.exportState()).toEqual({ phase: 'running', rows: 0 });
    expect(store.pageSize()).toBe(50);
    expect(store.page()).toBe(1);
    pending.resolve({ items: entries, next_cursor: null });
    const csv = await exporting;
    expect(csv?.split('\r\n')).toHaveLength(47);
    expect(store.exportState()).toEqual({ phase: 'done', rows: 45, capped: false });
    expect(store.rows()).toEqual(entries);
  });

  it('still invalidates a running export on an explicit list reload', async () => {
    await store.load('org');
    const pending = deferred();
    list.mockReturnValueOnce(pending.promise);
    const exporting = store.exportCsv('org');
    await store.load('org');
    pending.resolve({ items: entries, next_cursor: null });
    expect(await exporting).toBeNull();
    expect(store.exportState()).toEqual({ phase: 'idle' });
  });
});

describe('AuditStore empty cursor page', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('allows Previous after a successful empty last page', async () => {
    const list = vi
      .fn<AuditDataService['list']>()
      .mockResolvedValueOnce({ items: [entry(1)], next_cursor: 'end' })
      .mockResolvedValueOnce({ items: [], next_cursor: null })
      .mockResolvedValueOnce({ items: [entry(1)], next_cursor: 'end' });
    TestBed.configureTestingModule({
      providers: [AuditStore, { provide: AuditDataService, useValue: { list } }],
    });
    const store = TestBed.inject(AuditStore);
    await store.load('org');
    await store.next('org');
    expect(store.page()).toBe(2);
    expect(store.rows()).toEqual([]);
    expect(store.hasNext()).toBe(false);
    await store.previous('org');
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual([entry(1)]);
  });
});
