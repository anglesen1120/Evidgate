import { ApplicationRef, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, vi } from 'vitest';
import type { AuthoringChangeListItem } from '@evidgate/sdk';

import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { PendingChangesStore } from './pending-changes.store';

export const PROJECT = 'project-1';
export const COMMENT_ID = '018f0000-0000-7000-8000-00000000cd01';
export const DELETE_ID = '018f0000-0000-7000-8000-00000000cd02';
export const MERGE_ID = '018f0000-0000-7000-8000-00000000cd03';
export const SPLIT_ID = '018f0000-0000-7000-8000-00000000cd04';
export const TARGET_ID = '018f0000-0000-7000-8000-00000000ab01';
export const OTHER_TARGET = '018f0000-0000-7000-8000-00000000ab02';

export function change(
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

export function failure(
  kind: ApiError['kind'],
  status: number,
  extra: Partial<ApiError> = {},
): ApiError {
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

export const RECEIPT = {
  batch_id: '018f0000-0000-7000-8000-00000000ce01',
  applied_change_ids: [COMMENT_ID],
  versions: [],
  lineage: [],
  committed_at: '2026-11-19T00:00:05Z',
};

/**
 * One store per test, with the data service and the session faked at the seam the store calls.
 * Returns the mocks so each spec drives only the outcome it exercises.
 */
export function pendingStoreHarness() {
  const listPending = vi.fn<AuthoringPendingDataService['listPending']>();
  const selection = vi.fn<AuthoringPendingDataService['selection']>();
  const apply = vi.fn<AuthoringPendingDataService['apply']>();
  const discard = vi.fn<AuthoringPendingDataService['discard']>();
  const queue = vi.fn<AuthoringPendingDataService['queue']>();
  const checklistItems = vi.fn<AuthoringPendingDataService['checklistItems']>();
  const writable = signal(true);

  beforeEach(() => {
    writable.set(true);
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

  return {
    listPending,
    selection,
    apply,
    discard,
    queue,
    checklistItems,
    writable,
    settle,
    store,
  };
}
