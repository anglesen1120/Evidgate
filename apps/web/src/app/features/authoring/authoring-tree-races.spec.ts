import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import type { AuthoringTreeResponse, NodeDto } from '@evidgate/sdk';
import { BehaviorSubject } from 'rxjs';
import { ApiRequestError } from '../../core/http/api-error';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringPage } from './authoring.page';
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
const child: NodeDto = { ...root, node_id: 'child', parent_id: root.node_id };
const extra: NodeDto = { ...child, node_id: 'extra' };
const page = (items: NodeDto[], cursor: string | null = null): AuthoringTreeResponse => ({
  items,
  next_cursor: cursor,
});
const addressed = (items: NodeDto[], cursor: string | null = null): AuthoringTreeResponse => ({
  ...page(items, cursor),
  node: root,
  path: [],
});
const unavailable = () =>
  new ApiRequestError({
    kind: 'unavailable',
    status: 503,
    code: 'unavailable',
    message: 'Selection unavailable',
    requestId: null,
    retryable: true,
    retryAfterSeconds: null,
    details: [],
  });

// The executor form is how the resolver escapes: this project compiles against the ES2022 lib,
// which has no `Promise.withResolvers`.
function deferred<T>() {
  let resolve: (value: T) => void = () => {
    throw new Error('Deferred promise not initialized');
  };
  let reject: (reason: unknown) => void = () => {
    throw new Error('Deferred promise not initialized');
  };
  const promise = new Promise<T>((accept, refuse) => {
    resolve = accept;
    reject = refuse;
  });
  return { promise, resolve, reject };
}

class AnnouncementHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-page';
  readonly announcement = this.locatorFor('[data-authoring-announcement]');
}

describe('authoring tree interrupted reads', () => {
  provideLayoutTestStubs();
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
            listManualCases: vi.fn().mockResolvedValue({ items: [], next_cursor: null }),
            listChecklistItems: vi.fn().mockResolvedValue({ items: [], next_cursor: null }),
          },
        },
      ],
    });
  });

  async function seeded() {
    listTree.mockImplementation((_projectId, read) =>
      Promise.resolve(read.nodeId ? addressed([child], 'more') : page([root])),
    );
    const store = TestBed.inject(AuthoringStore);
    await store.init('project', root.node_id);
    return store;
  }

  it('drops a late continuation superseded by a selection refresh without reverting refreshed rows', async () => {
    const store = await seeded();
    const selection = deferred<AuthoringTreeResponse>();
    const continuation = deferred<AuthoringTreeResponse>();
    listTree.mockImplementation((_projectId, read) =>
      read.nodeId
        ? selection.promise
        : read.parentId
          ? continuation.promise
          : Promise.resolve(page([root])),
    );
    const refresh = store.retryTree();
    const following = store.loadMoreChildren(root.node_id);
    const refreshed = { ...child, title: 'Refreshed child', version: 2 };
    selection.resolve(addressed([refreshed], 'fresh-more'));
    await refresh;
    continuation.resolve(page([extra]));
    expect(await following).toBe(false);
    expect(store.children()[root.node_id]?.items).toEqual([refreshed]);
    expect(store.selectedNode()).toEqual(root);
  });

  it('settles an aborted empty child read so expansion can retry after selection fails', async () => {
    listTree.mockResolvedValue(page([root]));
    const store = TestBed.inject(AuthoringStore);
    await store.init('project');
    const children = deferred<AuthoringTreeResponse>();
    const selection = deferred<AuthoringTreeResponse>();
    listTree.mockImplementation((_projectId, read) =>
      read.nodeId ? selection.promise : children.promise,
    );
    const expansion = store.expand(root.node_id);
    const selected = store.init('project', root.node_id);
    selection.reject(unavailable());
    await selected;
    children.resolve(page([extra]));
    await expansion;
    expect(store.children()[root.node_id]?.status).not.toBe('loading');
    listTree.mockResolvedValue(page([child], 'more'));
    await store.expand(root.node_id);
    expect(store.children()[root.node_id]?.items).toEqual([child]);
    listTree.mockResolvedValue(page([extra]));
    expect(await store.loadMoreChildren(root.node_id)).toBe(true);
    expect(store.children()[root.node_id]?.items).toEqual([child, extra]);
  });

  it('keeps a longer retained walk cursor on selection refresh', async () => {
    const store = await seeded();
    listTree.mockResolvedValueOnce(page([extra], 'third-page'));
    expect(await store.loadMoreChildren(root.node_id)).toBe(true);
    listTree.mockImplementation((_projectId, read) =>
      read.nodeId ? Promise.resolve(addressed([child], 'more')) : Promise.resolve(page([root])),
    );
    await store.retryTree();
    expect(store.children()[root.node_id]?.nextCursor).toBe('third-page');
    const newest = { ...child, node_id: 'newest' };
    listTree.mockResolvedValueOnce(page([newest]));
    expect(await store.loadMoreChildren(root.node_id)).toBe(true);
    expect(listTree.mock.lastCall?.[1].cursor).toBe('third-page');
    expect(store.children()[root.node_id]?.items).toEqual([child, extra, newest]);
  });

  it('uses the fresh selection cursor to load new children after an exhausted branch refreshes', async () => {
    const store = await seeded();
    listTree.mockResolvedValue(page([extra]));
    await store.loadMoreChildren(root.node_id);
    expect(store.children()[root.node_id]?.nextCursor).toBeNull();
    const selection = deferred<AuthoringTreeResponse>();
    listTree.mockImplementation((_projectId, read) =>
      read.nodeId ? selection.promise : Promise.resolve(page([root])),
    );
    const refresh = store.retryTree();
    selection.resolve(addressed([child], 'new-children'));
    await refresh;
    expect(store.children()[root.node_id]?.nextCursor).toBe('new-children');
    const newest = { ...child, node_id: 'newest' };
    listTree.mockResolvedValue(page([newest]));
    expect(await store.loadMoreChildren(root.node_id)).toBe(true);
    expect(store.children()[root.node_id]?.items).toEqual([child, extra, newest]);
  });

  it('announces the selected node failure rather than claiming all project roots were selected', async () => {
    const selection = deferred<AuthoringTreeResponse>();
    const started = deferred<void>();
    listTree.mockImplementation((_projectId, read) => {
      if (!read.nodeId) return Promise.resolve(page([root]));
      started.resolve();
      return selection.promise;
    });
    const params = new BehaviorSubject(convertToParamMap({ projectId: 'project' }));
    const query = new BehaviorSubject(convertToParamMap({ node_id: root.node_id }));
    TestBed.configureTestingModule({
      imports: [AuthoringPage],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            pathFromRoot: [{ paramMap: params }],
            queryParamMap: query,
            snapshot: { data: { pageHeadingLevel: 1 } },
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(AuthoringPage);
    fixture.detectChanges();
    await started.promise;
    selection.reject(unavailable());
    await fixture.whenStable();
    fixture.detectChanges();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, AnnouncementHarness);
    const announcement = await (await harness.announcement()).text();
    expect(announcement).toBe(`Could not load selected scope ${root.node_id}`);
    expect(announcement).not.toContain('all project roots');
  });
});
