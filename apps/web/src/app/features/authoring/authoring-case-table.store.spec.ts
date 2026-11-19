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

  it('ends paging after a non-advancing cursor without offering an endless Retry or load-more', async () => {
    await store.init('project', {});
    list.mockResolvedValueOnce(page(['2'], 'next'));
    await store.loadMore();
    expect(store.error()).toMatchObject({ code: 'cursor_repeated', retryable: false });
    expect(store.nextCursor()).toBeNull();
    await store.retry();
    await store.loadMore();
    expect(list).toHaveBeenCalledTimes(2);
    list.mockResolvedValueOnce(page(['fresh'], 'new-next'));
    await store.reload();
    expect(store.error()).toBeNull();
    expect(store.nextCursor()).toBe('new-next');
  });

  it('ends a page without new rows rather than allowing the same continuation again', async () => {
    await store.init('project', {});
    list.mockResolvedValueOnce(page(['1'], 'other'));
    await store.loadMore();
    expect(store.error()).toMatchObject({ code: 'page_without_new_rows', retryable: false });
    expect(store.nextCursor()).toBeNull();
    await store.loadMore();
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('caps retained cases at 10000 including a partial final page and blocks further reads', async () => {
    list.mockResolvedValueOnce(
      page(
        Array.from({ length: 199 }, (_, i) => String(i)),
        '199',
      ),
    );
    await store.init('project', { limit: '200' });
    for (let start = 199; start < 10_000; start += 200) {
      list.mockResolvedValueOnce(
        page(
          Array.from({ length: 200 }, (_, i) => String(start + i)),
          String(start + 200),
        ),
      );
      await store.loadMore();
    }
    expect(store.rows()).toHaveLength(10_000);
    expect(store.rows().at(-1)?.case_id).toBe('9999');
    expect(store.browseCapped()).toBe(true);
    expect(store.nextCursor()).toBeNull();
    const reads = list.mock.calls.length;
    await store.loadMore();
    await store.retry();
    expect(list).toHaveBeenCalledTimes(reads);
    list.mockResolvedValueOnce(page(['new']));
    await store.init('project', { q: 'narrower' });
    expect(store.browseCapped()).toBe(false);
    expect(store.rows()).toHaveLength(1);
  });

  it('accepts the exact-cap continuation cursor and retains the cap notice after failed Refresh', async () => {
    list.mockResolvedValueOnce(
      page(
        Array.from({ length: 9_800 }, (_, i) => String(i)),
        'last-retained',
      ),
    );
    await store.init('project', {});
    list.mockResolvedValueOnce(
      page(
        Array.from({ length: 200 }, (_, i) => String(9_800 + i)),
        'beyond-cap',
      ),
    );
    expect(await store.loadMore()).toBe('last-retained');
    expect(store.rows()).toHaveLength(10_000);
    expect(store.browseCapped()).toBe(true);
    expect(store.nextCursor()).toBeNull();
    await store.init('project', { cursor: 'last-retained' });
    expect(list).toHaveBeenCalledTimes(2);
    list.mockRejectedValueOnce(apiError('network', 0));
    await store.reload();
    expect(store.rows()).toHaveLength(10_000);
    expect(store.browseCapped()).toBe(true);
    expect(store.stale()).toBe(true);
    list.mockResolvedValueOnce(page(['replacement']));
    await store.retry();
    expect(store.rows().map((item) => item.case_id)).toEqual(['replacement']);
    expect(store.browseCapped()).toBe(false);
  });

  it.each(['item_id', 'node_id'])('accepts uppercase dashed UUIDs for %s', async (key) => {
    const uuid = '018F0000-ABCD-7000-8000-000000000201';
    await store.init('project', { [key]: uuid });
    expect(store.invalidLink()).toBe(false);
    expect(list.mock.lastCall?.[1]).toHaveProperty(key, uuid);
  });

  it.each([
    ['item_id', '{018f0000-0000-7000-8000-000000000201}'],
    ['item_id', '018f0000000070008000000000000201'],
    ['node_id', '{018f0000-0000-7000-8000-000000000101}'],
    ['node_id', '018f0000000070008000000000000101'],
  ])('refuses malformed %s shape %s without widening the read', async (key, value) => {
    await store.init('project', { [key]: value });
    await store.reload();
    await store.retry();
    await store.loadMore();
    expect(list).not.toHaveBeenCalled();
    expect(store.invalidLink()).toBe(true);
    expect(store.error()).toMatchObject({ code: `invalid_${key}`, retryable: false });
  });

  it('does not claim a cap when the last complete page ends exactly at 10000 cases', async () => {
    const ids = (start: number) => Array.from({ length: 200 }, (_, i) => String(start + i));
    list.mockResolvedValueOnce(page(ids(0), 'p1'));
    await store.init('project', { limit: '200' });
    for (let index = 1; index < 50; index += 1) {
      list.mockResolvedValueOnce(page(ids(index * 200), index < 49 ? `p${index + 1}` : null));
      await store.loadMore();
    }
    expect(store.rows()).toHaveLength(10_000);
    expect(store.browseCapped()).toBe(false);
    expect(store.nextCursor()).toBeNull();
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

  it('refuses a malformed URL item UUID before reading and keeps refusing on Refresh and Retry', async () => {
    await store.init('project', { node_id: NODE, item_id: 'invalid-item' });
    await store.reload();
    await store.retry();
    await store.loadMore();
    expect(list).not.toHaveBeenCalled();
    expect(store.invalidLink()).toBe(true);
    expect(store.error()).toMatchObject({ code: 'invalid_item_id', retryable: false });
    expect(store.rows()).toEqual([]);
    await store.init('project', { node_id: NODE, item_id: ITEM });
    expect(list).toHaveBeenCalledTimes(1);
    expect(list.mock.lastCall?.[1].item_id).toBe(ITEM);
    expect(store.invalidLink()).toBe(false);
  });

  it('does not reuse an unfiltered scope when a malformed item UUID arrives in the URL', async () => {
    await store.init('project', {});
    list.mockClear();
    await store.init('project', { item_id: 'bad' });
    expect(list).not.toHaveBeenCalled();
    expect(store.rows()).toEqual([]);
    expect(store.invalidLink()).toBe(true);
    await store.init('project', { item_id: '' });
    expect(list).toHaveBeenCalledTimes(1);
    expect(store.invalidLink()).toBe(false);
    await store.init('project', { node_id: NODE });
    expect(list).toHaveBeenCalledTimes(2);
    expect(list.mock.lastCall?.[1].node_id).toBe(NODE);
    await store.init('project', { node_id: 'bad-node' });
    expect(list).toHaveBeenCalledTimes(2);
    expect(store.rows()).toEqual([]);
    expect(store.invalidLink()).toBe(true);
    expect(store.error()?.code).toBe('invalid_node_id');
  });
});
