import type { NodeDto } from '@evidgate/sdk';

import { ApiRequestError, isApiRequestError, type ApiError } from '../../core/http/api-error';
import { takeKeysetPage, type KeysetRefusals } from '../../shared/keyset-walk';
import type { AuthoringDataService } from './authoring-data.service';
import {
  EMPTY_NODE_PAGE,
  readyNodePage,
  selectionNodePage,
  type AuthoringNodePage,
  type AuthoringTreeState,
} from './authoring-tree-page';

/** A rejection that is not a typed API failure is a bug; the UI still gets one safe state. */
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message: 'The authoring request did not complete.',
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

function typedFailure(error: unknown): ApiError {
  return isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE;
}

/**
 * A continuation page that cannot advance the walk — the cursor it was asked for, or no row the
 * walk had not shown while promising another page — is refused typed instead of looping.
 */
const CURSOR_REFUSALS: KeysetRefusals = {
  cursorRepeated: {
    kind: 'unexpected',
    status: 0,
    code: 'cursor_repeated',
    message: 'The authoring page returned the cursor it was asked for.',
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  },
  pageWithoutNewRows: {
    kind: 'unexpected',
    status: 0,
    code: 'page_without_new_rows',
    message: 'The authoring page carried no node the tree had not shown yet.',
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  },
};

/**
 * A node read asked for the node and its path, so a page carrying neither cannot become a
 * successful selection: an empty path would claim the node sits at the root.
 */
const MISSING_PROJECTION: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'selection_projection_missing',
  message: 'The authoring node read did not carry the node and its path.',
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

interface ReadStream {
  sequence: number;
  controller: AbortController | null;
}

interface ReadToken {
  readonly generation: number;
  readonly sequence: number;
  readonly signal: AbortSignal;
}

interface ReadsRun {
  generation: number;
  readonly roots: ReadStream;
  readonly selection: ReadStream;
  readonly branches: Map<string, ReadStream>;
}

interface ReadsHost {
  readonly projectId: () => string | null;
  readonly roots: () => AuthoringNodePage;
  readonly children: () => Readonly<Record<string, AuthoringNodePage>>;
  readonly selectedNodeId: () => string | null;
  readonly selectedNode: () => NodeDto | null;
  readonly patch: (patch: Partial<AuthoringTreeState>) => void;
}

export interface TreeReads {
  reset(projectId: string): void;
  loadRoots(projectId: string): Promise<void>;
  loadSelection(projectId: string, nodeId: string): Promise<void>;
  loadChildren(projectId: string, parentId: string): Promise<void>;
  followRoots(): Promise<boolean>;
  followChildren(projectId: string, parentId: string): Promise<boolean>;
  clearSelection(): void;
  abortAll(): void;
}

/** Starts a stream's read: the previous controller is aborted, so only the newest can write. */
function begin(run: ReadsRun, stream: ReadStream): ReadToken {
  stream.controller?.abort();
  const controller = new AbortController();
  stream.controller = controller;
  stream.sequence += 1;
  return { generation: run.generation, sequence: stream.sequence, signal: controller.signal };
}

function live(run: ReadsRun, token: ReadToken, stream: ReadStream): boolean {
  return token.generation === run.generation && token.sequence === stream.sequence;
}

function branchOf(run: ReadsRun, parentId: string): ReadStream {
  const known = run.branches.get(parentId);
  if (known !== undefined) return known;
  const created: ReadStream = { sequence: 0, controller: null };
  run.branches.set(parentId, created);
  return created;
}

/**
 * Drops any read of one branch: a newer selection read owns that branch's rows now. The aborted
 * read never writes its own outcome, so its `loading` status is settled here or the spinner
 * outlives the request and blocks both retry paths.
 */
function abortBranch(run: ReadsRun, host: ReadsHost, parentId: string): void {
  const stream = run.branches.get(parentId);
  if (stream === undefined) return;
  stream.controller?.abort();
  stream.controller = null;
  stream.sequence += 1;
  const page = host.children()[parentId];
  if (page === undefined || page.status !== 'loading') return;
  const children = { ...host.children() };
  if (page.items.length === 0) delete children[parentId];
  else children[parentId] = { ...page, status: 'ready', error: null };
  host.patch({ children });
}

