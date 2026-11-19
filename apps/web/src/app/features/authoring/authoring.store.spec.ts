import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuthoringTreeResponse, NodeDto } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';

import { ApiRequestError, type ApiError } from '../../core/http/api-error';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringStore } from './authoring.store';

const ROOT: NodeDto = {
  node_id: '018f0000-0000-7000-8000-0000000000a1',
  parent_id: null,
  kind: 'module',
  title: 'Thanh toán',
  position: 0,
  version: 1,
  child_count: 2,
  item_count: 3,
};
const CHILD: NodeDto = {
  node_id: '018f0000-0000-7000-8000-0000000000a2',
  parent_id: ROOT.node_id,
  kind: 'category',
  title: 'Hoàn tiền',
  position: 0,
  version: 1,
  child_count: 0,
  item_count: 1,
};
const SIBLING: NodeDto = {
  node_id: '018f0000-0000-7000-8000-0000000000a3',
  parent_id: ROOT.node_id,
  kind: 'category',
  title: 'Thẻ hết hạn',
  position: 1,
  version: 1,
  child_count: 0,
  item_count: 0,
};
const PATH = [{ node_id: ROOT.node_id, kind: 'module', title: ROOT.title }] as const;
const FAILURE: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'unavailable',
  message: 'Temporarily unavailable',
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: 30,
};

function page(
  items: NodeDto[],
  nextCursor: string | null = null,
  extra: Pick<AuthoringTreeResponse, 'node' | 'path'> = {},
): AuthoringTreeResponse {
  return { items, next_cursor: nextCursor, ...extra };
}

function refusal(kind: ApiError['kind'], status: number): ApiRequestError {
  return new ApiRequestError({ ...FAILURE, kind, status, retryAfterSeconds: null });
}

