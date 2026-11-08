import { TestBed } from '@angular/core/testing';
import { flush, mountRetention } from './retention-form.test-support';

describe('RetentionStore write attempt ownership', () => {
  afterEach(() => {
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
    vi.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  it('keeps a newer PUT saving when an older answer returns for the same organization and project', async () => {
    const h = mountRetention();
    await h.load();
    const first = h.store.save({ artifact_days: 30, metadata_days: 90 });
    const firstPut = await h.take('PUT');
    h.org.set({ id: 'o-2' });
    h.projects.set([{ id: 'p-2', name: 'Second org project', org_id: 'o-2' }]);
    flush(await h.take('GET', 'p-2'));
    await h.settle();
    const other = h.store.save({ artifact_days: 40, metadata_days: 100 });
    const otherPut = await h.take('PUT', 'p-2');
    h.org.set({ id: 'o-1' });
    h.projects.set([{ id: 'p-1', name: 'First org project', org_id: 'o-1' }]);
    flush(await h.take('GET'));
    await h.settle();
    const newest = h.store.save({ artifact_days: 60, metadata_days: 120 });
    const newestPut = await h.take('PUT');
    expect(h.store.saving()).toBe(true);
    flush(firstPut, { artifact_days: 30, metadata_days: 90 });
    await first;
    const stillSaving = h.store.saving();
    flush(newestPut, { artifact_days: 60, metadata_days: 120 });
    expect(await newest).toBe(true);
    flush(otherPut, { artifact_days: 40, metadata_days: 100 });
    await other;
    await h.settle();
    h.http.verify();
    expect(stillSaving).toBe(true);
  });

  it('accepts the answer of the only write for a project after another project saved and the selection returned', async () => {
    const h = mountRetention();
    await h.load();
    const first = h.store.save({ artifact_days: 30, metadata_days: 90 });
    const firstPut = await h.take('PUT');
    h.org.set({ id: 'o-2' });
    h.projects.set([{ id: 'p-2', name: 'Second org project', org_id: 'o-2' }]);
    flush(await h.take('GET', 'p-2'));
    await h.settle();
    const other = h.store.save({ artifact_days: 40, metadata_days: 100 });
    const otherPut = await h.take('PUT', 'p-2');
    h.org.set({ id: 'o-1' });
    h.projects.set([{ id: 'p-1', name: 'First org project', org_id: 'o-1' }]);
    flush(await h.take('GET'));
    await vi.waitFor(() => {
      TestBed.tick();
      expect(h.store.loading()).toBe(false);
    });
    flush(firstPut, { artifact_days: 30, metadata_days: 90 });
    expect(await first).toBe(true);
    expect(h.store.policy()).toEqual({ artifact_days: 30, metadata_days: 90 });
    flush(otherPut, { artifact_days: 40, metadata_days: 100 });
    await other;
    await h.settle();
    h.http.verify();
  });
});
