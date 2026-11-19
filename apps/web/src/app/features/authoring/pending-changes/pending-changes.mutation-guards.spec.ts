import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuthoringChangePageResponse } from '@evidgate/sdk';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { PendingChangesStore } from './pending-changes.store';
import { CHANGE, PROJECT, pendingError, pendingRow } from './pending-changes.testing';

const OTHER_CHANGE = '018f0000-0000-7000-8000-000000000004';
const ROWS = [pendingRow(), { ...pendingRow('delete'), change_id: OTHER_CHANGE }];
const RECEIPT = {
  batch_id: '018f0000-0000-7000-8000-000000000005',
  applied_change_ids: [CHANGE, OTHER_CHANGE],
  versions: [],
  lineage: [],
  committed_at: '2026-11-19T00:00:05Z',
};

describe('PendingChangesStore mutation guards', () => {
  const listPending = vi.fn<AuthoringPendingDataService['listPending']>();
  const selection = vi.fn<AuthoringPendingDataService['selection']>();
  const apply = vi.fn<AuthoringPendingDataService['apply']>();
  const discard = vi.fn<AuthoringPendingDataService['discard']>();

  beforeEach(() => {
    listPending.mockReset().mockResolvedValue({ items: ROWS, next_cursor: null });
    selection.mockReset().mockResolvedValue({ items: ROWS, batchEtag: '"batch-1"' });
    apply.mockReset().mockResolvedValue(RECEIPT);
    discard.mockReset().mockResolvedValue({
      change_id: CHANGE,
      state: 'discarded',
      discarded_at: '2026-11-19T00:00:06Z',
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        PendingChangesStore,
        {
          provide: AuthoringPendingDataService,
          useValue: { listPending, selection, apply, discard },
        },
        {
          provide: SessionStore,
          useValue: { currentProject: signal({ id: PROJECT }), has: () => true },
        },
      ],
    });
  });

  it('refuses an unresolved apply replay when the selected ids change, then retries the restored set with the same key', async () => {
    const store = TestBed.inject(PendingChangesStore);
    await store.init(PROJECT);
    apply.mockRejectedValueOnce(new ApiRequestError(pendingError('network', 0)));
    await expect(store.apply()).resolves.toBe(false);
    const request = store.applyRequest();
    expect(request).not.toBeNull();

    store.toggle(OTHER_CHANGE);
    expect(store.selectedIds()).toEqual([CHANGE]);
    await expect(store.apply()).resolves.toBe(false);
    expect(apply).toHaveBeenCalledTimes(1);
    expect(store.applyError()?.code).toBe('selection_blocked');
    expect(store.applyBlockedReason()).toContain('Restore the original selection');
    expect(store.applyRequest()).toBe(request);

    store.toggle(OTHER_CHANGE);
    await expect(store.apply()).resolves.toBe(true);
    expect(apply).toHaveBeenCalledTimes(2);
    expect(selection).toHaveBeenCalledTimes(1);
    expect(apply.mock.calls[1]?.slice(0, 4)).toEqual(apply.mock.calls[0]?.slice(0, 4));
  });

  it('starts a fresh batch after an explicit reload abandons the unresolved replay intent', async () => {
    const store = TestBed.inject(PendingChangesStore);
    await store.init(PROJECT);
    apply.mockRejectedValueOnce(new ApiRequestError(pendingError('network', 0)));
    await store.apply();
    const replayKey = apply.mock.calls[0]?.[3];
    store.toggle(OTHER_CHANGE);
    expect(store.applyBlockedReason()).toContain('Restore the original selection');

    await store.reload();
    expect(store.applyRequest()).toBeNull();
    expect(store.applyBlockedReason()).toBeNull();
    await expect(store.apply()).resolves.toBe(true);
    expect(selection).toHaveBeenCalledTimes(2);
    expect(apply.mock.calls[1]?.[3]).not.toBe(replayKey);
  });

  it('treats reordered ids as the same unresolved apply intent', async () => {
    const store = TestBed.inject(PendingChangesStore);
    await store.init(PROJECT);
    apply.mockRejectedValueOnce(new ApiRequestError(pendingError('network', 0)));
    await store.apply();
    store.toggle(CHANGE);
    store.toggle(CHANGE);
    expect(store.selectedIds()).toEqual([OTHER_CHANGE, CHANGE]);
    expect(store.applyBlockedReason()).toBeNull();
    await expect(store.apply()).resolves.toBe(true);
    expect(apply.mock.calls[1]?.slice(0, 4)).toEqual(apply.mock.calls[0]?.slice(0, 4));
  });

  it('refuses discard while a retained reload is in flight and allows it only after the fresh page arrives', async () => {
    const store = TestBed.inject(PendingChangesStore);
    await store.init(PROJECT);
    let resolvePage: (value: AuthoringChangePageResponse) => void = () => undefined;
    const page = new Promise<AuthoringChangePageResponse>((resolve) => {
      resolvePage = resolve;
    });
    listPending.mockReturnValueOnce(page);
    const reload = store.reload();
    expect(store.queueStatus()).toBe('ready');
    expect(store.rows()).toEqual(ROWS);
    expect(store.queueLoading()).toBe(true);
    expect(store.discardBlocked()).toBe(true);
    await expect(store.discard(CHANGE)).resolves.toBe(false);
    expect(discard).not.toHaveBeenCalled();

    resolvePage({ items: ROWS, next_cursor: null });
    await reload;
    expect(store.discardBlocked()).toBe(false);
    await expect(store.discard(CHANGE)).resolves.toBe(true);
    expect(discard).toHaveBeenCalledTimes(1);
    expect(store.rows().map((row) => row.change_id)).toEqual([OTHER_CHANGE]);
  });

  it('keeps discard blocked after a retained reload fails until an explicit successful reload', async () => {
    const store = TestBed.inject(PendingChangesStore);
    await store.init(PROJECT);
    listPending.mockRejectedValueOnce(new ApiRequestError(pendingError('network', 0)));
    await store.reload();
    expect(store.queueStale()).toBe(true);
    expect(store.queueLoading()).toBe(false);
    expect(store.discardBlocked()).toBe(true);
    await expect(store.discard(CHANGE)).resolves.toBe(false);
    expect(discard).not.toHaveBeenCalled();

    await store.reload();
    expect(store.queueStale()).toBe(false);
    expect(store.discardBlocked()).toBe(false);
    await expect(store.discard(CHANGE)).resolves.toBe(true);
    expect(discard).toHaveBeenCalledTimes(1);
  });
});
