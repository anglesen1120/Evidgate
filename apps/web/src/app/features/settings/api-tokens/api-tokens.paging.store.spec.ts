import { TestBed } from '@angular/core/testing';
import { convertToParamMap } from '@angular/router';
import { mount, project, refusal, ROW } from './api-tokens.test-support';

describe('ApiTokensStore record pages', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('replaces first, middle and last pages and returns through the cursor history', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    await h.settle();
    expect(h.store.page()).toBe(1);
    expect(h.store.pageSize()).toBe(20);
    h.list.mockResolvedValueOnce({ items: [{ ...ROW, id: 'second' }], next_cursor: 'last' });
    h.store.next();
    await h.settle();
    expect(h.store.page()).toBe(2);
    expect(h.store.rows().map((row) => row.id)).toEqual(['second']);
    h.list.mockResolvedValueOnce({ items: [{ ...ROW, id: 'third' }], next_cursor: null });
    h.store.next();
    await h.settle();
    expect(h.store.page()).toBe(3);
    expect(h.store.hasNext()).toBe(false);
    h.list.mockResolvedValueOnce({ items: [{ ...ROW, id: 'second' }], next_cursor: 'last' });
    h.store.previous();
    await h.settle();
    expect(h.store.cursor()).toBe('middle');
    expect(h.store.page()).toBe(2);
    expect(h.store.rows().map((row) => row.id)).toEqual(['second']);
    h.store.previous();
    await h.settle();
    expect(h.store.cursor()).toBeUndefined();
    expect(h.store.page()).toBe(1);
    const reads = h.list.mock.calls.length;
    h.store.previous();
    h.store.next();
    await h.settle();
    expect(h.list).toHaveBeenCalledTimes(reads);
  });

  it.each([20, 50, 100])('uses selected size %i and restarts from the first page', async (size) => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    await h.settle();
    h.store.next();
    await h.settle();
    h.store.setPageSize(size);
    await h.settle();
    expect(h.store.page()).toBe(1);
    expect(h.store.cursor()).toBeUndefined();
    expect(h.store.pageSize()).toBe(size);
    expect(h.list).toHaveBeenLastCalledWith('project-1', undefined, expect.any(AbortSignal), size);
  });

  it('keeps the previous page on a later failure and retries the requested cursor', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    await h.settle();
    h.list.mockRejectedValueOnce(refusal('network'));
    h.store.next();
    await h.settle();
    expect(h.store.phase()).toBe('success');
    expect(h.store.page()).toBe(1);
    expect(h.store.rows()).toEqual([ROW]);
    expect(h.store.loadError()?.kind).toBe('network');
    h.list.mockResolvedValueOnce({ items: [{ ...ROW, id: 'second' }], next_cursor: null });
    h.store.reload();
    await h.settle();
    expect(h.store.page()).toBe(2);
    expect(h.store.rows().map((row) => row.id)).toEqual(['second']);
    expect(h.store.loadError()).toBeNull();
  });

  it('does not retain token metadata after a later authorization refusal', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    await h.settle();
    h.list.mockRejectedValueOnce(refusal('forbidden'));
    h.store.next();
    await h.settle();
    expect(h.store.phase()).toBe('forbidden');
    expect(h.store.rows()).toEqual([]);
  });

  it('resets history on project changes and mutations, but not opening a dialog', async () => {
    const h = mount(undefined, { projects: [project('project-1'), project('project-2')] });
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    await h.settle();
    h.store.next();
    await h.settle();
    h.store.openCreate();
    expect(h.store.page()).toBe(2);
    h.store.closeCreate();
    h.store.reloadFirst();
    await h.settle();
    expect(h.store.page()).toBe(1);
    h.store.selectProject('project-2');
    await h.settle();
    expect(h.store.page()).toBe(1);
    expect(h.store.cursor()).toBeUndefined();
  });

  it('keeps empty and single pages bounded and refuses unsupported sizes', async () => {
    const h = mount(() => Promise.resolve({ items: [], next_cursor: null }));
    await h.settle();
    expect(h.store.phase()).toBe('empty');
    expect(h.store.page()).toBe(1);
    h.store.next();
    h.store.previous();
    h.store.setPageSize(200);
    await h.settle();
    expect(h.store.pageSize()).toBe(20);
    expect(h.list).toHaveBeenCalledTimes(1);
  });

  it('restores known pages from URL navigation without discarding unrelated query state', async () => {
    const h = mount(undefined, { query: { project: 'project-1', marker: 'keep' } });
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'middle' });
    await h.settle();
    h.store.next();
    await h.settle();
    h.query.next(convertToParamMap({ project: 'project-1', cursor: 'middle', marker: 'keep' }));
    h.store.settleMove();
    h.query.next(convertToParamMap({ project: 'project-1', marker: 'keep' }));
    await h.settle();
    expect(h.store.page()).toBe(1);
    expect(h.store.cursor()).toBeUndefined();
  });

  it('returns to a restored opaque page after following its next cursor', async () => {
    const h = mount();
    await h.settle();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'restored-next' });
    h.query.next(convertToParamMap({ project: 'project-1', cursor: 'restored' }));
    await h.settle();
    expect(h.store.page()).toBe(1);
    h.store.next();
    await h.settle();
    expect(h.store.page()).toBe(2);
    h.store.previous();
    await h.settle();
    expect(h.store.cursor()).toBe('restored');
    expect(h.list).toHaveBeenLastCalledWith('project-1', 'restored', expect.any(AbortSignal), 20);
  });
});
