import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuthoringTreeResponse, NodeDto } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { ApiRequestError, type ApiError } from '../../core/http/api-error';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringStore } from './authoring.store';

const root: NodeDto = {
  node_id: 'root',
  parent_id: null,
  kind: 'module',
  title: 'Root',
  position: 0,
  version: 1,
  child_count: 2,
  item_count: 0,
};
const child: NodeDto = { ...root, node_id: 'child' };
const extra: NodeDto = { ...root, node_id: 'extra' };
const page = (items: NodeDto[], cursor: string | null = null): AuthoringTreeResponse => ({
  items,
  next_cursor: cursor,
});
const addressed = (items: NodeDto[], cursor: string | null = null): AuthoringTreeResponse => ({
  ...page(items, cursor),
  node: root,
  path: [],
});
const refusal = (kind: ApiError['kind'], status: number): ApiRequestError =>
  new ApiRequestError({
    kind,
    status,
    code: kind,
    message: kind,
    requestId: null,
    retryable: status === 503,
    retryAfterSeconds: null,
    details: [],
  });

describe('authoring tree read coordination', () => {
  const listTree = vi.fn<AuthoringDataService['listTree']>();

  beforeEach(() => {
    listTree.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        AuthoringStore,
        { provide: AuthoringDataService, useValue: { listTree } },
      ],
    });
  });

  async function seeded(): Promise<InstanceType<typeof AuthoringStore>> {
    listTree.mockImplementation((_projectId, read) =>
      Promise.resolve(read.nodeId ? addressed([child], 'more') : page([root])),
    );
    const store = TestBed.inject(AuthoringStore);
    await store.init('project', root.node_id);
    return store;
  }

  it('refreshes a loaded row in place and keeps a later continuation row', async () => {
    const store = await seeded();
    listTree.mockImplementation((_projectId, read) => {
      if (read.nodeId)
        return Promise.resolve(addressed([{ ...child, title: 'Renamed', version: 2 }], 'more'));
      if (read.parentId) return Promise.resolve(page([extra]));
      return Promise.resolve(page([root]));
    });
    await store.loadMoreChildren(root.node_id);
    expect(store.children()[root.node_id]?.items.map((node) => node.node_id)).toEqual([
      'child',
      'extra',
    ]);
    await store.retryTree();
    const items = store.children()[root.node_id]?.items ?? [];
    expect(items.map((node) => node.node_id)).toEqual(['child', 'extra']);
    expect(items[0]?.title).toBe('Renamed');
    expect(items[0]?.version).toBe(2);
  });

  it('aborts the in-flight append when a selection refresh starts', async () => {
    const store = await seeded();
    const branch = new Subject<AuthoringTreeResponse>();
    const selection = new Subject<AuthoringTreeResponse>();
    listTree.mockImplementation((_projectId, read) =>
      read.parentId
        ? firstValueFrom(branch)
        : read.nodeId
          ? firstValueFrom(selection)
          : Promise.resolve(page([root])),
    );
    const following = store.loadMoreChildren(root.node_id);
    const appendSignal = listTree.mock.calls.at(-1)?.[2];
    expect(appendSignal?.aborted).toBe(false);
    const refresh = store.retryTree();
    expect(appendSignal?.aborted).toBe(true);
    selection.next(addressed([extra]));
    branch.next(page([extra]));
    await Promise.all([refresh, following]);
    expect(store.children()[root.node_id]?.items).toEqual([child, extra]);
  });

  it('keeps the loaded rows and the selection when a selection refresh completes first', async () => {
    const store = await seeded();
    const selection = new Subject<AuthoringTreeResponse>();
    const branch = new Subject<AuthoringTreeResponse>();
    listTree.mockImplementation((_projectId, read) =>
      read.nodeId
        ? firstValueFrom(selection)
        : read.parentId
          ? firstValueFrom(branch)
          : Promise.resolve(page([root])),
    );
    const following = store.loadMoreChildren(root.node_id);
    const refresh = store.retryTree();
    selection.next(addressed([], null));
    await Promise.resolve();
    branch.next(page([extra]));
    await Promise.all([refresh, following]);
    expect(store.selectedNode()).toEqual(root);
    expect(store.children()[root.node_id]?.items).toEqual([child]);
  });

  it('merges the selection page into rows an append already loaded, never shrinking them', async () => {
    const store = await seeded();
    const selection = new Subject<AuthoringTreeResponse>();
    const branch = new Subject<AuthoringTreeResponse>();
    listTree.mockImplementation((_projectId, read) =>
      read.nodeId
        ? firstValueFrom(selection)
        : read.parentId
          ? firstValueFrom(branch)
          : Promise.resolve(page([root])),
    );
    const refresh = store.retryTree();
    const following = store.loadMoreChildren(root.node_id);
    branch.next(page([extra]));
    await following;
    selection.next(addressed([], null));
    await refresh;
    expect(store.children()[root.node_id]?.items).toEqual([child, extra]);
  });

  it('preserves loaded continuation pages when an expanded branch is selected', async () => {
    listTree.mockImplementation((_projectId, read) =>
      Promise.resolve(
        read.nodeId
          ? addressed([child], 'more')
          : read.cursor
            ? page([extra])
            : read.parentId
              ? page([child], 'more')
              : page([root]),
      ),
    );
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    await store.expand(root.node_id);
    await store.loadMoreChildren(root.node_id);
    await store.init('project', root.node_id);
    expect(store.children()[root.node_id]?.items).toEqual([child, extra]);
    // The selection read answers its own walk cursor, so a later continuation restarts there.
    expect(store.children()[root.node_id]?.nextCursor).toBe('more');
  });

  for (const failedRegion of ['roots', 'selection'] as const) {
    for (const failureFirst of [true, false]) {
      it(`keeps ${failedRegion} stale state when its failure completes ${failureFirst ? 'first' : 'last'}`, async () => {
        const store = await seeded();
        const roots = new Subject<AuthoringTreeResponse>();
        const selection = new Subject<AuthoringTreeResponse>();
        listTree.mockImplementation((_projectId, read) =>
          firstValueFrom(read.nodeId ? selection : roots),
        );
        const refresh = store.retryTree();
        const failure = () =>
          (failedRegion === 'roots' ? roots : selection).error(refusal('unavailable', 503));
        const success = () =>
          failedRegion === 'roots' ? selection.next(addressed([child])) : roots.next(page([root]));
        if (failureFirst) {
          failure();
          await Promise.resolve();
          success();
        } else {
          success();
          await Promise.resolve();
          failure();
        }
        await refresh;
        expect(store.rootsStale()).toBe(failedRegion === 'roots');
        expect(store.selectionStale()).toBe(failedRegion === 'selection');
        expect(store.stale()).toBe(true);
      });
    }
  }

  for (const deniedRegion of ['roots', 'selection'] as const) {
    for (const denialFirst of [true, false]) {
      for (const [kind, status] of [
        ['forbidden', 403],
        ['not_found', 404],
      ] as const) {
        it(`reports the ${kind} on ${deniedRegion} whichever read fails ${denialFirst ? 'first' : 'last'}`, async () => {
          const store = await seeded();
          const roots = new Subject<AuthoringTreeResponse>();
          const selection = new Subject<AuthoringTreeResponse>();
          listTree.mockImplementation((_projectId, read) =>
            firstValueFrom(read.nodeId ? selection : roots),
          );
          const refresh = store.retryTree();
          const deny = () =>
            (deniedRegion === 'roots' ? roots : selection).error(refusal(kind, status));
          const unavailable = () =>
            (deniedRegion === 'roots' ? selection : roots).error(refusal('unavailable', 503));
          if (denialFirst) {
            deny();
            await Promise.resolve();
            unavailable();
          } else {
            unavailable();
            await Promise.resolve();
            deny();
          }
          await refresh;
          expect(store.treeError()?.kind).toBe(kind);
        });
      }
    }
  }

  for (const parentId of [null, root.node_id] as const) {
    for (const answeredCursor of ['more', 'different'] as const) {
      const code = answeredCursor === 'more' ? 'cursor_repeated' : 'page_without_new_rows';
      it(`refuses a ${parentId === null ? 'roots' : 'child'} page that cannot advance (${code})`, async () => {
        listTree.mockImplementation((_projectId, read) =>
          Promise.resolve(read.cursor ? page([child], answeredCursor) : page([child], 'more')),
        );
        const store = TestBed.inject(AuthoringStore);
        await store.init('project');
        if (parentId !== null) await store.expand(parentId);
        await expect(store.loadMoreChildren(parentId)).resolves.toBe(false);
        const result = parentId === null ? store.roots() : store.children()[parentId];
        expect(result?.items).toEqual([child]);
        expect(result?.nextCursor).toBe('more');
        expect(result?.error).toEqual(expect.objectContaining({ kind: 'unexpected', code }));
      });
    }
  }
});
