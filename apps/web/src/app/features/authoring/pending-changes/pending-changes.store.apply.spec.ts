import { ApiRequestError } from '../../../core/http/api-error';
import { PENDING_COPY } from './pending-changes.copy';
import {
  PROJECT,
  COMMENT_ID,
  DELETE_ID,
  MERGE_ID,
  SPLIT_ID,
  TARGET_ID,
  change,
  failure,
  RECEIPT,
  pendingStoreHarness,
} from './pending-changes.store.testing';

describe('PendingChangesStore apply', () => {
  const { listPending, selection, apply, store } = pendingStoreHarness();

  it('applies with the exact batch ETag as If-Match and one Idempotency-Key', async () => {
    await store().init(PROJECT);
    await store().apply();
    expect(selection).toHaveBeenCalledWith(PROJECT, [COMMENT_ID], expect.any(AbortSignal));
    expect(apply).toHaveBeenCalledWith(
      PROJECT,
      '"batch-1"',
      [COMMENT_ID],
      expect.any(String),
      expect.any(AbortSignal),
    );
    expect(store().receipt()).toEqual(RECEIPT);
  });

  it('exposes the reviewed batch ETag and clears it when the review moves', async () => {
    await store().init(PROJECT);
    expect(store().batchEtag()).toBeNull();

    apply.mockRejectedValueOnce(new ApiRequestError(failure('validation', 422)));
    await store().apply();
    expect(store().batchEtag()).toBe('"batch-1"');

    store().toggle(COMMENT_ID);
    expect(store().batchEtag()).toBeNull();
    store().selectAll();
    expect(store().batchEtag()).toBeNull();

    await store().apply();
    expect(store().batchEtag()).toBeNull();
    await store().init('project-2');
    expect(store().batchEtag()).toBeNull();
  });

  it.each(['unavailable', 'network', 'timeout', 'unexpected'] as const)(
    'replays the original request after an unresolved %s without reading the queue again',
    async (kind) => {
      const row = change(DELETE_ID, 'delete');
      listPending.mockResolvedValue({ items: [row], next_cursor: null });
      selection.mockResolvedValueOnce({ items: [row], batchEtag: '"batch-1"' });
      selection.mockResolvedValue({ items: [{ ...row, stale: true }], batchEtag: '"batch-2"' });
      await store().init(PROJECT);
      const status = kind === 'unexpected' ? 500 : kind === 'unavailable' ? 503 : 0;
      const error = failure(kind, status);
      apply.mockRejectedValueOnce(new ApiRequestError(error));
      await expect(store().apply()).resolves.toBe(false);
      await expect(store().apply()).resolves.toBe(true);
      expect(selection).toHaveBeenCalledTimes(1);
      expect(apply).toHaveBeenCalledTimes(2);
      const first = apply.mock.calls[0];
      const second = apply.mock.calls[1];
      expect(first?.[3]).toBeTruthy();
      expect(second?.slice(0, 4)).toEqual(first?.slice(0, 4));
      expect(second?.slice(0, 3)).toEqual([PROJECT, '"batch-1"', [DELETE_ID]]);
    },
  );

  it('mints a new key when the reviewed batch changes', async () => {
    await store().init(PROJECT);
    await store().apply();
    const first = apply.mock.calls[0]?.[3];
    expect(first).toBeTruthy();

    listPending.mockResolvedValue({
      items: [change(COMMENT_ID, 'comment'), change(DELETE_ID, 'delete')],
      next_cursor: null,
    });
    await store().reload();
    store().toggle(COMMENT_ID);
    selection.mockResolvedValue({
      items: [change(DELETE_ID, 'delete')],
      batchEtag: '"batch-2"',
    });
    await store().apply();
    expect(apply).toHaveBeenCalledTimes(2);
    expect(apply.mock.calls[1]?.[3]).toBeTruthy();
    expect(apply.mock.calls[1]?.slice(0, 3)).toEqual([PROJECT, '"batch-2"', [DELETE_ID]]);
    expect(apply.mock.calls[1]?.[3]).not.toBe(first);
  });

  it('keeps the queue and the local reason unchanged on 412 and shows the conflict pointers', async () => {
    await store().init(PROJECT);
    store().setReason('Reviewed on the call');
    apply.mockRejectedValue(
      new ApiRequestError(
        failure('precondition_failed', 412, {
          code: 'precondition_failed',
          retryable: false,
          details: [{ path: `/items/${TARGET_ID}`, reason: 'target_changed' }],
        }),
      ),
    );
    await store().apply();
    expect(store().conflict()).toEqual({
      code: 'precondition_failed',
      pointers: [{ path: `/items/${TARGET_ID}`, reason: 'target_changed' }],
    });
    expect(
      store()
        .rows()
        .map((row) => row.change_id),
    ).toEqual([COMMENT_ID]);
    expect(store().selectedIds()).toEqual([COMMENT_ID]);
    expect(store().localReason()).toBe('Reviewed on the call');
    expect(store().receipt()).toBeNull();
  });

  it('clears only the applied ids and records a comment-only receipt', async () => {
    listPending.mockResolvedValue({
      items: [change(COMMENT_ID, 'comment'), change(DELETE_ID, 'delete')],
      next_cursor: null,
    });
    selection.mockResolvedValue({
      items: [change(COMMENT_ID, 'comment'), change(DELETE_ID, 'delete')],
      batchEtag: '"batch-1"',
    });
    await store().init(PROJECT);
    store().toggle(DELETE_ID);
    apply.mockResolvedValue({
      ...RECEIPT,
      applied_change_ids: [COMMENT_ID],
    });
    await store().apply();
    expect(
      store()
        .rows()
        .map((row) => row.change_id),
    ).toEqual([DELETE_ID]);
    expect(store().selectedIds()).toEqual([]);
    expect(store().receipt()?.versions).toEqual([]);
  });

  it('blocks an apply whose selection is stale, structural or out of date', async () => {
    listPending.mockResolvedValue({
      items: [
        change(COMMENT_ID, 'comment', { stale: true }),
        change(MERGE_ID, 'merge'),
        change(SPLIT_ID, 'split'),
      ],
      next_cursor: null,
    });
    await store().init(PROJECT);
    expect(store().applyBlockedReason()).toBe(PENDING_COPY.blockedStructural);
    await store().apply();
    expect(store().applyError()?.code).toBe('selection_blocked');
    expect(apply).not.toHaveBeenCalled();

    store().clearSelection();
    store().toggle(COMMENT_ID);
    expect(store().applyBlockedReason()).toBe(PENDING_COPY.blockedStale);
    await store().apply();
    expect(store().applyError()?.code).toBe('selection_blocked');
    expect(apply).not.toHaveBeenCalled();

    store().clearSelection();
    store().toggle(MERGE_ID);
    expect(store().applyBlockedReason()).toBe(PENDING_COPY.blockedStructural);
    store().toggle(MERGE_ID);
    store().toggle(SPLIT_ID);
    expect(store().applyBlockedReason()).toBe(PENDING_COPY.blockedStructural);
    await store().apply();
    expect(apply).not.toHaveBeenCalled();

    store().clearSelection();
    expect(store().applyBlockedReason()).toBe(PENDING_COPY.blockedEmpty);
  });
});
