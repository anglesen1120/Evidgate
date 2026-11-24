import { ApplicationRef, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { SecurityEventItem, SecurityEventList } from '@evidgate/sdk';
import { SecurityEventsDataService } from '../../../core/data/security-events-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { EMPTY_SECURITY_FILTERS, SecurityEventsStore } from './security-events.store';

const event: SecurityEventItem = {
  id: 'first',
  type: 'support_access',
  severity: 'critical',
  actor: { kind: 'user', id: 'actor1' },
  details: {},
  created_at: '2026-11-01T00:00:00Z',
};

describe('SecurityEventsStore', () => {
  const list = vi.fn<SecurityEventsDataService['list']>();
  const org = signal<{ id: string } | null>({ id: 'org1' });
  beforeEach(() => {
    org.set({ id: 'org1' });
    list.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        SecurityEventsStore,
        { provide: SecurityEventsDataService, useValue: { list } },
        { provide: SessionStore, useValue: { currentOrg: org } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function settle() {
    TestBed.tick();
    await TestBed.inject(ApplicationRef).whenStable();
  }
  it('narrows every supported query parameter and omits empty filters', async () => {
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    expect(list).toHaveBeenLastCalledWith('org1', { limit: 20 }, expect.any(AbortSignal));
    store.apply({
      projectId: 'project1',
      type: 'support_access',
      severity: 'critical',
      from: '2026-01-01T00:00:00Z',
      to: '2026-02-01T00:00:00Z',
    });
    await settle();
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      {
        limit: 20,
        project_id: 'project1',
        type: 'support_access',
        severity: 'critical',
        from: '2026-01-01T00:00:00Z',
        to: '2026-02-01T00:00:00Z',
      },
      expect.any(AbortSignal),
    );
    store.apply(EMPTY_SECURITY_FILTERS);
    await settle();
    expect(list).toHaveBeenLastCalledWith('org1', { limit: 20 }, expect.any(AbortSignal));
  });
  it('follows opaque cursors, returns to the previous page and resets on filters', async () => {
    list.mockResolvedValueOnce({ items: [], next_cursor: 'opaque+cursor' });
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    store.next();
    await settle();
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 20, cursor: 'opaque+cursor' },
      expect.any(AbortSignal),
    );
    expect(store.pageIndex()).toBe(1);
    store.apply({ ...EMPTY_SECURITY_FILTERS, severity: 'warning' });
    await settle();
    expect(store.pageIndex()).toBe(0);
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 20, severity: 'warning' },
      expect.any(AbortSignal),
    );
    list.mockResolvedValueOnce({ items: [], next_cursor: 'new-filter-cursor' });
    store.reload();
    await settle();
    store.next();
    await settle();
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 20, severity: 'warning', cursor: 'new-filter-cursor' },
      expect.any(AbortSignal),
    );
    store.previous();
    await settle();
    expect(store.pageIndex()).toBe(0);
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 20, severity: 'warning' },
      expect.any(AbortSignal),
    );
  });
  it('never publishes a superseded organization response', async () => {
    let finish: (value: SecurityEventList) => void = () => undefined;
    list.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const store = TestBed.inject(SecurityEventsStore);
    org.set({ id: 'old' });
    TestBed.tick();
    org.set({ id: 'new' });
    await settle();
    finish({ items: [], next_cursor: 'old-cursor' });
    await settle();
    expect(store.nextCursor()).toBeNull();
    expect(store.orgId()).toBe('new');
  });
  it('aborts a superseded filter request and never publishes its late response', async () => {
    let finish: (value: SecurityEventList) => void = () => undefined;
    let oldSignal: AbortSignal | undefined;
    list
      .mockImplementationOnce((_org, _query, signal) => {
        oldSignal = signal;
        return new Promise((resolve) => {
          finish = resolve;
        });
      })
      .mockResolvedValueOnce({ items: [], next_cursor: 'filtered-cursor' });
    const store = TestBed.inject(SecurityEventsStore);
    TestBed.tick();
    store.apply({ ...EMPTY_SECURITY_FILTERS, type: 'support_access' });
    await settle();
    expect(oldSignal?.aborted).toBe(true);
    expect(store.nextCursor()).toBe('filtered-cursor');
    finish({ items: [], next_cursor: 'superseded-cursor' });
    await settle();
    expect(store.nextCursor()).toBe('filtered-cursor');
    expect(store.filters().type).toBe('support_access');
    expect(store.pageIndex()).toBe(0);
  });
  it('replaces pages and refetches Previous without accumulating row history', async () => {
    list
      .mockResolvedValueOnce({ items: [event], next_cursor: 'second' })
      .mockResolvedValueOnce({ items: [{ ...event, id: 'second' }], next_cursor: 'third' })
      .mockResolvedValueOnce({ items: [{ ...event, id: 'third' }], next_cursor: null })
      .mockResolvedValueOnce({ items: [{ ...event, id: 'second' }], next_cursor: 'third' });
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    expect(store.rows().map((row) => row.id)).toEqual(['first']);
    expect(store.pageSize()).toBe(20);
    store.previous();
    await settle();
    expect(list).toHaveBeenCalledTimes(1);
    store.next();
    await settle();
    expect(store.rows().map((row) => row.id)).toEqual(['second']);
    expect(store.pageIndex()).toBe(1);
    expect(store.canNext()).toBe(true);
    store.next();
    await settle();
    expect(store.rows().map((row) => row.id)).toEqual(['third']);
    expect(store.pageIndex()).toBe(2);
    expect(store.canNext()).toBe(false);
    store.next();
    await settle();
    expect(list).toHaveBeenCalledTimes(3);
    store.previous();
    await settle();
    expect(store.rows().map((row) => row.id)).toEqual(['second']);
    expect(store.pageIndex()).toBe(1);
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 20, cursor: 'second' },
      expect.any(AbortSignal),
    );
  });
  it.each([20, 50, 100])(
    'resets the cursor and preserves filters when size becomes %s',
    async (size) => {
      list.mockResolvedValue({ items: [event], next_cursor: 'second' });
      const store = TestBed.inject(SecurityEventsStore);
      await settle();
      if (size === 20) store.setPageSize(50);
      store.apply({ ...EMPTY_SECURITY_FILTERS, projectId: 'project1' });
      await settle();
      store.next();
      await settle();
      store.setPageSize(size);
      await settle();
      expect(store.pageIndex()).toBe(0);
      expect(store.pageSize()).toBe(size);
      expect(list).toHaveBeenLastCalledWith(
        'org1',
        { limit: size, project_id: 'project1' },
        expect.any(AbortSignal),
      );
    },
  );
  it.each([0, 25, 200, NaN])(
    'refuses unsupported page size %s without another request',
    async (size) => {
      const store = TestBed.inject(SecurityEventsStore);
      await settle();
      store.setPageSize(size);
      await settle();
      expect(store.pageSize()).toBe(20);
      expect(list).toHaveBeenCalledTimes(1);
    },
  );
  it('refuses a size change after a failed read until Retry restores a ready page', async () => {
    list
      .mockResolvedValueOnce({ items: [event], next_cursor: 'second' })
      .mockRejectedValueOnce(new Error('cooldown'))
      .mockResolvedValueOnce({ items: [event], next_cursor: null });
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    store.next();
    await settle();
    expect(store.phase()).toBe('error');
    store.setPageSize(50);
    await settle();
    expect(store.pageSize()).toBe(20);
    expect(list).toHaveBeenCalledTimes(2);
    store.reload();
    await settle();
    store.setPageSize(50);
    await settle();
    expect(store.pageSize()).toBe(50);
  });

  it('keeps a failed target pending for Retry while retaining the successful rows and page number', async () => {
    list
      .mockResolvedValueOnce({ items: [event], next_cursor: 'second' })
      .mockRejectedValueOnce(new Error('network'))
      .mockResolvedValueOnce({ items: [{ ...event, id: 'second' }], next_cursor: null });
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    store.next();
    expect(store.rows().map((row) => row.id)).toEqual(['first']);
    expect(store.pageIndex()).toBe(0);
    await settle();
    expect(store.phase()).toBe('error');
    expect(store.hasPage()).toBe(true);
    expect(store.rows().map((row) => row.id)).toEqual(['first']);
    expect(store.pageIndex()).toBe(0);
    expect(store.loadError()?.retryable).toBe(true);
    store.reload();
    await settle();
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 20, cursor: 'second' },
      expect.any(AbortSignal),
    );
    expect(store.rows().map((row) => row.id)).toEqual(['second']);
    expect(store.pageIndex()).toBe(1);
    expect(store.loadError()).toBeNull();
  });
  it('retains the last successful page when Previous fails and retries the earlier cursor', async () => {
    list
      .mockResolvedValueOnce({ items: [event], next_cursor: 'second' })
      .mockResolvedValueOnce({ items: [{ ...event, id: 'second' }], next_cursor: null })
      .mockRejectedValueOnce(new Error('network'))
      .mockResolvedValueOnce({ items: [event], next_cursor: 'second' });
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    store.next();
    await settle();
    store.previous();
    await settle();
    expect(store.rows().map((row) => row.id)).toEqual(['second']);
    expect(store.pageIndex()).toBe(1);
    store.reload();
    await settle();
    expect(list).toHaveBeenLastCalledWith('org1', { limit: 20 }, expect.any(AbortSignal));
    expect(store.rows().map((row) => row.id)).toEqual(['first']);
    expect(store.pageIndex()).toBe(0);
  });
  it.each(['organization', 'filter', 'size'] as const)(
    'drops the retained page before a failed %s reset can disclose stale rows',
    async (scope) => {
      list
        .mockResolvedValueOnce({ items: [event], next_cursor: 'second' })
        .mockRejectedValueOnce(new Error('network'));
      const store = TestBed.inject(SecurityEventsStore);
      await settle();
      expect(store.rows()).toHaveLength(1);
      if (scope === 'organization') org.set({ id: 'org2' });
      else if (scope === 'filter')
        store.apply({ ...EMPTY_SECURITY_FILTERS, projectId: 'project2' });
      else store.setPageSize(50);
      expect(store.rows()).toEqual([]);
      await settle();
      expect(store.hasPage()).toBe(false);
      expect(store.rows()).toEqual([]);
      expect(store.pageIndex()).toBe(0);
      expect(store.phase()).toBe('error');
    },
  );
  it('refuses a repeated forward cursor but permits Previous', async () => {
    list
      .mockResolvedValueOnce({ items: [event], next_cursor: 'second' })
      .mockResolvedValueOnce({ items: [{ ...event, id: 'second' }], next_cursor: 'second' });
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    expect(store.canNext()).toBe(true);
    store.next();
    await settle();
    expect(store.canNext()).toBe(false);
    store.next();
    await settle();
    expect(list).toHaveBeenCalledTimes(2);
    store.previous();
    await settle();
    expect(store.pageIndex()).toBe(0);
    expect(list).toHaveBeenCalledTimes(3);
  });
  it('caps the cursor walk at 200 pages and permits a backward step', async () => {
    list.mockImplementation((_org, query) => {
      const page = query.cursor ? Number(query.cursor) : 1;
      return Promise.resolve({ items: [], next_cursor: String(page + 1) });
    });
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    for (let page = 1; page < 200; page++) {
      expect(store.canNext()).toBe(true);
      store.next();
      await settle();
    }
    expect(store.capped()).toBe(true);
    expect(store.pageIndex()).toBe(199);
    expect(store.canNext()).toBe(false);
    store.next();
    await settle();
    expect(list).toHaveBeenCalledTimes(200);
    store.previous();
    await settle();
    expect(store.pageIndex()).toBe(198);
    expect(store.capped()).toBe(false);
    expect(store.canNext()).toBe(true);
  });
});
