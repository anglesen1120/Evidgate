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
import type {
  AuthoringChangeListItem,
  AuthoringChangeV1,
  ChecklistItemDto,
  ListChecklistItemsData,
} from '@evidgate/sdk';

import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import type { ApiError } from '../../../core/http/api-error';
import { createRetryAfterWait } from '../../../core/http/retry';
import { SessionStore } from '../../../core/session/session.store';
import { AUTHORING_READ, AUTHORING_WRITE } from '../import-export/import-export.copy';
import { createPendingApply } from './pending-changes.apply';
import {
  MAX_APPLY_SELECTION,
  PENDING_COPY,
  pendingPreview,
  rowBlockedReason,
  type PendingPreview,
} from './pending-changes.copy';
import { applyBlockedReason as blockedApplyReason, discardRefused } from './pending-changes.guards';
import {
  createPendingBookkeeping,
  isCancellation,
  pendingDocument,
  pendingRequestFailure as fail,
  signatureOf,
  unexpectedFailure,
} from './pending-changes.intent';
import { createPendingReads } from './pending-changes.reads';
import { pendingInitialState } from './pending-changes.state';

export type { PendingConflict } from './pending-changes.state';

export const PendingChangesStore = signalStore(
  withState(pendingInitialState),
  withProps(() => ({
    _data: inject(AuthoringPendingDataService),
    _session: inject(SessionStore),
    _wait: createRetryAfterWait(),
  })),
  withProps(() => {
    const book = createPendingBookkeeping();
    return { _keys: book.keys, _run: book.run };
  }),
  withComputed((store) => ({
    selectedIdSet: computed(() => new Set(store.selectedIds())),
  })),
  withComputed((store) => ({
    canRead: computed(() => store._session.has(AUTHORING_READ)),
    canWrite: computed(() => store._session.has(AUTHORING_WRITE)),
    empty: computed(() => store.queueStatus() === 'ready' && store.rows().length === 0),
    notFound: computed(
      () => store.queueStatus() === 'error' && store.queueError()?.kind === 'not_found',
    ),
    forbidden: computed(
      () => store.queueStatus() === 'error' && store.queueError()?.kind === 'forbidden',
    ),
    selectedRows: computed(() =>
      store.rows().filter((row) => store.selectedIdSet().has(row.change_id)),
    ),
    selectedCount: computed(() => store.selectedIds().length),
    previewRow: computed<AuthoringChangeListItem | null>(() => {
      const id = store.previewId();
      return id === null ? null : (store.rows().find((row) => row.change_id === id) ?? null);
    }),
    rowsWithBlocks: computed(() =>
      store.rows().map((row) => ({ row, blockedReason: rowBlockedReason(row) })),
    ),
    retryReady: computed(() => store._wait.ready()),
  })),
  withComputed((store) => ({
    preview: computed<PendingPreview | null>(() => {
      const row = store.previewRow();
      return row === null ? null : pendingPreview(row, store.targetTitles()[row.target_id] ?? null);
    }),
    applyBlockedReason: computed<string | null>(() => blockedApplyReason(store)),
    discardBlocked: computed(() => discardRefused(store)),
  })),
  withMethods((store) => {
    function noteWait(error: ApiError): void {
      patchState(store, { retryAfterSeconds: error.retryAfterSeconds });
      store._wait.start(error.retryAfterSeconds);
    }

    function rememberTitles(items: readonly ChecklistItemDto[]): void {
      const titles = { ...store.targetTitles() };
      for (const item of items) titles[item.item_id] = item.title;
      patchState(store, { targetTitles: titles });
    }

    const { read, reload } = createPendingReads(store, noteWait);
    const apply = createPendingApply(store, read, noteWait);

    return {
      async init(projectId: string, scope: { readonly targetId?: string } = {}): Promise<void> {
        const targetId = scope.targetId ?? null;
        if (store.projectId() !== projectId || store.targetId() !== targetId) {
          store._keys.reset();
          store._run.stop();
          patchState(store, {
            projectId,
            targetId,
            rows: [],
            nextCursor: null,
            queueStatus: 'idle',
            queueError: null,
            queueStale: false,
            selectedIds: [],
            selectionTouched: false,
            batchEtag: null,
            previewId: null,
            localReason: '',
            targetTitles: {},
            receipt: null,
            conflict: null,
            applyError: null,
            applyRequest: null,
            discardError: null,
            enqueueError: null,
          });
        } else if (store.queueStatus() === 'ready' || store.queueStatus() === 'loading') {
          return;
        }
        await read(projectId, null, true);
      },
      async reload(): Promise<void> {
        await reload();
      },
      async loadMore(): Promise<void> {
        const project = store.projectId();
        const cursor = store.nextCursor();
        if (project === null || cursor === null || store.queueLoading() || store.applying()) return;
        await read(project, cursor, false);
      },
      toggle(id: string): void {
        const selected = new Set(store.selectedIds());
        if (selected.has(id)) selected.delete(id);
        else selected.add(id);
        patchState(store, {
          selectedIds: [...selected],
          selectionTouched: true,
          batchEtag: null,
        });
      },
      selectAll(): void {
        patchState(store, {
          selectedIds: store
            .rows()
            .slice(0, MAX_APPLY_SELECTION)
            .map((row) => row.change_id),
          selectionTouched: true,
          batchEtag: null,
        });
      },
      clearSelection(): void {
        patchState(store, { selectedIds: [], selectionTouched: true, batchEtag: null });
      },
      setPreview(id: string | null): void {
        patchState(store, { previewId: id });
      },
      setReason(value: string): void {
        patchState(store, { localReason: value });
      },
      rememberTargets(items: readonly ChecklistItemDto[]): void {
        rememberTitles(items);
      },
      async loadChecklistFacts(
        query?: ListChecklistItemsData['query'],
        cursor?: string,
      ): Promise<readonly ChecklistItemDto[] | null> {
        const project = store.projectId();
        if (project === null) return null;
        const signal = store._run.call();
        try {
          const page = await store._data.checklistItems(project, signal, cursor, query);
          rememberTitles(page.items);
          return page.items;
        } catch (error) {
          if (isCancellation(error)) return null;
          const failure = fail(error, PENDING_COPY.queueFailed);
          patchState(store, { queueError: failure });
          return null;
        } finally {
          store._run.release(signal);
        }
      },
      async apply(): Promise<boolean> {
        return apply();
      },
      async discard(id: string): Promise<boolean> {
        const project = store.projectId();
        const unsettled = store.queueLoading() || store.queueStale();
        if (project === null || store.discardingId() !== null || unsettled) return false;
        if (!store.canWrite()) {
          patchState(store, {
            discardError: unexpectedFailure('write_forbidden', PENDING_COPY.readOnly),
          });
          return false;
        }
        const key = store._keys.discard(id);
        patchState(store, { discardingId: id, discardError: null });
        const signal = store._run.call();
        try {
          await store._data.discard(project, id, key, signal);
          store._keys.dropDiscard(id);
          patchState(store, {
            rows: store.rows().filter((row) => row.change_id !== id),
            selectedIds: store.selectedIds().filter((candidate) => candidate !== id),
            batchEtag: null,
            previewId: store.previewId() === id ? null : store.previewId(),
          });
          return true;
        } catch (error) {
          if (isCancellation(error)) return false;
          const failure = fail(error, PENDING_COPY.discardFailed);
          patchState(store, { discardError: failure });
          if (failure.kind === 'conflict' || failure.kind === 'not_found') {
            store._keys.dropDiscard(id);
            await reload();
          } else if (!failure.retryable) store._keys.dropDiscard(id);
          noteWait(failure);
          return false;
        } finally {
          store._run.release(signal);
          patchState(store, { discardingId: null });
        }
      },
      async queueComment(targetId: string, body: string, reason?: string): Promise<boolean> {
        return queue(pendingDocument(targetId, 'comment', { body }, reason));
      },
      async queueDelete(targetId: string, reason?: string): Promise<boolean> {
        return queue(pendingDocument(targetId, 'delete', {}, reason));
      },
    };

    async function queue(document: AuthoringChangeV1): Promise<boolean> {
      const project = store.projectId();
      if (project === null || store.queueing()) return false;
      if (!store.canWrite()) {
        patchState(store, {
          enqueueError: unexpectedFailure('write_forbidden', PENDING_COPY.readOnly),
        });
        return false;
      }
      patchState(store, { queueing: true, enqueueError: null });
      const signal = store._run.call();
      try {
        const signature = signatureOf('queue', JSON.stringify(document), '', []);
        await store._data.queue(project, document, store._keys.queue(signature), signal);
        store._keys.dropQueue();
        await reload();
        return true;
      } catch (error) {
        if (isCancellation(error)) return false;
        const failure = fail(error, PENDING_COPY.queueFailed);
        patchState(store, {
          enqueueError:
            failure.kind === 'conflict' &&
            failure.details.some((detail) => detail.reason === 'change_already_pending')
              ? { ...failure, message: PENDING_COPY.duplicatePending }
              : failure,
        });
        if (!failure.retryable) store._keys.dropQueue();
        if (failure.kind === 'conflict') await reload();
        noteWait(failure);
        return false;
      } finally {
        store._run.release(signal);
        patchState(store, { queueing: false });
      }
    }
  }),
  withHooks({
    onDestroy(store) {
      store._run.stop();
      store._keys.reset();
      store._wait.clear();
    },
  }),
);

export type PendingChangesStoreInstance = InstanceType<typeof PendingChangesStore>;
