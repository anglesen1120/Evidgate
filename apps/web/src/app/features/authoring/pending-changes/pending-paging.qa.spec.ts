import { TestBed } from '@angular/core/testing';
import { ApiRequestError } from '../../../core/http/api-error';
import {
  COMMENT_ID,
  DELETE_ID,
  MERGE_ID,
  PROJECT,
  change,
  failure,
  pendingStoreHarness,
} from './pending-changes.store.testing';

const first = change(COMMENT_ID, 'comment');
const middle = change(DELETE_ID, 'delete');
const last = change(MERGE_ID, 'comment');

describe('Pending paging adversarial boundaries', () => {
  const { listPending, apply, store } = pendingStoreHarness();
  afterEach(() => TestBed.resetTestingModule());

  it('deduplicates a page-one ID reappearing on page three and still permits backward traversal', async () => {
    listPending.mockResolvedValueOnce({ items: [first], next_cursor: 'second' });
    await store().init(PROJECT);
    listPending.mockResolvedValueOnce({ items: [middle], next_cursor: 'third' });
    await store().next();
    listPending.mockResolvedValueOnce({ items: [first, last, last], next_cursor: null });
    await store().next();
    expect(store().rows()).toEqual([last]);
    expect(store().selectedIds()).toEqual([MERGE_ID]);
    expect(store().pageRowIds()).toEqual([[COMMENT_ID], [DELETE_ID], [MERGE_ID]]);
    listPending.mockResolvedValueOnce({ items: [middle], next_cursor: 'third' });
    await store().previous();
    expect(store().rows()).toEqual([middle]);
    listPending.mockResolvedValueOnce({ items: [first, last], next_cursor: null });
    await store().next();
    expect(store().rows()).toEqual([last]);
    expect(store().page()).toBe(3);
  });

  it('does not restore old-size selection or mutation eligibility when the new first page fails', async () => {
    listPending.mockResolvedValueOnce({ items: [first], next_cursor: 'second' });
    await store().init(PROJECT);
    store().setPreview(COMMENT_ID);
    listPending.mockRejectedValueOnce(new ApiRequestError(failure('network', 0)));
    await store().setPageSize(50);
    expect(store().rows()).toEqual([]);
    expect(store().selectedIds()).toEqual([]);
    expect(store().previewRow()).toBeNull();
    expect(store().pageRowIds()).toEqual([]);
    expect(store().queueStatus()).toBe('error');
    expect(await store().apply()).toBe(false);
    expect(apply).not.toHaveBeenCalled();
    listPending.mockResolvedValueOnce({ items: [last], next_cursor: null });
    await store().retryPage();
    expect(listPending).toHaveBeenLastCalledWith(
      PROJECT,
      expect.any(AbortSignal),
      undefined,
      undefined,
      50,
    );
    expect(store().rows()).toEqual([last]);
    expect(store().page()).toBe(1);
    expect(store().requestCursors()).toEqual([null]);
  });
});
