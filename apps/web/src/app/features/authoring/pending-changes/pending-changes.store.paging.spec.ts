import type { ListAuthoringChangesResponse as PendingPage } from '@evidgate/sdk';
import { ApiRequestError } from '../../../core/http/api-error';
import {
  PROJECT,
  COMMENT_ID,
  DELETE_ID,
  MERGE_ID,
  TARGET_ID,
  OTHER_TARGET,
  change,
  failure,
  pendingStoreHarness,
} from './pending-changes.store.testing';

const first = change(COMMENT_ID, 'comment');
const second = change(DELETE_ID, 'delete');
const third = change(MERGE_ID, 'comment');

describe('PendingChangesStore cursor pages', () => {
  const { listPending, selection, apply, queue, store } = pendingStoreHarness();

  async function openFirst(): Promise<void> {
    listPending.mockResolvedValueOnce({ items: [first], next_cursor: 'c2' });
    await store().init(PROJECT);
  }

  it('replaces first, middle and last pages and returns using the request cursor stack', async () => {
    await openFirst();
    expect(store().page()).toBe(1);
    expect(store().pageSize()).toBe(20);
    expect(listPending).toHaveBeenLastCalledWith(
      PROJECT,
      expect.any(AbortSignal),
      undefined,
      undefined,
      20,
    );
    listPending.mockResolvedValueOnce({ items: [second], next_cursor: 'c3' });
    await store().next();
    expect(store().rows()).toEqual([second]);
    expect(store().page()).toBe(2);
    listPending.mockResolvedValueOnce({ items: [third], next_cursor: null });
    await store().next();
    expect(store().rows()).toEqual([third]);
    expect(store().page()).toBe(3);
    expect(store().nextCursor()).toBeNull();
    await store().next();
    expect(listPending).toHaveBeenCalledTimes(3);
    listPending.mockResolvedValueOnce({ items: [second], next_cursor: 'c3' });
    await store().previous();
    expect(store().page()).toBe(2);
    expect(store().rows()).toEqual([second]);
    expect(listPending).toHaveBeenLastCalledWith(
      PROJECT,
      expect.any(AbortSignal),
      'c2',
      undefined,
      20,
    );
    listPending.mockResolvedValueOnce({ items: [first], next_cursor: 'c2' });
    await store().previous();
    expect(store().page()).toBe(1);
    expect(store().rows()).toEqual([first]);
    await store().previous();
    expect(listPending).toHaveBeenCalledTimes(5);
  });

  it.each([{ items: [] }, { items: [first] }])(
    'handles an initial empty or one-row terminal page: %j',
    async ({ items }) => {
      listPending.mockResolvedValueOnce({ items, next_cursor: null });
      await store().init(PROJECT);
      expect(store().rows()).toEqual(items);
      expect(store().empty()).toBe(items.length === 0);
      expect(store().page()).toBe(1);
      await store().next();
      await store().previous();
      expect(listPending).toHaveBeenCalledTimes(1);
    },
  );

  it('allows returning from an empty terminal page', async () => {
    await openFirst();
    listPending.mockResolvedValueOnce({ items: [], next_cursor: null });
    await store().next();
    expect(store().page()).toBe(2);
    expect(store().rows()).toEqual([]);
    listPending.mockResolvedValueOnce({ items: [first], next_cursor: 'c2' });
    await store().previous();
    expect(store().rows()).toEqual([first]);
  });

  it.each([50, 100, 20])('resets cursor history and selection for page size %i', async (size) => {
    await openFirst();
    listPending.mockResolvedValueOnce({ items: [second], next_cursor: 'c3' });
    await store().next();
    store().setPreview(DELETE_ID);
    listPending.mockResolvedValueOnce({ items: [third], next_cursor: null });
    await store().setPageSize(size);
    expect(store().page()).toBe(1);
    expect(store().pageSize()).toBe(size);
    expect(store().requestCursors()).toEqual([null]);
    expect(store().selectedIds()).toEqual([MERGE_ID]);
    expect(store().previewRow()).toBeNull();
    expect(listPending).toHaveBeenLastCalledWith(
      PROJECT,
      expect.any(AbortSignal),
      undefined,
      undefined,
      size,
    );
  });

  it('rejects unsupported page sizes without fetching', async () => {
    await openFirst();
    await store().setPageSize(200);
    expect(store().pageSize()).toBe(20);
    expect(listPending).toHaveBeenCalledTimes(1);
  });

  it('resets pages on target-filter changes and mutation reloads', async () => {
    await openFirst();
    listPending.mockResolvedValueOnce({ items: [second], next_cursor: 'c3' });
    await store().next();
    await store().init(PROJECT, { targetId: OTHER_TARGET });
    expect(store().page()).toBe(1);
    expect(store().requestCursors()).toEqual([null]);
    expect(listPending).toHaveBeenLastCalledWith(
      PROJECT,
      expect.any(AbortSignal),
      undefined,
      OTHER_TARGET,
      20,
    );
    listPending.mockResolvedValueOnce({ items: [first], next_cursor: 'c2' });
    await store().reload();
    listPending.mockResolvedValueOnce({ items: [second], next_cursor: null });
    await store().next();
    queue.mockResolvedValue({
      change_id: MERGE_ID,
      state: 'pending',
      base_version: 1,
      created_at: first.created_at,
    });
    await store().queueComment(TARGET_ID, 'Reviewed');
    expect(store().page()).toBe(1);
    expect(store().requestCursors()).toEqual([null]);
  });

  it('retains rows, selection and View on a later-page error and retries the failed cursor', async () => {
    await openFirst();
    store().setPreview(COMMENT_ID);
    listPending.mockRejectedValueOnce(new ApiRequestError(failure('network', 0)));
    await store().next();
    expect(store().rows()).toEqual([first]);
    expect(store().selectedIds()).toEqual([COMMENT_ID]);
    expect(store().previewRow()).toEqual(first);
    expect(store().queueStatus()).toBe('ready');
    expect(store().queueStale()).toBe(true);
    expect(store().page()).toBe(1);
    expect(store().discardBlocked()).toBe(true);
    expect(await store().apply()).toBe(false);
    expect(apply).not.toHaveBeenCalled();
    listPending.mockResolvedValueOnce({ items: [second], next_cursor: null });
    await store().retryPage();
    expect(listPending).toHaveBeenLastCalledWith(
      PROJECT,
      expect.any(AbortSignal),
      'c2',
      undefined,
      20,
    );
    expect(store().rows()).toEqual([second]);
    expect(store().page()).toBe(2);
    expect(store().queueStale()).toBe(false);
    expect(store().previewRow()).toBeNull();
    expect(store().selectedIds()).toEqual([DELETE_ID]);
  });

  it('retries Previous with its earlier cursor without moving the displayed page on failure', async () => {
    await openFirst();
    listPending.mockResolvedValueOnce({ items: [second], next_cursor: null });
    await store().next();
    listPending.mockRejectedValueOnce(new ApiRequestError(failure('network', 0)));
    await store().previous();
    expect(store().page()).toBe(2);
    expect(store().rows()).toEqual([second]);
    listPending.mockResolvedValueOnce({ items: [first], next_cursor: 'c2' });
    await store().retryPage();
    expect(store().page()).toBe(1);
    expect(store().rows()).toEqual([first]);
    expect(listPending).toHaveBeenLastCalledWith(
      PROJECT,
      expect.any(AbortSignal),
      undefined,
      undefined,
      20,
    );
  });

  it('ignores a late previous-size response after starting a new size', async () => {
    await openFirst();
    let resolve: (page: PendingPage) => void = () => {
      throw new Error('Pending read has not started');
    };
    const promise = new Promise<PendingPage>((finish) => {
      resolve = finish;
    });
    listPending.mockReturnValueOnce(promise);
    const moving = store().next();
    listPending.mockResolvedValueOnce({ items: [third], next_cursor: null });
    await store().setPageSize(100);
    resolve({ items: [second], next_cursor: 'c3' });
    await moving;
    expect(store().page()).toBe(1);
    expect(store().pageSize()).toBe(100);
    expect(store().rows()).toEqual([third]);
  });

  it('does not page away from an unresolved apply intent', async () => {
    await openFirst();
    apply.mockRejectedValueOnce(new ApiRequestError(failure('network', 0)));
    await store().apply();
    expect(store().applyRequest()).not.toBeNull();
    await store().next();
    await store().setPageSize(50);
    expect(store().rows()).toEqual([first]);
    expect(store().pageSize()).toBe(20);
    expect(listPending).toHaveBeenCalledTimes(1);
  });

  it('deduplicates within and across adjacent forward pages without retaining older rows', async () => {
    listPending.mockResolvedValueOnce({ items: [first, first], next_cursor: 'c2' });
    await store().init(PROJECT);
    expect(store().rows()).toEqual([first]);
    listPending.mockResolvedValueOnce({ items: [first, second, second], next_cursor: null });
    await store().next();
    expect(store().rows()).toEqual([second]);
  });

  it.each([
    { items: [second], next_cursor: 'c2' },
    { items: [first], next_cursor: 'c3' },
  ])('refuses non-advancing pages and retains the last good page: %j', async (page) => {
    await openFirst();
    listPending.mockResolvedValueOnce(page);
    await store().next();
    expect(store().rows()).toEqual([first]);
    expect(store().page()).toBe(1);
    expect(store().queueError()).not.toBeNull();
    listPending.mockResolvedValueOnce({ items: [second], next_cursor: 'c3' });
    await store().retryPage();
    expect(store().rows()).toEqual([second]);
    expect(store().queueError()).toBeNull();
  });

  it('refuses a cursor cycle to an earlier request cursor', async () => {
    await openFirst();
    listPending.mockResolvedValueOnce({ items: [second], next_cursor: 'c3' });
    await store().next();
    listPending.mockResolvedValueOnce({ items: [third], next_cursor: 'c2' });
    await store().next();
    expect(store().page()).toBe(2);
    expect(store().rows()).toEqual([second]);
    expect(store().queueError()).not.toBeNull();
  });

  it('keeps exact-selection completeness reads separate from the visible page size', async () => {
    await openFirst();
    await store().setPageSize(50);
    await store().apply();
    expect(selection).toHaveBeenCalledWith(PROJECT, [COMMENT_ID], expect.any(AbortSignal));
    expect(apply).toHaveBeenCalledWith(
      PROJECT,
      '"batch-1"',
      [COMMENT_ID],
      expect.any(String),
      expect.any(AbortSignal),
    );
  });
});
