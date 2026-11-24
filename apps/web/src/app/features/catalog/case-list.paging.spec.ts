import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CaseDto } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { ApiRequestError } from '../../core/http/api-error';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { SessionStore } from '../../core/session/session.store';
import { CASE_LIST_FIXTURE, CATALOG_FAILURE } from './case-list-store.fixtures';
import { CaseListStore } from './case-list.store';

describe('Catalog cursor pages', () => {
  const list = vi.fn<CaseListDataService['list']>();
  const get = vi.fn<CaseListDataService['get']>();
  const update = vi.fn<CaseListDataService['update']>();
  async function settle() {
    TestBed.tick();
    await TestBed.inject(ApplicationRef).whenStable();
    TestBed.tick();
  }
  beforeEach(() => {
    list.mockReset().mockResolvedValue(CASE_LIST_FIXTURE);
    get.mockReset();
    update.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        CaseListStore,
        { provide: CaseListDataService, useValue: { list, get, update } },
        { provide: SessionStore, useValue: { has: () => true } },
      ],
    });
  });
  function laterPage() {
    const row = CASE_LIST_FIXTURE.items[0];
    if (!row) throw new Error('Missing catalog fixture');
    return { items: [{ ...row, id: 'later-case' }], next_cursor: null };
  }
  it('replaces the displayed page and requests the preceding opaque cursor on Previous', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    expect(store.pageSize()).toBe(20);
    expect(store.page()).toBe(1);
    list.mockResolvedValueOnce(laterPage());
    await store.nextPage();
    expect(store.rows()).toEqual(laterPage().items);
    expect(store.page()).toBe(2);
    expect(store.hasMore()).toBe(false);
    await store.previousPage();
    expect(list).toHaveBeenLastCalledWith('project', { limit: 20 }, expect.any(AbortSignal));
    expect(store.rows()).toEqual(CASE_LIST_FIXTURE.items);
    expect(store.page()).toBe(1);
  });
  it('retains the page on a failed Previous and retries that previous intent', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockResolvedValueOnce(laterPage());
    await store.nextPage();
    list.mockRejectedValueOnce(new ApiRequestError(CATALOG_FAILURE));
    await store.previousPage();
    expect(store.rows()).toEqual(laterPage().items);
    expect(store.page()).toBe(2);
    expect(store.loadMoreFailure()).toEqual(CATALOG_FAILURE);
    await store.retryPage();
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual(CASE_LIST_FIXTURE.items);
  });
  it('resets local search and size changes to page one but keeps identical route state', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockResolvedValueOnce(laterPage());
    await store.nextPage();
    store.init('project', { record: 'case:later-case', mode: 'view' });
    expect(store.page()).toBe(2);
    store.init('project', { q: 'checkout' });
    await settle();
    expect(store.page()).toBe(1);
    store.setPageSize(100);
    await settle();
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(100);
    expect(list).toHaveBeenLastCalledWith('project', { limit: 100 }, expect.any(AbortSignal));
  });
  it('walks first, middle and last pages, then re-requests each preceding opaque cursor', async () => {
    const middle = { ...laterPage(), next_cursor: 'opaque+/=last-page' };
    const first = CASE_LIST_FIXTURE.items[0];
    if (!first) throw new Error('Missing catalog fixture');
    const last = { items: [{ ...first, id: 'last-case' }], next_cursor: null };
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual(CASE_LIST_FIXTURE.items);
    list
      .mockResolvedValueOnce(middle)
      .mockResolvedValueOnce(last)
      .mockResolvedValueOnce(middle)
      .mockResolvedValueOnce(CASE_LIST_FIXTURE);
    expect(await store.nextPage()).toBe(true);
    expect(store.page()).toBe(2);
    expect(store.rows()).toEqual(middle.items);
    expect(store.hasMore()).toBe(true);
    expect(await store.nextPage()).toBe(true);
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { limit: 20, cursor: 'opaque+/=last-page' },
      expect.any(AbortSignal),
    );
    expect(store.page()).toBe(3);
    expect(store.rows()).toEqual(last.items);
    expect(store.hasMore()).toBe(false);
    expect(await store.nextPage()).toBe(false);
    expect(await store.previousPage()).toBe(true);
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { limit: 20, cursor: 'catalog-more-opaque' },
      expect.any(AbortSignal),
    );
    expect(store.page()).toBe(2);
    expect(store.rows()).toEqual(middle.items);
    expect(store.hasMore()).toBe(true);
    expect(await store.previousPage()).toBe(true);
    expect(list).toHaveBeenLastCalledWith('project', { limit: 20 }, expect.any(AbortSignal));
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual(CASE_LIST_FIXTURE.items);
    expect(list).toHaveBeenCalledTimes(5);
  });
  it.each(['opaque+/=last-page', 'catalog-more-opaque'])(
    'retains the current page when the forward answer repeats cursor %s and retries that intent',
    async (repeated) => {
      const store = TestBed.inject(CaseListStore);
      store.init('project', {});
      await settle();
      const middle = { ...laterPage(), next_cursor: 'opaque+/=last-page' };
      list.mockResolvedValueOnce(middle);
      await store.nextPage();
      const first = CASE_LIST_FIXTURE.items[0];
      if (!first) throw new Error('Missing catalog fixture');
      const last = { items: [{ ...first, id: 'last-case' }], next_cursor: null };
      list.mockResolvedValueOnce({ ...last, next_cursor: repeated });
      expect(await store.nextPage()).toBe(false);
      expect(store.page()).toBe(2);
      expect(store.rows()).toEqual(middle.items);
      expect(store.hasMore()).toBe(true);
      expect(store.loadMoreFailure()?.code).toBe('cursor_repeated');
      list.mockResolvedValueOnce(last);
      expect(await store.retryPage()).toBe(true);
      expect(list).toHaveBeenLastCalledWith(
        'project',
        { limit: 20, cursor: 'opaque+/=last-page' },
        expect.any(AbortSignal),
      );
      expect(store.page()).toBe(3);
      expect(store.rows()).toEqual(last.items);
      expect(store.loadMoreFailure()).toBeNull();
      expect(store.hasMore()).toBe(false);
    },
  );
  it.each([false, true])(
    'does not navigate beyond a single page with rows: %s',
    async (hasRows) => {
      list.mockResolvedValue({
        items: hasRows ? CASE_LIST_FIXTURE.items : [],
        next_cursor: null,
      });
      const store = TestBed.inject(CaseListStore);
      store.init('project', {});
      await settle();
      await store.nextPage();
      await store.previousPage();
      expect(store.page()).toBe(1);
      expect(store.rows()).toEqual(hasRows ? CASE_LIST_FIXTURE.items : []);
      expect(list).toHaveBeenCalledTimes(1);
    },
  );
  it('discards cached owner ETags, intents and operations when a replacement is accepted', async () => {
    const row = CASE_LIST_FIXTURE.items[0];
    if (!row) throw new Error('Missing catalog fixture');
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    get.mockResolvedValueOnce({ row, etag: '"cached-owner-version"' });
    await store.reloadOwner(row.id);
    update.mockRejectedValueOnce(new Error('Unconfirmed write'));
    await store.assignOwner(row.id, 'new-owner');
    const oldIntent = update.mock.calls[0]?.[4];
    expect(store.ownerFailure(row.id)?.code).toBe('owner_outcome_unknown');
    list.mockResolvedValueOnce(laterPage());
    await store.nextPage();
    expect(store.ownerOps()).toEqual({});
    const fresh = { ...row, version: row.version + 10 };
    list.mockResolvedValueOnce({ items: [fresh], next_cursor: CASE_LIST_FIXTURE.next_cursor });
    await store.previousPage();
    update.mockResolvedValueOnce({ ...fresh, owner_membership_id: 'new-owner' });
    await store.assignOwner(row.id, 'new-owner');
    expect(update.mock.calls[1]?.[3]).toBe(`"${fresh.version}"`);
    expect(update.mock.calls[1]?.[4]).not.toEqual(oldIntent);
  });

  it.each(['network', 'cursor'] as const)(
    'preserves owner ETag, intent and failure across a refused %s page and retry',
    async (failure) => {
      const row = CASE_LIST_FIXTURE.items[0];
      if (!row) throw new Error('Missing catalog fixture');
      const store = TestBed.inject(CaseListStore);
      store.init('project', {});
      await settle();
      get.mockResolvedValueOnce({ row, etag: '"cached-owner-version"' });
      await store.reloadOwner(row.id);
      update.mockRejectedValue(new Error('Unconfirmed write'));
      await store.assignOwner(row.id, 'new-owner');
      const ops = store.ownerOps();
      const rows = store.rows();
      if (failure === 'network') {
        list.mockRejectedValue(new ApiRequestError(CATALOG_FAILURE));
      } else {
        list.mockResolvedValue({
          ...laterPage(),
          next_cursor: CASE_LIST_FIXTURE.next_cursor,
        });
      }
      expect(await store.nextPage()).toBe(false);
      expect(store.ownerOps()).toBe(ops);
      expect(store.rows()).toBe(rows);
      expect(await store.retryPage()).toBe(false);
      expect(store.ownerOps()).toBe(ops);
      await store.assignOwner(row.id, 'new-owner');
      expect(update.mock.calls[1]?.[3]).toBe('"cached-owner-version"');
      expect(update.mock.calls[1]?.[4]).toEqual(update.mock.calls[0]?.[4]);
    },
  );

  it.each(['confirmed', 'refused', 'unknown'] as const)(
    'ignores an old %s owner write after navigating away and back',
    async (outcome) => {
      const row = CASE_LIST_FIXTURE.items[0];
      if (!row) throw new Error('Missing catalog fixture');
      const store = TestBed.inject(CaseListStore);
      store.init('project', {});
      await settle();
      const pending = new Subject<CaseDto>();
      update.mockReturnValueOnce(firstValueFrom(pending));
      const write = store.assignOwner(row.id, 'old-page-owner');
      expect(store.ownerPending(row.id)).toBe(true);
      list.mockResolvedValueOnce(laterPage());
      await store.nextPage();
      expect(store.ownerOps()).toEqual({});
      const fresh = { ...row, version: row.version + 10, owner_membership_id: 'remote-owner' };
      list.mockResolvedValueOnce({ items: [fresh], next_cursor: null });
      await store.previousPage();
      if (outcome === 'confirmed') {
        pending.next({ ...row, owner_membership_id: 'old-page-owner' });
      } else {
        pending.error(
          outcome === 'refused'
            ? new ApiRequestError({ ...CATALOG_FAILURE, status: 412 })
            : new Error('Unconfirmed write'),
        );
      }
      await write;
      expect(store.rows()).toEqual([fresh]);
      expect(store.ownerOps()).toEqual({});
    },
  );

  it('allows a pending owner write to finish when navigation fails', async () => {
    const row = CASE_LIST_FIXTURE.items[0];
    if (!row) throw new Error('Missing catalog fixture');
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    const pending = new Subject<CaseDto>();
    update.mockReturnValueOnce(firstValueFrom(pending));
    const write = store.assignOwner(row.id, 'new-owner');
    list.mockRejectedValueOnce(new ApiRequestError(CATALOG_FAILURE));
    expect(await store.nextPage()).toBe(false);
    expect(store.ownerPending(row.id)).toBe(true);
    const confirmed = { ...row, owner_membership_id: 'new-owner', version: row.version + 1 };
    pending.next(confirmed);
    await write;
    expect(store.rows()[0]).toEqual(confirmed);
    expect(store.ownerPending(row.id)).toBe(false);
    expect(store.ownerFailure(row.id)).toBeNull();
  });
});
