import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ManualCaseListResponse } from '@evidgate/sdk';
import { apiError } from '../../shared/testing/http-data.testing';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringCaseTableStore } from './authoring-case-table.store';

const page = (id: string, cursor: string | null): ManualCaseListResponse => ({
  items: [
    {
      case_id: id,
      code: id,
      title: id,
      node_id: 'node',
      checklist_item_id: 'item',
      priority: 'high',
      state: 'draft',
      version: 1,
      linked_live_count: 0,
      linked_case_ids: [],
    },
  ],
  next_cursor: cursor,
});

describe('Authoring case cursor pages', () => {
  const list = vi.fn<AuthoringDataService['listManualCases']>();
  let store: InstanceType<typeof AuthoringCaseTableStore>;
  beforeEach(() => {
    list.mockReset().mockResolvedValue(page('first', 'second'));
    TestBed.configureTestingModule({
      providers: [
        AuthoringCaseTableStore,
        { provide: AuthoringDataService, useValue: { listManualCases: list } },
      ],
    });
    store = TestBed.inject(AuthoringCaseTableStore);
  });
  afterEach(() => TestBed.resetTestingModule());

  it('requests 20 by default and navigates first, middle, last and previous pages', async () => {
    await store.init('project', {});
    expect(list.mock.lastCall?.[1].limit).toBe(20);
    expect(store.page()).toBe(1);
    list.mockResolvedValueOnce(page('middle', 'last'));
    await store.loadMore();
    expect(store.rows().map((row) => row.case_id)).toEqual(['middle']);
    expect(store.page()).toBe(2);
    list.mockResolvedValueOnce(page('last', null));
    await store.loadMore();
    expect(store.rows().map((row) => row.case_id)).toEqual(['last']);
    expect(store.page()).toBe(3);
    expect(store.nextCursor()).toBeNull();
    list.mockResolvedValueOnce(page('middle', 'last'));
    await store.previous();
    expect(list.mock.lastCall?.[1].cursor).toBe('second');
    expect(store.page()).toBe(2);
    list.mockResolvedValueOnce(page('first', 'second'));
    await store.previous();
    expect(list.mock.lastCall?.[1]).not.toHaveProperty('cursor');
    expect(store.page()).toBe(1);
  });

  it('keeps the accepted page on a failed next or previous and retries the intended cursor', async () => {
    await store.init('project', {});
    list.mockRejectedValueOnce(apiError('network', 0));
    await store.loadMore();
    expect(store.page()).toBe(1);
    expect(store.rows()[0]?.case_id).toBe('first');
    list.mockResolvedValueOnce(page('second', null));
    await store.retry();
    expect(store.page()).toBe(2);
    list.mockRejectedValueOnce(apiError('network', 0));
    await store.previous();
    expect(store.rows()[0]?.case_id).toBe('second');
    expect(store.page()).toBe(2);
    list.mockResolvedValueOnce(page('first', 'second'));
    await store.retry();
    expect(store.page()).toBe(1);
    expect(store.rows()[0]?.case_id).toBe('first');
  });

  it('resets the page after size and filter changes without accepting arbitrary sizes', async () => {
    await store.init('project', {});
    list.mockResolvedValueOnce(page('second', null));
    await store.loadMore();
    await store.init('project', { limit: '50' });
    expect(store.page()).toBe(1);
    expect(list.mock.lastCall?.[1]).toEqual({ limit: 50 });
    await store.init('project', { limit: '100', state: 'approved' });
    expect(store.page()).toBe(1);
    expect(list.mock.lastCall?.[1]).toEqual({ limit: 100, state: 'approved' });
    await store.init('project', { limit: '200' });
    expect(list.mock.lastCall?.[1].limit).toBe(20);
  });

  it('refuses a cursor cycle to an earlier page without replacing the accepted page', async () => {
    await store.init('project', {});
    list.mockResolvedValueOnce(page('second', 'third'));
    await store.loadMore();
    list.mockResolvedValueOnce(page('third', 'second'));
    await store.loadMore();
    expect(store.page()).toBe(2);
    expect(store.rows()[0]?.case_id).toBe('second');
    expect(store.error()?.code).toBe('cursor_repeated');
    expect(store.nextCursor()).toBeNull();
    list.mockResolvedValueOnce(page('first', 'second'));
    await store.previous();
    expect(store.page()).toBe(1);
    expect(store.error()).toBeNull();
  });

  it('does not navigate an empty single page or before the first page', async () => {
    list.mockResolvedValueOnce({ items: [], next_cursor: null });
    await store.init('project', {});
    await store.previous();
    await store.loadMore();
    expect(list).toHaveBeenCalledTimes(1);
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual([]);
  });
});
