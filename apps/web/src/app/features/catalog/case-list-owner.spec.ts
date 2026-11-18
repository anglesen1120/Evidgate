import { ApplicationRef, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CaseDto } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { ApiRequestError } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import { CASE_LIST_FIXTURE, CATALOG_FAILURE } from './case-list-store.fixtures';
import { CaseListStore } from './case-list.store';

const OWNER = '018f0000-0000-7000-8000-000000000032';

describe('CaseListStore owner recovery', () => {
  const canWrite = signal(true);
  let list = vi.fn<CaseListDataService['list']>();
  let update = vi.fn<CaseListDataService['update']>();
  let get = vi.fn<CaseListDataService['get']>();
  beforeEach(() => {
    canWrite.set(true);
    list = vi.fn<CaseListDataService['list']>().mockResolvedValue(CASE_LIST_FIXTURE);
    update = vi.fn<CaseListDataService['update']>();
    get = vi.fn<CaseListDataService['get']>();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        CaseListStore,
        { provide: CaseListDataService, useValue: { list, update, get } },
        { provide: SessionStore, useValue: { has: () => canWrite() } },
      ],
    });
  });
  async function settle(): Promise<void> {
    TestBed.tick();
    await TestBed.inject(ApplicationRef).whenStable();
    TestBed.tick();
  }
  async function loaded() {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    const row = store.rows()[0];
    if (!row) throw new Error('Missing case fixture');
    return { store, row };
  }

  it('keeps the optimistic owner and reuses the intent after an unconfirmed answer', async () => {
    const { store, row } = await loaded();
    update
      .mockRejectedValueOnce(new TypeError('connection lost'))
      .mockResolvedValueOnce({ ...row, owner_membership_id: OWNER, version: row.version + 1 });
    await store.assignOwner(row.id, OWNER);
    expect(store.rows()[0]?.owner_membership_id).toBe(OWNER);
    expect(store.ownerFailure(row.id)?.code).toBe('owner_outcome_unknown');
    expect(store.ownerFailure(row.id)?.message).not.toMatch(/restored/i);
    await store.assignOwner(row.id, OWNER);
    expect(update.mock.calls[1]?.[4]).toBe(update.mock.calls[0]?.[4]);
    expect(update.mock.calls[1]?.[3]).toBe(update.mock.calls[0]?.[3]);
    expect(store.ownerFailure(row.id)).toBeNull();
  });

  it('sends one PATCH for a rapid double assignment', async () => {
    const { store, row } = await loaded();
    const response = new Subject<CaseDto>();
    update.mockReturnValueOnce(firstValueFrom(response));
    const pending = store.assignOwner(row.id, OWNER);
    await store.assignOwner(row.id, OWNER);
    expect(update).toHaveBeenCalledTimes(1);
    response.next(row);
    await pending;
  });

  it('ignores a pending PATCH result after the server filters change', async () => {
    const { store, row } = await loaded();
    const response = new Subject<CaseDto>();
    update.mockReturnValueOnce(firstValueFrom(response));
    const pending = store.assignOwner(row.id, OWNER);
    list.mockResolvedValueOnce({ items: [], next_cursor: null });
    store.init('project', { status: 'missing' });
    await settle();
    response.next({ ...row, owner_membership_id: OWNER });
    await pending;
    expect(store.rows()).toEqual([]);
  });

  it('refetches only the refused case and quotes its fresh ETag on the retry', async () => {
    const { store, row } = await loaded();
    const appended = { ...row, id: '018f0000-0000-7000-8000-00000000ca77' };
    list.mockResolvedValueOnce({ items: [appended], next_cursor: null });
    await expect(store.loadMore()).resolves.toBe(true);
    update.mockRejectedValueOnce(
      new ApiRequestError({ ...CATALOG_FAILURE, kind: 'precondition_failed', status: 412 }),
    );
    await store.assignOwner(row.id, OWNER);
    const refreshed = { ...row, version: row.version + 1 };
    get.mockResolvedValueOnce({ row: refreshed, etag: '"new-server-etag"' });
    await store.reloadOwner(row.id);
    expect(store.rows()).toEqual([refreshed, ...CASE_LIST_FIXTURE.items.slice(1), appended]);
    update.mockResolvedValueOnce({ ...refreshed, owner_membership_id: OWNER });
    await store.assignOwner(row.id, OWNER);
    expect(update.mock.calls[1]?.[3]).toBe('"new-server-etag"');
    expect(update.mock.calls[1]?.[4]).not.toBe(update.mock.calls[0]?.[4]);
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('refuses a mid-session permission revocation without a PATCH', async () => {
    const { store, row } = await loaded();
    canWrite.set(false);
    await store.assignOwner(row.id, OWNER);
    expect(update).not.toHaveBeenCalled();
    expect(store.ownerFailure(row.id)?.status).toBe(403);
  });
});
