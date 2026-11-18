import { ApplicationRef, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CaseListResponse } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { ApiRequestError } from '../../core/http/api-error';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { SessionStore } from '../../core/session/session.store';
import { CASE_LIST_FIXTURE, CATALOG_FAILURE } from './case-list-store.fixtures';
import { caseFiltersToQuery, CaseListStore } from './case-list.store';

describe('CaseListStore', () => {
  let list = vi.fn<CaseListDataService['list']>();
  let update = vi.fn<CaseListDataService['update']>();
  const canWrite = signal(true);
  beforeEach(() => {
    list = vi.fn<CaseListDataService['list']>().mockResolvedValue(CASE_LIST_FIXTURE);
    update = vi.fn<CaseListDataService['update']>();
    canWrite.set(true);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        CaseListStore,
        { provide: CaseListDataService, useValue: { list, update } },
        { provide: SessionStore, useValue: { has: () => canWrite() } },
      ],
    });
  });
  async function settle() {
    TestBed.tick();
    await TestBed.inject(ApplicationRef).whenStable();
    TestBed.tick();
  }

  it('keeps the newest project response even when the old request ignores cancellation', async () => {
    const old = new Subject<CaseListResponse>();
    const latest = new Subject<CaseListResponse>();
    list.mockReturnValueOnce(firstValueFrom(old)).mockReturnValueOnce(firstValueFrom(latest));
    const store = TestBed.inject(CaseListStore);
    store.init('old', {});
    TestBed.tick();
    store.init('new', {});
    TestBed.tick();
    latest.next(CASE_LIST_FIXTURE);
    await settle();
    old.next({ items: [], next_cursor: null });
    await settle();
    expect(store.rows()).toEqual(CASE_LIST_FIXTURE.items);
    expect(store.status()).toBe('ready');
    expect(store.hasMore()).toBe(true);
  });

  it.each(['forbidden', 'unavailable'] as const)(
    'reports a typed %s without retaining private rows',
    async (kind) => {
      const store = TestBed.inject(CaseListStore);
      store.init('project', {});
      await settle();
      const previous = CASE_LIST_FIXTURE.items[0];
      if (!previous?.owner_membership_id) throw new Error('No owned case fixture');
      expect(store.rows().map((row) => row.annotation_id)).toContain(previous.annotation_id);
      expect(store.rows().map((row) => row.owner_membership_id)).toContain(
        previous.owner_membership_id,
      );
      const pending = new Subject<CaseListResponse>();
      list.mockReturnValueOnce(firstValueFrom(pending));
      store.init('next-project', { status: 'missing' });
      TestBed.tick();
      expect(list).toHaveBeenCalledTimes(2);
      expect(list).toHaveBeenLastCalledWith(
        'next-project',
        { status: 'missing', limit: 50 },
        expect.any(AbortSignal),
      );
      expect(store.status()).toBe('loading');
      expect(store.rows().map((row) => row.annotation_id)).not.toContain(previous.annotation_id);
      expect(store.rows().map((row) => row.owner_membership_id)).not.toContain(
        previous.owner_membership_id,
      );
      pending.error(
        new ApiRequestError({ ...CATALOG_FAILURE, kind, status: kind === 'forbidden' ? 403 : 503 }),
      );
      await settle();
      expect(store.loadError()?.kind).toBe(kind);
      expect(store.status()).toBe('error');
      expect(store.rows()).toEqual([]);
      expect(store.rows().map((row) => row.annotation_id)).not.toContain(previous.annotation_id);
      expect(store.rows().map((row) => row.owner_membership_id)).not.toContain(
        previous.owner_membership_id,
      );
    },
  );

  it('maps an unexpected rejection to a safe typed failure, not an empty success', async () => {
    list.mockRejectedValue(new Error('private diagnostic'));
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    expect(store.loadError()?.kind).toBe('unexpected');
    expect(store.loadError()?.message).not.toContain('private diagnostic');
  });

  it('round-trips filters and page size while dropping a URL cursor', async () => {
    const store = TestBed.inject(CaseListStore);
    const query = {
      status: 'missing',
      framework: 'playwright',
      q: 'checkout',
      cursor: 'opaque+/=',
      limit: '25',
    };
    store.init('project', query);
    await settle();
    expect(caseFiltersToQuery(store.filters())).toEqual({
      status: 'missing',
      framework: 'playwright',
      q: 'checkout',
    });
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { status: 'missing', framework: 'playwright', limit: 25 },
      expect.any(AbortSignal),
    );
    store.init('project', { ...query, ...caseFiltersToQuery(store.filters()) });
    await settle();
    expect(store.filters()).toEqual({
      status: 'missing',
      framework: 'playwright',
      search: 'checkout',
    });
    expect(list).toHaveBeenCalledTimes(1);
  });

  it('filters only loaded name and identity rows and identifies a no-match', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', { q: 'CHECKOUT' });
    await settle();
    expect(store.visibleRows().map((row) => row.annotation_id)).toEqual([
      '018f0000-0000-7000-8000-0000000c0017',
    ]);
    store.init('project', { q: 'not-present' });
    await settle();
    expect(store.isEmpty()).toBe(true);
    expect(store.isFiltered()).toBe(true);
    expect(store.hasMore()).toBe(true);
    expect(list).toHaveBeenCalledTimes(1);
  });

  it('drops an invalid enum and limit without sending them to the SDK', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', { status: 'success', limit: '201' });
    await settle();
    expect(caseFiltersToQuery(store.filters())).toEqual({});
    expect(list).toHaveBeenCalledWith('project', { limit: 50 }, expect.any(AbortSignal));
  });

  it('accumulates the opaque next page once and preserves rows while loading', async () => {
    const next = new Subject<CaseListResponse>();
    list.mockResolvedValueOnce(CASE_LIST_FIXTURE).mockReturnValueOnce(firstValueFrom(next));
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    void store.loadMore();
    void store.loadMore();
    TestBed.tick();
    expect(store.loadingMore()).toBe(true);
    expect(store.rows()).toEqual(CASE_LIST_FIXTURE.items);
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { limit: 50, cursor: 'catalog-more-opaque' },
      expect.any(AbortSignal),
    );
    const row = CASE_LIST_FIXTURE.items[0];
    if (!row) throw new Error('Missing case fixture');
    const fresh = { ...row, id: '018f0000-0000-7000-8000-00000000ca03' };
    next.next({ items: [row, fresh], next_cursor: null });
    await settle();
    expect(store.rows()).toEqual([...CASE_LIST_FIXTURE.items, fresh]);
    expect(store.hasMore()).toBe(false);
  });

  it('retains loaded rows and retry cursor after a typed load-more refusal', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockRejectedValueOnce(new ApiRequestError(CATALOG_FAILURE));
    void store.loadMore();
    await settle();
    expect(store.rows()).toEqual(CASE_LIST_FIXTURE.items);
    expect(store.status()).toBe('ready');
    expect(store.loadMoreFailure()).toEqual(CATALOG_FAILURE);
    void store.loadMore();
    await settle();
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { limit: 50, cursor: 'catalog-more-opaque' },
      expect.any(AbortSignal),
    );
  });

  it('ignores a late continuation after a server filter resets the list', async () => {
    const next = new Subject<CaseListResponse>();
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockReturnValueOnce(firstValueFrom(next));
    void store.loadMore();
    TestBed.tick();
    list.mockResolvedValueOnce({ items: [], next_cursor: null });
    store.init('project', { status: 'archived' });
    TestBed.tick();
    expect(store.rows()).toEqual([]);
    await settle();
    next.next(CASE_LIST_FIXTURE);
    await settle();
    expect(store.rows()).toEqual([]);
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { limit: 50, status: 'archived' },
      expect.any(AbortSignal),
    );
  });

  it('resets cursor and rows for supported page sizes and ignores unsupported sizes', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    void store.loadMore();
    await settle();
    store.setPageSize(200);
    TestBed.tick();
    expect(store.rows()).toEqual([]);
    await settle();
    expect(store.pageSize()).toBe(200);
    expect(list).toHaveBeenLastCalledWith('project', { limit: 200 }, expect.any(AbortSignal));
    const calls = list.mock.calls.length;
    store.setPageSize(30);
    await settle();
    expect(list).toHaveBeenCalledTimes(calls);
  });

  it('optimistically assigns one owner and replaces the row with the server confirmation', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    const row = store.rows()[0];
    if (!row) throw new Error('Missing case fixture');
    const membershipId = '018f0000-0000-7000-8000-000000000032';
    const response = new Subject<typeof row>();
    update.mockReturnValueOnce(firstValueFrom(response));
    const pending = store.assignOwner(row.id, membershipId);
    expect(store.ownerPending(row.id)).toBe(true);
    expect(store.rows()[0]?.owner_membership_id).toBe(membershipId);
    expect(update).toHaveBeenCalledWith(
      row.id,
      { owner_membership_id: membershipId },
      expect.any(AbortSignal),
      `"${row.version}"`,
    );
    const confirmed = { ...row, owner_membership_id: membershipId, version: row.version + 1 };
    response.next(confirmed);
    await pending;
    expect(store.rows()[0]).toEqual(confirmed);
    expect(store.ownerPending(row.id)).toBe(false);
    expect(store.ownerFailure(row.id)).toBeNull();
  });

  it.each([
    ['forbidden', 403],
    ['conflict', 409],
    ['precondition_failed', 412],
  ] as const)('rolls back an owner PATCH refused as %s', async (kind, status) => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    const row = store.rows()[0];
    if (!row) throw new Error('Missing case fixture');
    const failure = { ...CATALOG_FAILURE, kind, status };
    update.mockRejectedValueOnce(new ApiRequestError(failure));
    await store.assignOwner(row.id, '018f0000-0000-7000-8000-000000000032');
    expect(store.rows()[0]).toEqual(row);
    expect(store.ownerFailure(row.id)).toEqual(failure);
    expect(store.ownerPending(row.id)).toBe(false);
  });

  it('refuses forbidden writes and null unassignment without sending a PATCH', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    const row = store.rows()[0];
    if (!row) throw new Error('Missing case fixture');
    canWrite.set(false);
    await store.assignOwner(row.id, '018f0000-0000-7000-8000-000000000032');
    expect(store.ownerFailure(row.id)?.kind).toBe('forbidden');
    canWrite.set(true);
    await store.assignOwner(row.id, null);
    expect(store.ownerFailure(row.id)?.kind).toBe('validation');
    expect(update).not.toHaveBeenCalled();
  });

  it('does not restore an owner response into a superseding list', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    const row = store.rows()[0];
    if (!row) throw new Error('Missing case fixture');
    const response = new Subject<typeof row>();
    update.mockReturnValueOnce(firstValueFrom(response));
    const pending = store.assignOwner(row.id, '018f0000-0000-7000-8000-000000000032');
    list.mockResolvedValueOnce({ items: [], next_cursor: null });
    store.init('other-project', {});
    await settle();
    response.next(row);
    await pending;
    expect(store.rows()).toEqual([]);
    expect(store.ownerFailure(row.id)).toBeNull();
  });
});