describe('AuthoringStore', () => {
  const listTree = vi.fn<AuthoringDataService['listTree']>();
  beforeEach(() => {
    listTree.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        AuthoringStore,
        {
          provide: AuthoringDataService,
          useValue: {
            listTree,
            listChecklistItems: vi.fn(),
            listManualCases: vi.fn(),
          },
        },
      ],
    });
  });
  async function settle() {
    TestBed.tick();
    await TestBed.inject(ApplicationRef).whenStable();
    TestBed.tick();
  }

  it('restores the addressed node and its root-first path from the URL id', async () => {
    listTree.mockImplementation((_projectId, read) =>
      Promise.resolve(
        read.nodeId === CHILD.node_id
          ? page([SIBLING], null, { node: CHILD, path: [...PATH] })
          : page([ROOT]),
      ),
    );
    const store = TestBed.inject(AuthoringStore);
    await store.init('project', CHILD.node_id);
    await settle();

    expect(listTree).toHaveBeenCalledWith(
      'project',
      { nodeId: CHILD.node_id },
      expect.any(AbortSignal),
    );
    expect(listTree).toHaveBeenCalledWith('project', {}, expect.any(AbortSignal));
    expect(store.selectedNodeId()).toBe(CHILD.node_id);
    expect(store.selectedNode()).toEqual(CHILD);
    expect(store.path()).toEqual(PATH);
    expect(store.roots().items).toEqual([ROOT]);
    expect(store.children()[CHILD.node_id]?.items).toEqual([SIBLING]);
    expect(store.treeStatus()).toBe('ready');
    expect(store.treeError()).toBeNull();

    await store.expand(CHILD.node_id);
    expect(listTree).toHaveBeenCalledTimes(2);
  });

  it('loads the roots once and reads no branch until it is expanded', async () => {
    listTree.mockResolvedValue(page([ROOT]));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();

    expect(listTree).toHaveBeenCalledTimes(1);
    expect(listTree).toHaveBeenCalledWith('project', {}, expect.any(AbortSignal));
    expect(store.roots()).toEqual({
      items: [ROOT],
      nextCursor: null,
      status: 'ready',
      error: null,
    });
    expect(store.children()).toEqual({});
  });

  it('does not re-read the tree when the route re-initialises the same view', async () => {
    listTree.mockResolvedValue(page([ROOT]));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();
    await store.init('project');
    await settle();
    expect(listTree).toHaveBeenCalledTimes(1);
  });

  it('loads a branch on explicit expansion and refetches only after a failed one', async () => {
    listTree.mockImplementation((_projectId, read) =>
      read.parentId === undefined
        ? Promise.resolve(page([ROOT]))
        : Promise.resolve(page([CHILD, SIBLING])),
    );
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();

    await store.expand(ROOT.node_id);
    await settle();
    expect(listTree).toHaveBeenCalledWith(
      'project',
      { parentId: ROOT.node_id },
      expect.any(AbortSignal),
    );
    expect(store.children()[ROOT.node_id]).toEqual({
      items: [CHILD, SIBLING],
      nextCursor: null,
      status: 'ready',
      error: null,
    });

    await store.expand(ROOT.node_id);
    expect(listTree).toHaveBeenCalledTimes(2);

    listTree.mockImplementationOnce(() => Promise.reject(refusal('unavailable', 503)));
    await store.expand(SIBLING.node_id);
    await settle();
    expect(store.children()[SIBLING.node_id]?.status).toBe('error');
    expect(store.children()[SIBLING.node_id]?.error?.kind).toBe('unavailable');
    await store.expand(SIBLING.node_id);
    expect(listTree).toHaveBeenCalledTimes(4);
  });

  it('drops a selection response that arrives after a newer selection', async () => {
    const first = new Subject<AuthoringTreeResponse>();
    const second = new Subject<AuthoringTreeResponse>();
    listTree.mockImplementation((_projectId, read) => {
      if (read.nodeId === ROOT.node_id) return firstValueFrom(first);
      if (read.nodeId === CHILD.node_id) return firstValueFrom(second);
      return Promise.resolve(page([]));
    });
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();

    store.selectNode(ROOT.node_id);
    expect(store.selectNode(CHILD.node_id)).toBe(CHILD.node_id);
    const superseded = listTree.mock.calls.find(([, read]) => read.nodeId === ROOT.node_id)?.[2];
    expect(superseded?.aborted).toBe(true);

    second.next(page([SIBLING], null, { node: CHILD, path: [...PATH] }));
    await settle();
    first.next(page([ROOT], null, { node: ROOT, path: [] }));
    await settle();

    expect(store.selectedNodeId()).toBe(CHILD.node_id);
    expect(store.selectedNode()).toEqual(CHILD);
    expect(store.path()).toEqual(PATH);
  });

  it('keeps the rows an earlier read loaded and marks the tree stale when a refresh fails', async () => {
    listTree.mockResolvedValueOnce(page([ROOT]));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();

    const refresh = new Subject<AuthoringTreeResponse>();
    listTree.mockReturnValueOnce(firstValueFrom(refresh));
    const retried = store.retryTree();
    await settle();
    expect(store.treeStatus()).toBe('loading');

    refresh.error(refusal('unavailable', 503));
    await retried;
    await settle();

    expect(store.roots().items).toEqual([ROOT]);
    expect(store.roots().status).toBe('error');
    expect(store.treeError()).toEqual(
      expect.objectContaining({ kind: 'unavailable', status: 503 }),
    );
    expect(store.stale()).toBe(true);

    listTree.mockResolvedValueOnce(page([ROOT, CHILD]));
    await store.retryTree();
    await settle();
    expect(store.stale()).toBe(false);
    expect(store.treeStatus()).toBe('ready');
  });

  it('reports a typed refusal for the first load without inventing rows', async () => {
    listTree.mockRejectedValue(refusal('forbidden', 403));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();

    expect(store.roots().items).toEqual([]);
    expect(store.roots().status).toBe('error');
    expect(store.treeError()).toEqual(expect.objectContaining({ kind: 'forbidden', status: 403 }));
    expect(store.stale()).toBe(false);
  });

  it('refuses a node read that omits the requested node or path instead of an empty selection', async () => {
    listTree.mockImplementation((_projectId, read) =>
      Promise.resolve(
        read.nodeId === undefined
          ? page([ROOT])
          : page([SIBLING], null, { node: CHILD, path: null }),
      ),
    );
    const store = TestBed.inject(AuthoringStore);
    await store.init('project', CHILD.node_id);
    await settle();

    expect(store.selectionStatus()).toBe('error');
    expect(store.treeError()?.code).toBe('selection_projection_missing');
    expect(store.selectedNode()).toBeNull();
    expect(store.path()).toEqual([]);
    expect(store.children()[CHILD.node_id]).toBeUndefined();
  });

  it('maps an unexpected rejection to a typed failure, never to an empty success', async () => {
    listTree.mockRejectedValue(new Error('private diagnostic'));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();

    expect(store.roots().status).toBe('error');
    expect(store.treeError()?.code).toBe('request_unresolved');
    expect(store.treeError()?.message).not.toContain('private diagnostic');
  });

  it('clears the previous project and aborts its read when the project changes', async () => {
    const previous = new Subject<AuthoringTreeResponse>();
    listTree.mockImplementation((projectId) =>
      projectId === 'old' ? firstValueFrom(previous) : Promise.resolve(page([ROOT])),
    );
    const store = TestBed.inject(AuthoringStore);
    void store.init('old');
    await settle();
    await store.init('new');
    await settle();

    expect(listTree.mock.calls[0]?.[2].aborted).toBe(true);
    expect(store.projectId()).toBe('new');
    expect(store.roots().items).toEqual([ROOT]);

    previous.next(page([CHILD]));
    await settle();
    expect(store.roots().items).toEqual([ROOT]);
  });

  it('follows the roots cursor once and refuses a second follow while the page is loading', async () => {
    const more = new Subject<AuthoringTreeResponse>();
    listTree.mockImplementation((_projectId, read) =>
      read.cursor === 'next' ? firstValueFrom(more) : Promise.resolve(page([ROOT], 'next')),
    );
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();

    const following = store.loadMoreChildren(null);
    await settle();
    expect(listTree).toHaveBeenLastCalledWith(
      'project',
      { cursor: 'next' },
      expect.any(AbortSignal),
    );
    await expect(store.loadMoreChildren(null)).resolves.toBe(false);
    expect(listTree).toHaveBeenCalledTimes(2);

    more.next(page([CHILD]));
    await expect(following).resolves.toBe(true);
    await settle();
    expect(store.roots().items).toEqual([ROOT, CHILD]);
    expect(store.roots().nextCursor).toBeNull();
    await expect(store.loadMoreChildren(null)).resolves.toBe(false);
  });

  it('appends a children page without repeating a node the page shifted back', async () => {
    const more = new Subject<AuthoringTreeResponse>();
    listTree.mockImplementation((_projectId, read) => {
      if (read.parentId === undefined) return Promise.resolve(page([ROOT]));
      if (read.cursor === 'more') return firstValueFrom(more);
      return Promise.resolve(page([CHILD], 'more'));
    });
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();
    await store.expand(ROOT.node_id);
    await settle();

    const following = store.loadMoreChildren(ROOT.node_id);
    more.next(page([CHILD, SIBLING]));
    await expect(following).resolves.toBe(true);
    await settle();
    expect(store.children()[ROOT.node_id]?.items).toEqual([CHILD, SIBLING]);
    expect(store.children()[ROOT.node_id]?.nextCursor).toBeNull();
  });

  it('exposes the server counts unchanged and invents no pending-change count', async () => {
    listTree.mockResolvedValue(page([ROOT]));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();

    const node = store.roots().items[0];
    expect(node?.child_count).toBe(2);
    expect(node?.item_count).toBe(3);
    expect(node !== undefined && 'pending_change_count' in node).toBe(false);
  });

  it('returns the selected id for the caller to mirror and clears the selection with null', async () => {
    listTree.mockImplementation((_projectId, read) =>
      Promise.resolve(
        read.nodeId === undefined ? page([ROOT]) : page([], null, { node: CHILD, path: [...PATH] }),
      ),
    );
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await settle();

    expect(store.selectNode(CHILD.node_id)).toBe(CHILD.node_id);
    await settle();
    expect(store.selectedNode()).toEqual(CHILD);

    expect(store.selectNode(null)).toBeNull();
    expect(store.selectedNodeId()).toBeNull();
    expect(store.selectedNode()).toBeNull();
    expect(store.path()).toEqual([]);
    expect(store.selectionStatus()).toBe('idle');
  });
});
