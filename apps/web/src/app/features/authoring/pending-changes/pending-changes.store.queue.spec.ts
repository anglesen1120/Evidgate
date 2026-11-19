import { ApiRequestError } from '../../../core/http/api-error';
import { PENDING_COPY } from './pending-changes.copy';
import {
  PROJECT,
  COMMENT_ID,
  DELETE_ID,
  TARGET_ID,
  change,
  failure,
  pendingStoreHarness,
} from './pending-changes.store.testing';

describe('PendingChangesStore mutations', () => {
  const { listPending, discard, queue, checklistItems, store } = pendingStoreHarness();

  it('discards one change with its own key and retries it with the same key', async () => {
    listPending.mockResolvedValue({
      items: [change(COMMENT_ID, 'comment'), change(DELETE_ID, 'delete')],
      next_cursor: null,
    });
    await store().init(PROJECT);
    discard.mockRejectedValue(new ApiRequestError(failure('unavailable', 503)));
    await store().discard(DELETE_ID);
    await store().discard(DELETE_ID);
    expect(discard.mock.calls[0]?.[2]).toBe(discard.mock.calls[1]?.[2]);
    expect(
      store()
        .rows()
        .map((row) => row.change_id),
    ).toEqual([COMMENT_ID, DELETE_ID]);

    discard.mockResolvedValue({
      change_id: DELETE_ID,
      state: 'discarded',
      discarded_at: '2026-11-19T00:00:06Z',
    });
    await store().discard(DELETE_ID);
    expect(
      store()
        .rows()
        .map((row) => row.change_id),
    ).toEqual([COMMENT_ID]);
    expect(store().selectedIds()).toEqual([COMMENT_ID]);
  });

  it('refreshes the queue when a discard answers change_not_pending', async () => {
    await store().init(PROJECT);
    const calls = listPending.mock.calls.length;
    discard.mockRejectedValue(
      new ApiRequestError(
        failure('conflict', 409, {
          details: [{ path: '/state', reason: 'change_not_pending' }],
        }),
      ),
    );
    await store().discard(COMMENT_ID);
    expect(listPending.mock.calls.length).toBe(calls + 1);
    expect(store().discardError()?.kind).toBe('conflict');
  });

  it('queues a comment and a delete through queueAuthoringChange and reports a duplicate intent', async () => {
    await store().init(PROJECT);
    queue.mockResolvedValue({
      change_id: COMMENT_ID,
      state: 'pending',
      base_version: 1,
      created_at: '2026-11-19T00:00:00Z',
    });
    await store().queueComment(TARGET_ID, 'New note');
    expect(queue.mock.calls[0]?.[1]).toEqual({
      target_kind: 'checklist_item',
      target_id: TARGET_ID,
      change_type: 'comment',
      payload: { body: 'New note' },
    });
    await store().queueDelete(TARGET_ID, 'No longer needed');
    expect(queue.mock.calls[1]?.[1]).toMatchObject({
      change_type: 'delete',
      payload: {},
      reason: 'No longer needed',
    });

    queue.mockRejectedValue(
      new ApiRequestError(
        failure('conflict', 409, {
          details: [{ path: '/change_type', reason: 'change_already_pending' }],
        }),
      ),
    );
    await store().queueDelete(TARGET_ID);
    expect(store().enqueueError()?.message).toBe(PENDING_COPY.duplicatePending);
  });

  it('keeps a queue submission alive while checklist facts load', async () => {
    await store().init(PROJECT);
    let finish!: () => void;
    const settled = new Promise<void>((resolve) => {
      finish = resolve;
    });
    queue.mockImplementation(() =>
      settled.then(() =>
        Promise.resolve({
          change_id: COMMENT_ID,
          state: 'pending' as const,
          base_version: 1,
          created_at: '2026-11-19T00:00:00Z',
        }),
      ),
    );
    checklistItems.mockResolvedValue({ items: [], next_cursor: null });
    const submission = store().queueComment(TARGET_ID, 'New note');
    await Promise.resolve();
    const queueSignal = queue.mock.calls[0]?.[3];
    expect(queueSignal?.aborted).toBe(false);
    await store().loadChecklistFacts();
    expect(queueSignal?.aborted).toBe(false);
    finish();
    await expect(submission).resolves.toBe(true);
    expect(store().enqueueError()).toBeNull();
  });

  it('keeps the queue intent key when the submission is cancelled', async () => {
    await store().init(PROJECT);
    queue.mockRejectedValueOnce(Object.assign(new Error('cancelled'), { name: 'AbortError' }));
    queue.mockResolvedValueOnce({
      change_id: COMMENT_ID,
      state: 'pending',
      base_version: 1,
      created_at: '2026-11-19T00:00:00Z',
    });
    await expect(store().queueComment(TARGET_ID, 'New note')).resolves.toBe(false);
    expect(store().enqueueError()).toBeNull();
    await expect(store().queueComment(TARGET_ID, 'New note')).resolves.toBe(true);
    expect(queue.mock.calls[1]?.[2]).toBe(queue.mock.calls[0]?.[2]);
  });
});
