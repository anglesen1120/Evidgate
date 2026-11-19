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

function pendingPage() {
  let finish: (page: PendingPage) => void = () => {
    throw new Error('Pending read has not started');
  };
  const promise = new Promise<PendingPage>((resolve) => {
    finish = resolve;
  });
  return { promise, finish: (page: PendingPage) => finish(page) };
}

describe('PendingChangesStore target scope', () => {
  const { listPending, queue, discard, store } = pendingStoreHarness();

  it('clears the old target rows and review state before the next target read resolves', async () => {
    await store().init(PROJECT, { targetId: TARGET_ID });
    store().setPreview(COMMENT_ID);
    store().setReason('Reason for the first target');
    expect(store().rows()).toMatchObject([{ change_id: COMMENT_ID }]);
    expect(store().selectedIds()).toEqual([COMMENT_ID]);
    expect(store().previewRow()?.change_id).toBe(COMMENT_ID);

    const next = pendingPage();
    listPending.mockReturnValueOnce(next.promise);
    const changing = store().init(PROJECT, { targetId: OTHER_TARGET });
    expect(store().rows()).toEqual([]);
    expect(store().selectedIds()).toEqual([]);
    expect(store().previewRow()).toBeNull();
    expect(store().localReason()).toBe('');
    expect(store().queueLoading()).toBe(true);
    next.finish({
      items: [change(DELETE_ID, 'delete', { target_id: OTHER_TARGET })],
      next_cursor: null,
    });
    await changing;
    expect(store().rows()).toMatchObject([{ target_id: OTHER_TARGET }]);
    expect(store().selectedIds()).toEqual([DELETE_ID]);
  });

  it('ignores a late read from the previous target after switching scopes', async () => {
    const previous = pendingPage();
    listPending.mockReturnValueOnce(previous.promise);
    const firstLoad = store().init(PROJECT, { targetId: TARGET_ID });
    const previousSignal = listPending.mock.calls[0]?.[1];
    listPending.mockResolvedValueOnce({
      items: [change(DELETE_ID, 'delete', { target_id: OTHER_TARGET })],
      next_cursor: null,
    });
    await store().init(PROJECT, { targetId: OTHER_TARGET });
    expect(previousSignal?.aborted).toBe(true);
    expect(store().rows()).toMatchObject([{ change_id: DELETE_ID }]);
    previous.finish({ items: [change(COMMENT_ID, 'comment')], next_cursor: null });
    await firstLoad;
    expect(store().rows()).toMatchObject([{ change_id: DELETE_ID }]);
    expect(store().selectedIds()).toEqual([DELETE_ID]);
    expect(store().targetId()).toBe(OTHER_TARGET);
    expect(store().queueLoading()).toBe(false);
  });

  it('returns to project-wide rows when the target scope is removed', async () => {
    listPending.mockImplementation((_project, _signal, _cursor, target) =>
      Promise.resolve({
        items:
          target === TARGET_ID
            ? [change(COMMENT_ID, 'comment')]
            : [change(DELETE_ID, 'delete', { target_id: OTHER_TARGET })],
        next_cursor: null,
      }),
    );
    await store().init(PROJECT, { targetId: TARGET_ID });
    expect(store().rows()).toMatchObject([{ change_id: COMMENT_ID }]);
    await store().init(PROJECT);
    expect(store().targetId()).toBeNull();
    expect(store().rows()).toMatchObject([{ change_id: DELETE_ID }]);
  });

  it('treats an empty target id as no scope', async () => {
    await store().init(PROJECT, { targetId: '' });
    expect(store().targetId()).toBeNull();
    expect(listPending).toHaveBeenLastCalledWith(PROJECT, expect.any(AbortSignal), undefined);
  });

  it.each(['success', 'conflict'] as const)(
    'retains scoped rows after a queue submission returns %s',
    async (outcome) => {
      await store().init(PROJECT, { targetId: TARGET_ID });
      await store().reload();
      listPending.mockImplementation((_project, _signal, _cursor, target) =>
        Promise.resolve({
          items:
            target === TARGET_ID
              ? [change(DELETE_ID, 'delete')]
              : [change(MERGE_ID, 'merge', { target_id: OTHER_TARGET })],
          next_cursor: null,
        }),
      );
      if (outcome === 'success') {
        queue.mockResolvedValue({
          change_id: DELETE_ID,
          state: 'pending',
          base_version: 1,
          created_at: '2026-11-19T00:00:00Z',
        });
      } else {
        queue.mockRejectedValue(new ApiRequestError(failure('conflict', 409)));
      }
      await expect(store().queueDelete(TARGET_ID)).resolves.toBe(outcome === 'success');
      expect(store().targetId()).toBe(TARGET_ID);
      expect(store().rows()).toMatchObject([{ change_id: DELETE_ID, target_id: TARGET_ID }]);
    },
  );

  it.each([
    ['conflict', 409],
    ['not_found', 404],
  ] as const)('retains the target scope when discard returns %s', async (kind, status) => {
    await store().init(PROJECT, { targetId: TARGET_ID });
    await store().reload();
    expect(store().rows()).toMatchObject([{ change_id: COMMENT_ID }]);
    discard.mockRejectedValue(new ApiRequestError(failure(kind, status)));
    listPending.mockImplementation((_project, _signal, _cursor, target) =>
      Promise.resolve({
        items: target === TARGET_ID ? [] : [change(MERGE_ID, 'merge', { target_id: OTHER_TARGET })],
        next_cursor: null,
      }),
    );
    await expect(store().discard(COMMENT_ID)).resolves.toBe(false);
    expect(store().discardError()?.kind).toBe(kind);
    expect(store().targetId()).toBe(TARGET_ID);
    expect(store().rows()).toEqual([]);
    expect(store().selectedIds()).toEqual([]);
    expect(store().empty()).toBe(true);
  });
});
