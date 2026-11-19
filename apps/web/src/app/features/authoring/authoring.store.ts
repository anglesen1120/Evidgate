import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';

import type { ApiError } from '../../core/http/api-error';
import { AuthoringDataService } from './authoring-data.service';
import { createTreeReads } from './authoring-tree-reads';
import {
  EMPTY_NODE_PAGE,
  type AuthoringPageStatus,
  type AuthoringTreeState,
} from './authoring-tree-page';

export type { AuthoringNodePage, AuthoringPageStatus } from './authoring-tree-page';

/**
 * The authoring tree's page state. Provided by the surface that renders it, never at the root:
 * the project and the addressed node arrive from the route, so the store owns one project's
 * tree only. The URL owns the selected id; {@link selectNode} takes it and answers it back for
 * the caller to mirror, so the store never writes a route.
 */
export const AuthoringStore = signalStore(
  withState<AuthoringTreeState>({
    projectId: null,
    roots: EMPTY_NODE_PAGE,
    children: {},
    selectedNodeId: null,
    selectedNode: null,
    path: [],
    selectionStatus: 'idle',
    selectionError: null,
    rootsStale: false,
    selectionStale: false,
  }),
  withProps((store, data = inject(AuthoringDataService)) => {
    const reads = createTreeReads(
      {
        projectId: () => store.projectId(),
        roots: () => store.roots(),
        children: () => store.children(),
        selectedNodeId: () => store.selectedNodeId(),
        selectedNode: () => store.selectedNode(),
        patch: (patch) => patchState(store, patch),
      },
      data,
    );
    return { _reads: reads };
  }),
  withHooks({
    onDestroy(store) {
      store._reads.abortAll();
    },
  }),
  withComputed((store) => ({
    /** The tree region's own state: independent of the case table, which owns its own. */
    treeStatus: computed<AuthoringPageStatus>(() => {
      const rootsStatus = store.roots().status;
      const selectionStatus = store.selectionStatus();
      if (rootsStatus === 'loading' || selectionStatus === 'loading') return 'loading';
      if (
        rootsStatus === 'error' ||
        selectionStatus === 'error' ||
        (rootsStatus === 'capped' && store.roots().error !== null)
      )
        return 'error';
      return rootsStatus === 'ready' || rootsStatus === 'capped' ? 'ready' : 'idle';
    }),
    /** Either region keeps rows a failed refresh could not replace. */
    stale: computed(() => store.rootsStale() || store.selectionStale()),
    treeError: computed<ApiError | null>(() => {
      const rootsError = store.roots().error;
      const selectionError = store.selectionError();
      const denial = [rootsError, selectionError].find(
        (error): error is ApiError =>
          error !== null && (error.kind === 'forbidden' || error.kind === 'not_found'),
      );
      return denial ?? rootsError ?? selectionError;
    }),
  })),
  withMethods((store) => ({
    /**
     * Applies the route's project and addressed node, resolving when the reads it started
     * settle. The roots load once per project; the addressed node is read when the route names
     * one, and a repeat of the same settled id starts nothing, so a query-string change does not
     * re-read the tree.
     */
    init(projectId: string, selectedNodeId: string | null = null): Promise<void> {
      const started: Promise<unknown>[] = [];
      if (store.projectId() !== projectId) store._reads.reset(projectId);
      if (store.roots().status === 'idle' || store.roots().status === 'error') {
        started.push(store._reads.loadRoots(projectId));
      }
      if (selectedNodeId === null) {
        if (store.selectedNodeId() !== null) store._reads.clearSelection();
        return Promise.all(started).then(() => undefined);
      }
      const settled =
        store.selectedNodeId() === selectedNodeId && store.selectionStatus() === 'ready';
      const pending =
        store.selectedNodeId() === selectedNodeId && store.selectionStatus() === 'loading';
      if (!settled && !pending) {
        started.push(store._reads.loadSelection(projectId, selectedNodeId));
      }
      return Promise.all(started).then(() => undefined);
    },
    /**
     * Selects a node and reads it with its root-first path and first children page; returns the
     * id so the caller mirrors it in the route. `null` clears the selection without a request.
     */
    selectNode(nodeId: string | null): string | null {
      if (nodeId === null) {
        store._reads.clearSelection();
        return null;
      }
      const projectId = store.projectId();
      const settled = store.selectedNodeId() === nodeId && store.selectionStatus() === 'ready';
      const pending = store.selectedNodeId() === nodeId && store.selectionStatus() === 'loading';
      if (projectId !== null && !settled && !pending) {
        void store._reads.loadSelection(projectId, nodeId);
      }
      return nodeId;
    },
    /** Reads a branch's children page on explicit expansion only; never recursively. */
    expand(parentId: string): Promise<void> {
      const projectId = store.projectId();
      if (projectId === null) return Promise.resolve();
      const page = store.children()[parentId];
      if (page !== undefined && page.status !== 'error') return Promise.resolve();
      return store._reads.loadChildren(projectId, parentId);
    },
    /**
     * Follows one page of `parentId`'s children, or of the roots when it is `null`. Resolves
     * `true` only when the rows in state came from that page.
     */
    loadMoreChildren(parentId: string | null): Promise<boolean> {
      const projectId = store.projectId();
      if (projectId === null) return Promise.resolve(false);
      return parentId === null
        ? store._reads.followRoots()
        : store._reads.followChildren(projectId, parentId);
    },
    /** Re-reads the tree region, keeping the rows an earlier read loaded when it fails. */
    retryTree(): Promise<void> {
      const projectId = store.projectId();
      if (projectId === null) return Promise.resolve();
      const nodeId = store.selectedNodeId();
      const reads = [store._reads.loadRoots(projectId)];
      if (nodeId !== null) reads.push(store._reads.loadSelection(projectId, nodeId));
      return Promise.all(reads).then(() => undefined);
    },
  })),
);
