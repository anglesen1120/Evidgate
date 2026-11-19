import { manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import type { AuthoringNodePathEntry, NodeDto } from '@evidgate/sdk';
import { BehaviorSubject } from 'rxjs';
import type { ApiError } from '../../core/http/api-error';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { AuthoringPage } from './authoring.page';
import { AuthoringStore, type AuthoringNodePage } from './authoring.store';
import { AuthoringPageHarness } from './authoring-tree.testing';

const root: NodeDto = {
  node_id: '018f0000-0000-7000-8000-000000000101',
  parent_id: null,
  kind: 'module',
  title: 'Module',
  position: 0,
  version: 1,
  child_count: 1,
  item_count: 4,
};
const selected: NodeDto = {
  ...root,
  node_id: '018f0000-0000-7000-8000-000000000102',
  parent_id: root.node_id,
  kind: 'feature',
  title: 'Selected feature',
  child_count: 0,
};
const failure: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'unavailable',
  message: 'Fixture transport failure',
  requestId: 'request',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
};

describe('AuthoringPage', () => {
  provideLayoutTestStubs();
  const query = new BehaviorSubject(convertToParamMap({}));
  const params = new BehaviorSubject(convertToParamMap({ projectId: 'project' }));
  const status = signal<'idle' | 'loading' | 'ready' | 'error'>('ready');
  const error = signal<ApiError | null>(null);
  const roots = signal<AuthoringNodePage>({
    items: [root],
    nextCursor: null,
    status: 'ready',
    error: null,
  });
  const node = signal<NodeDto | null>(selected);
  const selectedId = signal<string | null>(selected.node_id);
  const path = signal<AuthoringNodePathEntry[]>([root]);
  const stale = signal(false);
  const selectionStatus = signal<'idle' | 'loading' | 'ready' | 'error'>('ready');
  const init = vi.fn().mockResolvedValue(undefined);
  const retryTree = vi.fn().mockResolvedValue(undefined);
  const selectNode = vi.fn((id: string | null) => id);
  const store = {
    projectId: signal('project'),
    roots,
    children: signal({}),
    selectedNode: node,
    selectedNodeId: selectedId,
    path,
    selectionStatus,
    treeStatus: status,
    treeError: error,
    stale,
    init,
    retryTree,
    selectNode,
    expand: vi.fn(),
    loadMoreChildren: vi.fn().mockResolvedValue(true),
  };

  beforeEach(() => {
    query.next(convertToParamMap({}));
    params.next(convertToParamMap({ projectId: 'project' }));
    status.set('ready');
    error.set(null);
    stale.set(false);
    selectionStatus.set('ready');
    roots.set({ items: [root], nextCursor: null, status: 'ready', error: null });
    node.set(selected);
    selectedId.set(selected.node_id);
    path.set([root]);
    init.mockClear();
    retryTree.mockClear();
    selectNode.mockClear();
    TestBed.configureTestingModule({
      imports: [AuthoringPage],
      providers: [
        provideZonelessChangeDetection(),
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
    TestBed.overrideComponent(AuthoringPage, {
      set: { providers: [{ provide: AuthoringStore, useValue: store }] },
    });
  });

  async function render() {
    const fixture = TestBed.createComponent(AuthoringPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      AuthoringPageHarness,
    );
    return { fixture, harness };
  }

  function host(fixture: ComponentFixture<AuthoringPage>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('reads the heading level from route data and keeps the table a separate neutral region', async () => {
    const { harness } = await render();
    expect(await (await harness.heading()).text()).toBe('Feature tree');
    expect(await (await harness.table()).text()).toContain('Case table');
    expect(await (await harness.table()).getAttribute('aria-busy')).not.toBe('true');
    expect(await harness.path()).toEqual(['Project roots', 'Module', 'Selected feature']);
    expect(await (await harness.selectedId())?.text()).toContain(selected.node_id);
  });

  it('restores the addressed node on reload and reacts to browser query and project changes', async () => {
    query.next(convertToParamMap({ node_id: selected.node_id, q: 'untouched' }));
    const { fixture } = await render();
    expect(init).toHaveBeenLastCalledWith('project', selected.node_id);
    query.next(convertToParamMap({ node_id: root.node_id }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(init).toHaveBeenLastCalledWith('project', root.node_id);
    query.next(convertToParamMap({}));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(init).toHaveBeenLastCalledWith('project', null);
    params.next(convertToParamMap({ projectId: 'next-project' }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(init).toHaveBeenLastCalledWith('next-project', null);
  });

  it('uses the router to mirror only the explicit scope selection and reset its cursor', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const { harness } = await render();
    const tree = await harness.tree();
    if (!tree) throw new Error('No hierarchy');
    await (await tree.scope(root.node_id)).click();
    expect(selectNode).toHaveBeenCalledWith(root.node_id);
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParamsHandling: 'merge',
        queryParams: { node_id: root.node_id, cursor: null },
      }),
    );
  });

  it('shows loading in the hierarchy only and calls the tree retry method', async () => {
    status.set('loading');
    roots.set({ items: [], nextCursor: null, status: 'loading', error: null });
    const { fixture, harness } = await render();
    await manualChangeDetection(async () => {
      expect(await harness.loading()).not.toBeNull();
      expect(await (await harness.table()).text()).toContain('Case table');
    });
    status.set('error');
    error.set(failure);
    fixture.detectChanges();
    await fixture.whenStable();
    await (await harness.refresh()).click();
    expect(retryTree).toHaveBeenCalledOnce();
  });

  it('distinguishes confirmed empty, forbidden and not-found and removes denied identities', async () => {
    roots.set({ items: [], nextCursor: null, status: 'ready', error: null });
    node.set(null);
    selectedId.set(null);
    path.set([]);
    const { fixture, harness } = await render();
    expect(await harness.empty()).not.toBeNull();
    status.set('error');
    error.set({ ...failure, kind: 'forbidden', status: 403, retryable: false });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await harness.forbidden()).not.toBeNull();
    expect(await harness.tree()).toBeNull();
    expect(await harness.selectedId()).toBeNull();
    error.set({ ...failure, kind: 'not_found', status: 404, retryable: false });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await harness.notFound()).not.toBeNull();
  });

  it('retains previously loaded nodes and labels the tree stale after failed refresh', async () => {
    const { fixture, harness } = await render();
    status.set('ready');
    error.set(failure);
    stale.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await harness.stale()).not.toBeNull();
    expect(await harness.tree()).not.toBeNull();
    expect(await harness.error()).not.toBeNull();
    expect(await (await harness.table()).text()).toContain('Case table');
  });

  it('uses a section heading when the containing route already owns the page heading', async () => {
    TestBed.inject(ActivatedRoute).snapshot.data['pageHeadingLevel'] = 2;
    const { harness } = await render();
    expect(await (await harness.heading()).getProperty('tagName')).toBe('H2');
  });

  it('hides retained paths, selected identities and roots after a forbidden read', async () => {
    error.set({ ...failure, kind: 'forbidden', status: 403, retryable: false });
    status.set('error');
    const { harness } = await render();
    expect(await harness.forbidden()).not.toBeNull();
    expect(await harness.tree()).toBeNull();
    expect(await harness.selectedId()).toBeNull();
    expect(await harness.path()).toEqual([]);
    expect(await harness.text()).not.toContain(selected.title);
  });

  it('does not declare the hierarchy empty while a root continuation is still available', async () => {
    roots.set({ items: [], nextCursor: 'following-roots', status: 'ready', error: null });
    node.set(null);
    selectedId.set(null);
    path.set([]);
    const { harness } = await render();
    expect(await harness.empty()).toBeNull();
    const tree = await harness.tree();
    if (!tree) throw new Error('The root continuation must remain reachable.');
    await (await tree.more('roots')).click();
    expect(store.loadMoreChildren).toHaveBeenCalledWith(null);
  });

  it('announces one completed scope change politely outside the busy region', async () => {
    const { fixture } = await render();
    const regions = host(fixture).querySelectorAll('[data-authoring-announcement]');
    expect(regions).toHaveLength(1);
    expect(host(fixture).querySelectorAll('app-authoring-tree-pane [aria-live]')).toHaveLength(0);
    const region = regions[0];
    if (!(region instanceof HTMLElement)) throw new Error('No live region is rendered.');
    expect(region.getAttribute('aria-live')).toBe('polite');
    expect(region.getAttribute('role')).toBe('status');
    expect(region.closest('[aria-busy]')).toBeNull();
    expect(region.textContent).toContain(selected.title);
    expect(host(fixture).querySelector('app-authoring-tree-pane [aria-live]')).toBeNull();

    node.set({ ...selected, title: 'Renamed scope' });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(region.textContent).toContain('Renamed scope');
    expect(region.textContent).not.toContain(selected.title);
  });

  it('announces the roots scope when the selection is cleared', async () => {
    node.set(null);
    selectedId.set(null);
    path.set([]);
    selectionStatus.set('idle');
    const { fixture } = await render();
    const region = host(fixture).querySelector('[data-authoring-announcement]');
    if (!(region instanceof HTMLElement)) throw new Error('No live region is rendered.');
    expect(region.textContent).toContain('Scope set to all project roots');
  });
});
