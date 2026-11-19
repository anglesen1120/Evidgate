import { ApplicationRef, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuthoringChangeListItem, ChecklistItemDto } from '@evidgate/sdk';

import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { PendingChangesStore } from './pending-changes.store';
import { PENDING_COPY } from './pending-changes.copy';

const PROJECT = 'project-1';
const COMMENT_ID = '018f0000-0000-7000-8000-00000000cd01';
const DELETE_ID = '018f0000-0000-7000-8000-00000000cd02';
const MERGE_ID = '018f0000-0000-7000-8000-00000000cd03';
const SPLIT_ID = '018f0000-0000-7000-8000-00000000cd04';
const TARGET_ID = '018f0000-0000-7000-8000-00000000ab01';
const OTHER_TARGET = '018f0000-0000-7000-8000-00000000ab02';

function change(
  id: string,
  changeType: AuthoringChangeListItem['change_type'],
  extra: Partial<AuthoringChangeListItem> = {},
): AuthoringChangeListItem {
  const payload =
    changeType === 'comment'
      ? { body: 'Please confirm the wording' }
      : changeType === 'merge'
        ? { source_item_ids: [TARGET_ID, OTHER_TARGET], survivor_item_id: TARGET_ID }
        : changeType === 'split'
          ? { new_items: [{ title: 'Split one' }, { title: 'Split two' }] }
          : {};
  return {
    change_id: id,
    target_kind: 'checklist_item',
    target_id: TARGET_ID,
    change_type: changeType,
    payload,
    state: 'pending',
    source: 'manual',
    base_version: 1,
    author_membership_id: TARGET_ID,
    reason: null,
    created_at: '2026-11-19T00:00:00Z',
    ...extra,
  };
}

function failure(kind: ApiError['kind'], status: number, extra: Partial<ApiError> = {}): ApiError {
  return {
    kind,
    status,
    code: kind === 'conflict' ? 'invalid_transition' : kind,
    message: `${kind} (test)`,
    requestId: null,
    retryable: kind === 'rate_limited' || kind === 'unavailable',
    details: [],
    retryAfterSeconds: kind === 'rate_limited' || kind === 'unavailable' ? 5 : null,
    ...extra,
  };
}

const RECEIPT = {
  batch_id: '018f0000-0000-7000-8000-00000000ce01',
  applied_change_ids: [COMMENT_ID],
  versions: [],
  lineage: [],
  committed_at: '2026-11-19T00:00:05Z',
};

describe('PendingChangesStore', () => {
  const listPending = vi.fn<AuthoringPendingDataService['listPending']>();
  const selection = vi.fn<AuthoringPendingDataService['selection']>();
  const apply = vi.fn<AuthoringPendingDataService['apply']>();
  const discard = vi.fn<AuthoringPendingDataService['discard']>();
  const queue = vi.fn<AuthoringPendingDataService['queue']>();
  const checklistItems = vi.fn<AuthoringPendingDataService['checklistItems']>();
  let writable = signal(true);

  beforeEach(() => {
    writable = signal(true);
    for (const mock of [listPending, selection, apply, discard, queue, checklistItems])
      mock.mockReset();
    listPending.mockResolvedValue({ items: [change(COMMENT_ID, 'comment')], next_cursor: null });
    selection.mockResolvedValue({
      items: [change(COMMENT_ID, 'comment')],
      batchEtag: '"batch-1"',
    });
    apply.mockResolvedValue(RECEIPT);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        PendingChangesStore,
        {
          provide: AuthoringPendingDataService,
          useValue: { listPending, selection, apply, discard, queue, checklistItems },
        },
        {
          provide: SessionStore,
          useValue: {
            currentProject: signal({ id: PROJECT, name: 'Storefront' }),
            has: (code: string) => (code === 'authoring:read' ? true : writable()),
            onSessionEnd: () => () => undefined,
          },
        },
      ],
    });
  });

  async function settle(): Promise<void> {
    TestBed.tick();
    await TestBed.inject(ApplicationRef).whenStable();
    TestBed.tick();
  }

  function store() {
    return TestBed.inject(PendingChangesStore);
  }

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

  it('reuses the same key when the same ids and batch ETag are retried after a 503', async () => {
    await store().init(PROJECT);
    apply.mockRejectedValueOnce(new ApiRequestError(failure('unavailable', 503)));
    await store().apply();
    expect(store().applyError()?.kind).toBe('unavailable');
    expect(store().retryAfterSeconds()).toBe(5);
    await store().apply();
    const first = apply.mock.calls[0]?.[3];
    const second = apply.mock.calls[1]?.[3];
    expect(first).toBeDefined();
    expect(second).toBe(first);
  });

  it('mints a new key when the reviewed batch changes', async () => {
    await store().init(PROJECT);
    await store().apply();
    const first = apply.mock.calls[0]?.[3];

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
