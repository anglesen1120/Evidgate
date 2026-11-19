import type { AuthoringNodePathEntry, NodeDto } from '@evidgate/sdk';

import { ApiRequestError, isApiRequestError, type ApiError } from '../../core/http/api-error';
import type { AuthoringDataService } from './authoring-data.service';

/** The read state of one page of the tree, or of the addressed node's own read. */
export type AuthoringPageStatus = 'idle' | 'loading' | 'ready' | 'error';

/**
 * One page of nodes keyed by the parent it was read for. `error` is the last read this page
 * failed with: under `status: 'error'` it is the page's own failure, and under `status:
 * 'ready'` it is a follow-up page that failed while the rows above it stay usable.
 */
export interface AuthoringNodePage {
  readonly items: readonly NodeDto[];
  readonly nextCursor: string | null;
  readonly status: AuthoringPageStatus;
  readonly error: ApiError | null;
}

export interface AuthoringTreeState {
  readonly projectId: string | null;
  /** The roots page; the hierarchy every expanded branch hangs off. */
  readonly roots: AuthoringNodePage;
  /** Loaded branches by parent node id; a missing entry is a branch nobody expanded yet. */
  readonly children: Readonly<Record<string, AuthoringNodePage>>;
  readonly selectedNodeId: string | null;
  /** The addressed node as the server returned it, with its counts. */
  readonly selectedNode: NodeDto | null;
  /** The addressed node's ancestors, root first, exactly as the server returned them. */
  readonly path: readonly AuthoringNodePathEntry[];
  readonly selectionStatus: AuthoringPageStatus;
  readonly selectionError: ApiError | null;
  /** True while a failed refresh is still showing the rows an earlier read loaded. */
  readonly stale: boolean;
}

/** A branch nobody has read yet: one shared object, so a missing entry allocates nothing. */
export const EMPTY_NODE_PAGE: AuthoringNodePage = {
  items: [],
  nextCursor: null,
  status: 'idle',
  error: null,
};

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

/** Appends the rows a page has not shown yet, keeping page order and the first copy of a node. */
function mergeNodes(loaded: readonly NodeDto[], fresh: readonly NodeDto[]): NodeDto[] {
  const seen = new Set(loaded.map((node) => node.node_id));
  const merged = [...loaded];
  for (const node of fresh) {
    if (seen.has(node.node_id)) continue;
    seen.add(node.node_id);
    merged.push(node);
  }
  return merged;
}

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

async function loadRoots(
  run: ReadsRun,
  host: ReadsHost,
  data: AuthoringDataService,
  projectId: string,
): Promise<void> {
  const token = begin(run, run.roots);
  host.patch({ roots: { ...host.roots(), status: 'loading', error: null } });
  try {
    const page = await data.listTree(projectId, {}, token.signal);
    if (!live(run, token, run.roots)) return;
    host.patch({
      roots: { items: page.items, nextCursor: page.next_cursor, status: 'ready', error: null },
      stale: false,
    });
  } catch (error) {
    if (!live(run, token, run.roots)) return;
    const kept = host.roots();
    host.patch({
      roots: { ...kept, status: 'error', error: typedFailure(error) },
      stale: kept.items.length > 0,
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
    host.patch({
      roots: {
        items: mergeNodes(host.roots().items, next.items),
        nextCursor: next.next_cursor,
        status: 'ready',
        error: null,
      },
    });
    return true;
  } catch (error) {
    if (!live(run, token, run.roots)) return false;
    host.patch({ roots: { ...host.roots(), status: 'ready', error: typedFailure(error) } });
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
      stale: false,
      children: {
        ...host.children(),
        [nodeId]: {
          items: page.items,
          nextCursor: page.next_cursor,
          status: 'ready',
          error: null,
        },
      },
    });
  } catch (error) {
    if (!live(run, token, run.selection)) return;
    host.patch({
      selectionStatus: 'error',
      selectionError: typedFailure(error),
      stale: refreshing && host.selectedNode() !== null,
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
        [parentId]: {
          items: page.items,
          nextCursor: page.next_cursor,
          status: 'ready',
          error: null,
        },
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
    const current = host.children()[parentId] ?? EMPTY_NODE_PAGE;
    host.patch({
      children: {
        ...host.children(),
        [parentId]: {
          items: mergeNodes(current.items, next.items),
          nextCursor: next.next_cursor,
          status: 'ready',
          error: null,
        },
      },
    });
    return true;
  } catch (error) {
    if (!live(run, token, stream)) return false;
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
    stale: false,
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