async function loadRoots(
  run: ReadsRun,
  host: ReadsHost,
  data: AuthoringDataService,
  projectId: string,
): Promise<void> {
  const token = begin(run, run.roots);
  const previous = host.roots();
  host.patch({ roots: { ...host.roots(), status: 'loading', error: null } });
  try {
    const page = await data.listTree(projectId, {}, token.signal);
    if (!live(run, token, run.roots)) return;
    host.patch({
      roots: readyNodePage(page.items, page.next_cursor),
      rootsStale: false,
    });
  } catch (error) {
    if (!live(run, token, run.roots)) return;
    const kept = host.roots();
    host.patch({
      roots: previous.status === 'capped'
        ? { ...previous, error: typedFailure(error) }
        : { ...kept, status: 'error', error: typedFailure(error) },
      rootsStale: kept.items.length > 0,
    });
  }
}

async function followRoots(
  run: ReadsRun,
  host: ReadsHost,
  data: AuthoringDataService,
): Promise<boolean> {
  const projectId = host.projectId();
  const page = host.roots();
  const cursor = page.nextCursor;
  if (projectId === null || cursor === null || page.status !== 'ready') return false;
  const token = begin(run, run.roots);
  host.patch({ roots: { ...page, status: 'loading', error: null } });
  try {
    const next = await data.listTree(projectId, { cursor }, token.signal);
    if (!live(run, token, run.roots)) return false;
    const step = takeKeysetPage({
      cursor,
      items: next.items,
      nextCursor: next.next_cursor,
      seen: new Set(page.items.map((node) => node.node_id)),
      keyOf: (node: NodeDto) => node.node_id,
      refusals: CURSOR_REFUSALS,
    });
    if (step.outcome === 'refused') {
      host.patch({ roots: { ...page, status: 'ready', error: step.error, nextCursor: null } });
      return false;
    }
    host.patch({
      roots: readyNodePage([...page.items, ...step.fresh], step.nextCursor),
    });
    return true;
  } catch (error) {
    if (!live(run, token, run.roots)) return false;
    host.patch({ roots: { ...page, status: 'ready', error: typedFailure(error) } });
    return false;
  }
}

async function loadSelection(
  run: ReadsRun,
  host: ReadsHost,
  data: AuthoringDataService,
  projectId: string,
  nodeId: string,
): Promise<void> {
  const token = begin(run, run.selection);
  const refreshing = host.selectedNodeId() === nodeId;
  abortBranch(run, host, nodeId);
  host.patch({
    selectedNodeId: nodeId,
    ...(refreshing ? {} : { selectedNode: null, path: [] }),
    selectionStatus: 'loading',
    selectionError: null,
  });
  try {
    const page = await data.listTree(projectId, { nodeId }, token.signal);
    if (!live(run, token, run.selection)) return;
    const node = page.node ?? null;
    const path = page.path ?? null;
    if (node === null || path === null) throw new ApiRequestError(MISSING_PROJECTION);
    host.patch({
      selectedNode: node,
      path,
      selectionStatus: 'ready',
      selectionError: null,
      selectionStale: false,
      children: {
        ...host.children(),
        [nodeId]: selectionNodePage(host.children()[nodeId], page.items, page.next_cursor),
      },
    });
  } catch (error) {
    if (!live(run, token, run.selection)) return;
    host.patch({
      selectionStatus: 'error',
      selectionError: typedFailure(error),
      selectionStale: refreshing && host.selectedNode() !== null,
    });
  }
}

async function loadChildren(
  run: ReadsRun,
  host: ReadsHost,
  data: AuthoringDataService,
  projectId: string,
  parentId: string,
): Promise<void> {
  const stream = branchOf(run, parentId);
  const token = begin(run, stream);
  const previous = host.children()[parentId] ?? EMPTY_NODE_PAGE;
  host.patch({
    children: { ...host.children(), [parentId]: { ...previous, status: 'loading', error: null } },
  });
  try {
    const page = await data.listTree(projectId, { parentId }, token.signal);
    if (!live(run, token, stream)) return;
    host.patch({
      children: {
        ...host.children(),
        [parentId]: readyNodePage(page.items, page.next_cursor),
      },
    });
  } catch (error) {
    if (!live(run, token, stream)) return;
    const kept = host.children()[parentId] ?? EMPTY_NODE_PAGE;
    host.patch({
      children: {
        ...host.children(),
        [parentId]: { ...kept, status: 'error', error: typedFailure(error) },
      },
    });
  }
}

