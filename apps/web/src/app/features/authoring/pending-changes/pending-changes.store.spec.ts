import type { ChecklistItemDto } from '@evidgate/sdk';

import { ApiRequestError } from '../../../core/http/api-error';
import { PENDING_COPY } from './pending-changes.copy';
import {
  PROJECT,
  COMMENT_ID,
  DELETE_ID,
  MERGE_ID,
  SPLIT_ID,
  TARGET_ID,
  OTHER_TARGET,
  change,
  failure,
  pendingStoreHarness,
} from './pending-changes.store.testing';

describe('PendingChangesStore queue reads', () => {
  const { listPending, apply, checklistItems, settle, writable, store } = pendingStoreHarness();

  it('loads the pending queue and selects every loaded change by default', async () => {
    listPending.mockResolvedValue({
      items: [change(COMMENT_ID, 'comment'), change(DELETE_ID, 'delete')],
      next_cursor: 'cursor-2',
    });
    await store().init(PROJECT);
    await settle();
    expect(listPending).toHaveBeenCalledWith(PROJECT, expect.any(AbortSignal), undefined);
    expect(store().queueStatus()).toBe('ready');
    expect(store().selectedIds()).toEqual([COMMENT_ID, DELETE_ID]);
    expect(store().nextCursor()).toBe('cursor-2');
    expect(store().empty()).toBe(false);
  });

  it('keeps a target scope across reload and loadMore and refetches when the scope changes', async () => {
    listPending.mockResolvedValue({ items: [change(COMMENT_ID, 'comment')], next_cursor: 'c2' });
    await store().init(PROJECT, { targetId: TARGET_ID });
    await store().loadMore();
    await store().reload();
    expect(listPending.mock.calls.map((call) => [call[2], call[3]])).toEqual([
      [undefined, TARGET_ID],
      ['c2', TARGET_ID],
      [undefined, TARGET_ID],
    ]);
    await store().init(PROJECT, { targetId: OTHER_TARGET });
    expect(listPending).toHaveBeenLastCalledWith(
      PROJECT,
      expect.any(AbortSignal),
      undefined,
      OTHER_TARGET,
    );
    await store().init(PROJECT);
    expect(listPending).toHaveBeenLastCalledWith(PROJECT, expect.any(AbortSignal), undefined);
  });

  it('distinguishes an empty queue from a not-found or forbidden read', async () => {
    listPending.mockResolvedValue({ items: [], next_cursor: null });
    await store().init(PROJECT);
    expect(store().empty()).toBe(true);
    expect(store().notFound()).toBe(false);

    listPending.mockRejectedValue(new ApiRequestError(failure('not_found', 404)));
    await store().reload();
    expect(store().notFound()).toBe(true);
    expect(store().empty()).toBe(false);

    listPending.mockRejectedValue(new ApiRequestError(failure('forbidden', 403)));
    await store().reload();
    expect(store().forbidden()).toBe(true);
  });

  it('keeps the queue readable while write permission is absent', async () => {
    writable.set(false);
    await store().init(PROJECT);
    expect(store().queueStatus()).toBe('ready');
    expect(store().canWrite()).toBe(false);
    expect(store().applyBlockedReason()).toBe(PENDING_COPY.readOnly);
    await store().apply();
    expect(apply).not.toHaveBeenCalled();
    expect(store().discardBlocked()).toBe(true);
  });

  it('builds a plain-text preview from the payload and never invents unavailable impact', async () => {
    listPending.mockResolvedValue({
      items: [change(COMMENT_ID, 'comment'), change(MERGE_ID, 'merge'), change(SPLIT_ID, 'split')],
      next_cursor: null,
    });
    await store().init(PROJECT);
    store().setPreview(COMMENT_ID);
    expect(store().preview()?.lines).toEqual(['Please confirm the wording']);
    store().setPreview(MERGE_ID);
    expect(store().preview()?.lines.join(' ')).toContain(TARGET_ID);
    expect(store().preview()?.unavailable).toBe(PENDING_COPY.blockedStructural);
    store().setPreview(SPLIT_ID);
    expect(store().preview()?.lines.join(' ')).toContain('Split one');
    expect(
      store()
        .rowsWithBlocks()
        .find((entry) => entry.row.change_id === SPLIT_ID)?.blockedReason,
    ).toBe(PENDING_COPY.blockedStructural);
  });

  it('remembers a checklist title for the preview target', async () => {
    await store().init(PROJECT);
    const item: ChecklistItemDto = {
      item_id: TARGET_ID,
      node_id: OTHER_TARGET,
      title: 'Refund policy',
      description: null,
      priority: 'high',
      state: 'active',
      source: 'manual',
      position: 1,
      version: 2,
    };
    checklistItems.mockResolvedValue({ items: [item], next_cursor: null });
    await store().loadChecklistFacts({ state: 'active' });
    store().setPreview(COMMENT_ID);
    expect(store().targetTitles()[TARGET_ID]).toBe('Refund policy');
    expect(store().preview()?.targetId).toBe(TARGET_ID);
  });
});
