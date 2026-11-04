import { TestBed } from '@angular/core/testing';
import { RunDataService } from '../data/run-data.service';
import { RUN_LIST_PAGE_LIMIT, RunListStore } from './run-list.store';
import { FakeRunDataService, rows } from './run-list-store.fixtures';

describe('RunListStore ref validation', () => {
  let data: FakeRunDataService;
  let store: InstanceType<typeof RunListStore>;

  beforeEach(() => {
    data = new FakeRunDataService();
    TestBed.configureTestingModule({
      providers: [RunListStore, { provide: RunDataService, useValue: data }],
    });
    store = TestBed.inject(RunListStore);
  });
  describe('ref name validation on the form path', () => {
    it('trims a padded ref name before sending it and keeps it in the URL query', async () => {
      data.all = rows(2);
      await store.init('p-1');
      expect(data.calls).toHaveLength(1);

      await store.setFilters({ refName: '  refs/heads/main  ' });

      expect(store.filters().refName).toBe('refs/heads/main');
      expect(store.refRejected()).toBe(false);
      expect(data.calls[1]?.request).toEqual({
        limit: RUN_LIST_PAGE_LIMIT,
        refName: 'refs/heads/main',
      });
      expect(store.toQuery()).toEqual({ ref: 'refs/heads/main' });
    });

    it('refuses a ref name longer than 255 code points: not sent, not in the query, reported', async () => {
      data.all = rows(2);
      await store.init('p-1');
      const before = data.calls.length;

      await store.setFilters({ refName: 'x'.repeat(256) });

      // No second read: the refused value narrowed nothing, so the list it applies to did not change.
      expect(data.calls).toHaveLength(before);
      expect(store.filters().refName).toBe('');
      expect(store.refRejected()).toBe(true);
      expect(store.toQuery()).toEqual({});
      // 255 code points is the accepted bound, not 256.
      await store.setFilters({ refName: 'x'.repeat(255) });
      expect(store.refRejected()).toBe(false);
      expect(data.calls.at(-1)?.request.refName).toBe('x'.repeat(255));
    });

    it('refuses a ref name carrying a control character and drops the previous one', async () => {
      data.all = rows(2);
      await store.init('p-1', { ref: 'refs/heads/main' });
      expect(data.calls).toHaveLength(1);

      await store.setFilters({ refName: 'refs/heads/\u0000main' });

      // The accepted filter is gone: the list restarts unfiltered instead of keeping the old value
      // while the field shows the refused one.
      expect(data.calls).toHaveLength(2);
      expect(data.calls[1]?.request).toEqual({ limit: RUN_LIST_PAGE_LIMIT });
      expect(store.filters().refName).toBe('');
      expect(store.refRejected()).toBe(true);
      expect(store.toQuery()).toEqual({});
    });

    it('treats a whitespace-only ref name as a cleared filter, not a refusal', async () => {
      data.all = rows(2);
      await store.init('p-1', { ref: 'refs/heads/main' });

      await store.setFilters({ refName: '   ' });

      expect(store.filters().refName).toBe('');
      expect(store.refRejected()).toBe(false);
      expect(store.toQuery()).toEqual({});
      expect(data.calls.at(-1)?.request).toEqual({ limit: RUN_LIST_PAGE_LIMIT });
    });

    it('clears the reported refusal when the next value is accepted', async () => {
      data.all = rows(2);
      await store.init('p-1');
      await store.setFilters({ refName: 'bad\nref' });
      expect(store.refRejected()).toBe(true);

      await store.setFilters({ refName: 'refs/heads/main' });

      expect(store.refRejected()).toBe(false);
      expect(store.filters().refName).toBe('refs/heads/main');
    });

    it('clears the reported refusal when the URL is the source again', async () => {
      data.all = rows(2);
      await store.init('p-1');
      await store.setFilters({ refName: 'bad\nref' });
      expect(store.refRejected()).toBe(true);

      await store.init('p-1', { ref: 'refs/heads/main' });

      expect(store.refRejected()).toBe(false);
      expect(store.filters().refName).toBe('refs/heads/main');
    });

    it('reports a refused value even while the stored ref filter is empty', async () => {
      // No read yet: the stored ref filter is already '', so the refused value leaves the filter
      // state untouched and only the report changes. It must not be swallowed for that reason.
      await store.setFilters({ refName: 'a'.repeat(256) });

      expect(store.filters().refName).toBe('');
      expect(store.refRejected()).toBe(true);
      expect(data.calls).toEqual([]);

      await store.setFilters({ refName: '' });

      expect(store.refRejected()).toBe(false);
    });

    it('clears the reported refusal when the filters are cleared', async () => {
      data.all = rows(2);
      await store.init('p-1');
      await store.setFilters({ refName: 'bad\nref' });
      expect(store.refRejected()).toBe(true);

      await store.setFilters({ state: '', decision: '', scope: '', refName: '' });

      expect(store.refRejected()).toBe(false);
      expect(store.toQuery()).toEqual({});
    });
  });
});
