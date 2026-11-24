import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ApiRequestError } from '../../core/http/api-error';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { SessionStore } from '../../core/session/session.store';
import { CASE_LIST_FIXTURE, CATALOG_FAILURE } from './case-list-store.fixtures';
import { CaseListStore } from './case-list.store';

describe('Catalog cursor pages', () => {
  const list = vi.fn<CaseListDataService['list']>();
  async function settle() {
    TestBed.tick();
    await TestBed.inject(ApplicationRef).whenStable();
    TestBed.tick();
  }
  beforeEach(() => {
    list.mockReset().mockResolvedValue(CASE_LIST_FIXTURE);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        CaseListStore,
        { provide: CaseListDataService, useValue: { list } },
        { provide: SessionStore, useValue: { has: () => false } },
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
});
