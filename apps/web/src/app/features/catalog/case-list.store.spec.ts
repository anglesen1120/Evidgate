import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CaseListResponse } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { ApiRequestError } from '../../core/http/api-error';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { CASE_LIST_FIXTURE, CATALOG_FAILURE } from './case-list-store.fixtures';
import { caseFiltersToQuery, CaseListStore } from './case-list.store';

describe('CaseListStore', () => {
  let list = vi.fn<CaseListDataService['list']>();
  beforeEach(() => {
    list = vi.fn<CaseListDataService['list']>().mockResolvedValue(CASE_LIST_FIXTURE);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        CaseListStore,
        { provide: CaseListDataService, useValue: { list } },
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

  it('round-trips server filters, local search and opaque cursor without changing the page limit', async () => {
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
      { status: 'missing', framework: 'playwright', cursor: 'opaque+/=', limit: 25 },
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
});
