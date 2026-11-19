import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuthoringTreeResponse, NodeDto } from '@evidgate/sdk';
import { RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { apiError } from '../../shared/testing/http-data.testing';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringStore } from './authoring.store';

const root: NodeDto = {
  node_id: 'root',
  parent_id: null,
  kind: 'module',
  title: 'Root',
  position: 0,
  version: 1,
  child_count: 10_001,
  item_count: 0,
};
const nodes = (count: number, offset = 0): NodeDto[] =>
  Array.from({ length: count }, (_, index) => ({
    ...root,
    node_id: `child-${offset + index}`,
    parent_id: root.node_id,
    child_count: 0,
  }));
const page = (items: NodeDto[], next_cursor: string | null = null): AuthoringTreeResponse => ({
  items,
  next_cursor,
});

// The public store exercises the same read ownership and cancellation as the routed surface.
describe('authoring tree retained-row bounds', () => {
  const listTree = vi.fn<AuthoringDataService['listTree']>();
  beforeEach(() => {
    listTree.mockReset().mockResolvedValue(page([root]));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        AuthoringStore,
        { provide: AuthoringDataService, useValue: { listTree } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it.each([
    [10_000, null, 'ready'],
    [10_000, 'beyond-cap', 'capped'],
    [10_001, null, 'capped'],
  ] as const)('bounds a roots page at %s nodes with cursor %s', async (count, cursor, status) => {
    listTree.mockResolvedValueOnce(page(nodes(count), cursor));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');

    expect(store.roots().items).toHaveLength(10_000);
    expect(store.roots().items.at(-1)?.node_id).toBe('child-9999');
    expect(store.roots().status).toBe(status);
    expect(store.treeStatus()).toBe('ready');
    expect(store.roots().nextCursor).toBeNull();
    expect(await store.loadMoreChildren(null)).toBe(false);
    expect(listTree).toHaveBeenCalledTimes(1);
  });

  it.each([
    [10_000, null, 'ready'],
    [10_000, 'beyond-cap', 'capped'],
    [10_001, null, 'capped'],
  ] as const)(
    'bounds an expanded branch at %s nodes with cursor %s',
    async (count, cursor, status) => {
      const store = TestBed.inject(AuthoringStore);
      await store.init('project');
      listTree.mockResolvedValueOnce(page(nodes(count), cursor));
      await store.expand(root.node_id);

      const branch = store.children()[root.node_id];
      expect(branch?.items).toHaveLength(10_000);
      expect(branch?.status).toBe(status);
      expect(branch?.nextCursor).toBeNull();
      expect(await store.loadMoreChildren(root.node_id)).toBe(false);
      expect(listTree).toHaveBeenCalledTimes(2);
    },
  );

  it('bounds roots at the injected retained-row cap rather than one compiled in', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        AuthoringStore,
        { provide: AuthoringDataService, useValue: { listTree } },
        { provide: RETAINED_ROWS_CAP, useValue: 2 },
      ],
    });
    listTree.mockReset().mockResolvedValueOnce(page(nodes(3), 'more'));

    const store = TestBed.inject(AuthoringStore);
    await store.init('project');

    expect(store.roots().items.map((node) => node.node_id)).toEqual(['child-0', 'child-1']);
    expect(store.roots().status).toBe('capped');
    expect(store.roots().nextCursor).toBeNull();
  });

  it.each([null, root.node_id])('caps the accumulated walk for parent %s', async (parentId) => {
    const store = TestBed.inject(AuthoringStore);
    if (parentId === null) {
      listTree.mockResolvedValueOnce(page(nodes(9_999), 'last-page'));
      await store.init('project');
    } else {
      await store.init('project');
      listTree.mockResolvedValueOnce(page(nodes(9_999), 'last-page'));
      await store.expand(parentId);
    }
    listTree.mockResolvedValueOnce(page(nodes(2, 9_999), 'discarded'));
    expect(await store.loadMoreChildren(parentId)).toBe(true);

    const branch = parentId === null ? store.roots() : store.children()[parentId];
    expect(branch?.items).toHaveLength(10_000);
    expect(branch?.items.at(-1)?.node_id).toBe('child-9999');
    expect(branch?.status).toBe('capped');
    expect(branch?.nextCursor).toBeNull();
    const reads = listTree.mock.calls.length;
    expect(await store.loadMoreChildren(parentId)).toBe(false);
    expect(listTree).toHaveBeenCalledTimes(reads);
  });

  it.each([null, 'fresh-first-page'])(
    'caps selected-node children and preserves the capped state when selection refresh has cursor %s',
    async (cursor) => {
      listTree.mockImplementation((_project, query) =>
        Promise.resolve(
          query.nodeId ? { ...page(nodes(10_001)), node: root, path: [] } : page([root]),
        ),
      );
      const store = TestBed.inject(AuthoringStore);
      await store.init('project', root.node_id);
      expect(store.children()[root.node_id]?.items).toHaveLength(10_000);
      expect(store.children()[root.node_id]?.status).toBe('capped');

      const changed: NodeDto = {
        ...root,
        node_id: 'child-0',
        parent_id: root.node_id,
        child_count: 0,
        title: 'Refreshed title',
      };
      listTree.mockImplementation((_project, query) =>
        Promise.resolve(
          query.nodeId ? { ...page([changed], cursor), node: root, path: [] } : page([root]),
        ),
      );
      await store.retryTree();
      const branch = store.children()[root.node_id];
      expect(branch?.items).toHaveLength(10_000);
      expect(branch?.items[0]?.title).toBe('Refreshed title');
      expect(branch?.status).toBe('capped');
      expect(branch?.nextCursor).toBeNull();
    },
  );

  it('keeps the capped roots notice when a failed refresh retains its truncated rows', async () => {
    listTree.mockResolvedValueOnce(page(nodes(10_001)));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    expect(store.roots().status).toBe('capped');
    listTree.mockRejectedValueOnce(apiError('network', 0));
    await store.retryTree();
    expect(store.roots().items).toHaveLength(10_000);
    expect(store.roots().status).toBe('capped');
    expect(store.roots().error?.kind).toBe('network');
    expect(store.treeStatus()).toBe('error');
    expect(store.rootsStale()).toBe(true);
  });

  it.each([
    ['forbidden', 403],
    ['not_found', 404],
  ] as const)(
    'retries capped roots with a retained %s error on route init',
    async (kind, status) => {
      listTree.mockResolvedValueOnce(page(nodes(10_001)));
      const store = TestBed.inject(AuthoringStore);
      await store.init('project');
      await store.init('project');
      expect(listTree).toHaveBeenCalledTimes(1);
      listTree.mockRejectedValueOnce(apiError(kind, status));
      await store.retryTree();
      expect(store.roots().status).toBe('capped');
      expect(store.treeError()?.kind).toBe(kind);

      await store.init('project');
      expect(listTree).toHaveBeenCalledTimes(3);
      expect(store.roots().status).toBe('ready');
      expect(store.roots().items).toEqual([root]);
      expect(store.treeError()).toBeNull();
      expect(store.treeStatus()).toBe('ready');
      expect(store.rootsStale()).toBe(false);
    },
  );

  it('clears a capped roots walk when a successful refresh replaces it', async () => {
    listTree.mockResolvedValueOnce(page(nodes(10_001)));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    expect(store.roots().status).toBe('capped');
    await store.retryTree();
    expect(store.roots().status).toBe('ready');
    expect(store.roots().items).toEqual([root]);
  });
});
