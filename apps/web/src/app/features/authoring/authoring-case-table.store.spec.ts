import { TestBed } from '@angular/core/testing';
import type { ManualCaseListItemDto, ManualCaseListResponse } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiRequestError } from '../../core/http/api-error';
import { apiError } from '../../shared/testing/http-data.testing';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringCaseTableStore } from './authoring-case-table.store';

const NODE = '018f0000-0000-7000-8000-000000000101';
const ITEM = '018f0000-0000-7000-8000-000000000201';
const row = (id: string): ManualCaseListItemDto => ({
  case_id: id,
  code: `SHOP-${id}`,
  node_id: NODE,
  checklist_item_id: ITEM,
  title: 'Địa chỉ giao hàng',
  priority: 'high',
  state: 'draft',
  version: 1,
  linked_live_count: 0,
  linked_case_ids: [],
});
const page = (ids: string[], cursor: string | null = null): ManualCaseListResponse => ({
  items: ids.map(row),
  next_cursor: cursor,
});

describe('AuthoringCaseTableStore', () => {
  const list = vi.fn<AuthoringDataService['listManualCases']>();
  let store: InstanceType<typeof AuthoringCaseTableStore>;
  beforeEach(() => {
    list.mockReset().mockResolvedValue(page(['1'], 'next'));
    TestBed.configureTestingModule({
      providers: [
        AuthoringCaseTableStore,
        { provide: AuthoringDataService, useValue: { listManualCases: list } },
      ],
    });
    store = TestBed.inject(AuthoringCaseTableStore);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('sends node, item, state, priority, coverage, server search and limit', async () => {
    await store.init('project', {
      node_id: NODE,
      item_id: ITEM,
      state: 'draft',
      priority: 'critical',
      covered: 'false',
      q: 'Địa chỉ',
      limit: '100',
    });
    expect(list.mock.calls[0]?.slice(0, 2)).toEqual([
      'project',
      {
        node_id: NODE,
        item_id: ITEM,
        state: 'draft',
        priority: 'critical',
        covered: false,
        q: 'Địa chỉ',
        limit: 100,
      },
    ]);
    expect(store.rows().map((item) => item.case_id)).toEqual(['1']);
  });

  it('drops a stale response even if the old transport ignores cancellation', async () => {
    const old = new Subject<ManualCaseListResponse>();
    list.mockReturnValueOnce(firstValueFrom(old)).mockResolvedValueOnce(page(['2']));
    const first = store.init('project', { q: 'old' });
    await store.init('project', { q: 'new' });
    expect(list.mock.calls[0]?.[2].aborted).toBe(true);
    old.next(page(['1']));
    await first;
    expect(store.rows().map((item) => item.case_id)).toEqual(['2']);
  });

  it('appends keyset rows and consumes its own URL cursor without re-reading', async () => {
    await store.init('project', {});
    list.mockResolvedValueOnce(page(['2']));
    expect(await store.loadMore()).toBe('next');
    await store.init('project', { cursor: 'next' });
    expect(list).toHaveBeenCalledTimes(2);
    expect(store.rows().map((item) => item.case_id)).toEqual(['1', '2']);
    await store.init('project', {});
    expect(list).toHaveBeenCalledTimes(3);
  });

  it('retries the failed continuation without replacing either accepted prefix page', async () => {
    await store.init('project', {});
    list.mockResolvedValueOnce(page(['2'], 'third-page'));
    await store.loadMore();
    list.mockRejectedValueOnce(apiError('network', 0));
    await store.loadMore();
    expect(store.rows().map((item) => item.case_id)).toEqual(['1', '2']);
    list.mockResolvedValueOnce(page(['3']));
    expect(await store.retry()).toBe('third-page');
    expect(list.mock.lastCall?.[1].cursor).toBe('third-page');
    expect(store.rows().map((item) => item.case_id)).toEqual(['1', '2', '3']);
    expect(store.error()).toBeNull();
  });

  it('explicitly refreshes from the first page after loading a continuation', async () => {
    await store.init('project', {});
    list.mockResolvedValueOnce(page(['2'], 'third-page'));
    await store.loadMore();
    list.mockResolvedValueOnce(page(['fresh-first'], 'fresh-next'));
    await store.reload();
    expect(list.mock.lastCall?.[1]).not.toHaveProperty('cursor');
    expect(store.routeCursor()).toBeNull();
    expect(store.rows().map((item) => item.case_id)).toEqual(['fresh-first']);
    expect(store.nextCursor()).toBe('fresh-next');
  });

  it('resets rows and cursor when filters change and cancels a pending next page', async () => {
    await store.init('project', {});
    const old = new Subject<ManualCaseListResponse>();
    list.mockReturnValueOnce(firstValueFrom(old)).mockResolvedValueOnce(page(['3']));
    const more = store.loadMore();
    await store.init('project', { covered: 'true' });
    old.next(page(['2']));
    await more;
    expect(store.rows().map((item) => item.case_id)).toEqual(['3']);
    expect(store.nextCursor()).toBeNull();
    expect(list.mock.calls[2]?.[1]).not.toHaveProperty('cursor');
  });

  it('refuses a repeated cursor without replacing loaded rows', async () => {
    await store.init('project', {});
    list.mockResolvedValueOnce(page(['2'], 'next'));
    await store.loadMore();
    expect(store.error()?.code).toBe('cursor_repeated');
    expect(store.rows().map((item) => item.case_id)).toEqual(['1']);
  });

  it('retains stale rows on refresh failure but clears them on a 403', async () => {
    await store.init('project', {});
    list.mockRejectedValueOnce(apiError('network', 0));
    await store.reload();
    expect(store.stale()).toBe(true);
    expect(store.rows()).toHaveLength(1);
    list.mockRejectedValueOnce(apiError('forbidden', 403));
    await store.reload();
    expect(store.rows()).toEqual([]);
    expect(store.stale()).toBe(false);
  });

  it('enforces Retry-After before either refresh or load-more can request again', async () => {
    vi.useFakeTimers();
    list.mockRejectedValueOnce(
      new ApiRequestError({
        ...apiError('unavailable', 503).error,
        retryable: true,
        retryAfterSeconds: 3,
      }),
    );
    await store.init('project', {});
    await store.reload();
    await store.loadMore();
    expect(list).toHaveBeenCalledTimes(1);
    expect(store.retryReady()).toBe(false);
    await vi.advanceTimersByTimeAsync(3000);
    await store.reload();
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('refuses a 201-code-point search before making a request', async () => {
    await store.init('project', { q: 'x'.repeat(201) });
    expect(list).not.toHaveBeenCalled();
    expect(store.error()?.kind).toBe('validation');
    await store.init('project', { q: 'x'.repeat(200) });
    expect(list).toHaveBeenCalledTimes(1);
  });

  it('refuses an invalid URL search again on explicit Refresh and Retry', async () => {
    await store.init('project', { q: 'x'.repeat(201) });
    await store.reload();
    await store.retry();
    expect(list).not.toHaveBeenCalled();
    expect(store.error()?.code).toBe('query_too_long');
  });

  it('omits malformed URL UUIDs on both the initial read and Refresh', async () => {
    await store.init('project', { node_id: 'invalid-node', item_id: 'invalid-item' });
    await store.reload();
    expect(list).toHaveBeenCalledTimes(2);
    for (const [, query] of list.mock.calls) {
      expect(query).not.toHaveProperty('node_id');
      expect(query).not.toHaveProperty('item_id');
    }
  });
});
