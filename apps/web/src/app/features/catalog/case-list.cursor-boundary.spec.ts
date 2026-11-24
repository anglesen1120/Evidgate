import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CaseListResponse } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { SessionStore } from '../../core/session/session.store';
import { RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { CASE_LIST_FIXTURE } from './case-list-store.fixtures';
import { CaseListStore } from './case-list.store';

function page(id: string, cursor: string | null): CaseListResponse {
  const row = CASE_LIST_FIXTURE.items[0];
  if (!row) throw new Error('Missing catalog fixture');
  return { items: [{ ...row, id }], next_cursor: cursor };
}

describe('Catalog cursor boundaries', () => {
  const list = vi.fn<CaseListDataService['list']>();
  const first = page('first-case', 'opaque+/=second');
  const middle = page('middle-case', 'opaque+/=third');
  const last = page('last-case', null);

  async function settle() {
    TestBed.tick();
    await TestBed.inject(ApplicationRef).whenStable();
    TestBed.tick();
  }

  beforeEach(() => {
    list.mockReset().mockResolvedValue(first);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        CaseListStore,
        { provide: CaseListDataService, useValue: { list } },
        { provide: SessionStore, useValue: { has: () => false } },
      ],
    });
  });

  it('refuses an advancing cursor with no new rows and retries without committing its history', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockResolvedValueOnce({ ...first, next_cursor: 'unaccepted-cursor' });
    expect(await store.nextPage()).toBe(false);
    expect(store.rows()).toEqual(first.items);
    expect(store.page()).toBe(1);
    expect(store.requestCursors()).toEqual([null]);
    expect(store.nextCursor()).toBe(first.next_cursor);
    expect(store.loadMoreFailure()?.code).toBe('page_without_new_rows');
    list.mockResolvedValueOnce(last);
    expect(await store.retryPage()).toBe(true);
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { limit: 20, cursor: first.next_cursor },
      expect.any(AbortSignal),
    );
    expect(store.rows()).toEqual(last.items);
    expect(store.requestCursors()).toEqual([null, first.next_cursor]);
    expect(store.loadMoreFailure()).toBeNull();
  });

  it('returns from an empty terminal page without losing the previous request cursor', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockResolvedValueOnce({ items: [], next_cursor: null });
    expect(await store.nextPage()).toBe(true);
    expect(store.rows()).toEqual([]);
    expect(store.page()).toBe(2);
    expect(store.hasMore()).toBe(false);
    expect(await store.nextPage()).toBe(false);
    expect(await store.previousPage()).toBe(true);
    expect(store.rows()).toEqual(first.items);
    expect(store.page()).toBe(1);
    expect(store.requestCursors()).toEqual([null]);
  });

  it('replaces discarded forward history when a previous page supplies a new cursor', async () => {
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockResolvedValueOnce(middle).mockResolvedValueOnce(last);
    await store.nextPage();
    await store.nextPage();
    const changedMiddle = { ...middle, next_cursor: 'replacement+/=third' };
    list.mockResolvedValueOnce(changedMiddle).mockResolvedValueOnce(last);
    await store.previousPage();
    expect(store.requestCursors()).toEqual([null, first.next_cursor]);
    expect(await store.nextPage()).toBe(true);
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { limit: 20, cursor: changedMiddle.next_cursor },
      expect.any(AbortSignal),
    );
    expect(store.requestCursors()).toEqual([null, first.next_cursor, changedMiddle.next_cursor]);
    expect(store.rows()).toEqual(last.items);
  });

  it('allows only one direction to be pending and leaves its committed page visible', async () => {
    const pending = new Subject<CaseListResponse>();
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockResolvedValueOnce(middle);
    await store.nextPage();
    list.mockReturnValueOnce(firstValueFrom(pending));
    const previous = store.previousPage();
    expect(await store.nextPage()).toBe(false);
    expect(await store.previousPage()).toBe(false);
    expect(await store.retryPage()).toBe(false);
    expect(store.rows()).toEqual(middle.items);
    expect(store.page()).toBe(2);
    expect(list).toHaveBeenCalledTimes(3);
    pending.next(first);
    expect(await previous).toBe(true);
    expect(store.page()).toBe(1);
  });

  it.each(['resolve', 'reject'] as const)(
    'ignores an old pending page that will %s after the selected size changes',
    async (outcome) => {
      const pending = new Subject<CaseListResponse>();
      const store = TestBed.inject(CaseListStore);
      store.init('project', {});
      await settle();
      list.mockReturnValueOnce(firstValueFrom(pending));
      const oldPage = store.nextPage();
      const oldSignal = list.mock.calls.at(-1)?.[2];
      const replacement = page('replacement-case', null);
      list.mockResolvedValueOnce(replacement);
      store.setPageSize(50);
      expect(oldSignal?.aborted).toBe(true);
      await settle();
      if (outcome === 'resolve') pending.next(last);
      else pending.error(new Error('obsolete request failed'));
      expect(await oldPage).toBe(false);
      expect(store.pageSize()).toBe(50);
      expect(store.page()).toBe(1);
      expect(store.rows()).toEqual(replacement.items);
      expect(store.requestCursors()).toEqual([null]);
      expect(store.loadMoreFailure()).toBeNull();
      expect(await store.retryPage()).toBe(false);
      expect(list).toHaveBeenCalledTimes(3);
    },
  );

  it('preserves the pending page when only a route dialog selection changes', async () => {
    const pending = new Subject<CaseListResponse>();
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockReturnValueOnce(firstValueFrom(pending));
    const next = store.nextPage();
    store.init('project', { record: 'case:first-case', mode: 'view' });
    expect(store.loadingMore()).toBe(true);
    expect(list).toHaveBeenCalledTimes(2);
    pending.next(middle);
    expect(await next).toBe(true);
    expect(store.page()).toBe(2);
    expect(store.rows()).toEqual(middle.items);
  });

  it('resets a pending page for local search and never sends search to the SDK', async () => {
    const pending = new Subject<CaseListResponse>();
    const store = TestBed.inject(CaseListStore);
    store.init('project', {});
    await settle();
    list.mockReturnValueOnce(firstValueFrom(pending));
    const next = store.nextPage();
    store.init('project', { q: 'no-match-on-this-page' });
    await settle();
    pending.next(middle);
    expect(await next).toBe(false);
    expect(list).toHaveBeenLastCalledWith('project', { limit: 20 }, expect.any(AbortSignal));
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual(first.items);
    expect(store.visibleRows()).toEqual([]);
    expect(store.hasMore()).toBe(true);
    expect(store.requestCursors()).toEqual([null]);
  });

  it.each([
    [null, false],
    ['beyond-cap', true],
  ] as const)(
    'distinguishes complete current-page capacity from cursor %s',
    async (cursor, capped) => {
      TestBed.overrideProvider(RETAINED_ROWS_CAP, { useValue: 2 });
      const store = TestBed.inject(CaseListStore);
      store.init('project', {});
      await settle();
      const second = page('second-case', null);
      const third = page('third-case', null);
      list.mockResolvedValueOnce({ items: [...second.items, ...third.items], next_cursor: cursor });
      expect(await store.nextPage()).toBe(true);
      expect(store.rows()).toEqual([...second.items, ...third.items]);
      expect(store.page()).toBe(2);
      expect(store.browseCapped()).toBe(capped);
      expect(store.hasMore()).toBe(false);
      expect(await store.previousPage()).toBe(true);
      expect(store.rows()).toEqual(first.items);
      expect(store.browseCapped()).toBe(false);
      expect(store.hasMore()).toBe(true);
    },
  );

  it.each(['0', '-1', 'NaN', 'Infinity', '25', '20.5', ''])(
    'defaults unsupported route limit %s without forwarding a route cursor',
    async (limit) => {
      const store = TestBed.inject(CaseListStore);
      store.init('project', { limit, cursor: 'untrusted-route-cursor' });
      await settle();
      expect(store.pageSize()).toBe(20);
      expect(store.page()).toBe(1);
      expect(list).toHaveBeenCalledExactlyOnceWith(
        'project',
        { limit: 20 },
        expect.any(AbortSignal),
      );
    },
  );
});