/**
 * True when a newer write replaced the page a continuation read its cursor from: that
 * continuation's rows, cursor and failure all describe a walk the branch no longer follows.
 */
function replaced(host: ReadsHost, parentId: string, base: AuthoringNodePage): boolean {
  const current = host.children()[parentId] ?? EMPTY_NODE_PAGE;
  return current.items !== base.items || current.nextCursor !== base.nextCursor;
}

async function followChildren(
  run: ReadsRun,
  host: ReadsHost,
  data: AuthoringDataService,
  projectId: string,
  parentId: string,
): Promise<boolean> {
  const page = host.children()[parentId] ?? EMPTY_NODE_PAGE;
  const cursor = page.nextCursor;
  if (cursor === null || page.status !== 'ready') return false;
  const stream = branchOf(run, parentId);
  const token = begin(run, stream);
  host.patch({
    children: { ...host.children(), [parentId]: { ...page, status: 'loading', error: null } },
  });
  try {
    const next = await data.listTree(projectId, { parentId, cursor }, token.signal);
    if (!live(run, token, stream)) return false;
    if (replaced(host, parentId, page)) return false;
    const step = takeKeysetPage({
      cursor,
      items: next.items,
      nextCursor: next.next_cursor,
      seen: new Set(page.items.map((node) => node.node_id)),
      keyOf: (node: NodeDto) => node.node_id,
      refusals: CURSOR_REFUSALS,
    });
    if (step.outcome === 'refused') {
      host.patch({
        children: {
          ...host.children(),
          [parentId]: { ...page, status: 'ready', error: step.error, nextCursor: null },
        },
      });
      return false;
    }
    host.patch({
      children: {
        ...host.children(),
        [parentId]: readyNodePage([...page.items, ...step.fresh], step.nextCursor),
      },
    });
    return true;
  } catch (error) {
    if (!live(run, token, stream)) return false;
    if (replaced(host, parentId, page)) return false;
    host.patch({
      children: {
        ...host.children(),
        [parentId]: { ...page, status: 'ready', error: typedFailure(error) },
      },
    });
    return false;
  }
}

function clearSelection(run: ReadsRun, host: ReadsHost): void {
  run.selection.controller?.abort();
  run.selection.controller = null;
  run.selection.sequence += 1;
  host.patch({
    selectedNodeId: null,
    selectedNode: null,
    path: [],
    selectionStatus: 'idle',
    selectionError: null,
    selectionStale: false,
  });
}

function abortAll(run: ReadsRun): void {
  run.generation += 1;
  for (const stream of [run.roots, run.selection, ...run.branches.values()]) {
    stream.controller?.abort();
    stream.controller = null;
  }
  run.branches.clear();
}

function reset(run: ReadsRun, host: ReadsHost, projectId: string): void {
  abortAll(run);
  host.patch({
    projectId,
    roots: EMPTY_NODE_PAGE,
    children: {},
    selectedNodeId: null,
    selectedNode: null,
    path: [],
    selectionStatus: 'idle',
    selectionError: null,
    rootsStale: false,
    selectionStale: false,
  });
}

/**
 * The tree reads behind the store: one stream per page (the roots, the addressed node, one per
 * expanded branch). A stream aborts its previous controller and keeps a sequence, so a response
 * that outlives a newer request — or a project switch — never writes state.
 */
export function createTreeReads(host: ReadsHost, data: AuthoringDataService): TreeReads {
  const run: ReadsRun = {
    generation: 0,
    roots: { sequence: 0, controller: null },
    selection: { sequence: 0, controller: null },
    branches: new Map<string, ReadStream>(),
  };
  return {
    reset: (projectId) => reset(run, host, projectId),
    loadRoots: (projectId) => loadRoots(run, host, data, projectId),
    loadSelection: (projectId, nodeId) => loadSelection(run, host, data, projectId, nodeId),
    loadChildren: (projectId, parentId) => loadChildren(run, host, data, projectId, parentId),
    followRoots: () => followRoots(run, host, data),
    followChildren: (projectId, parentId) => followChildren(run, host, data, projectId, parentId),
    clearSelection: () => clearSelection(run, host),
    abortAll: () => abortAll(run),
  };
}
