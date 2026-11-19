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
  ApplyAuthoringChangesResponse,
  AuthoringChangeListItem,
  AuthoringChangeV1,
  ChecklistItemDto,
  ListChecklistItemsData,
} from '@evidgate/sdk';

import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { createRetryAfterWait } from '../../../core/http/retry';
import { SessionStore } from '../../../core/session/session.store';
import { AUTHORING_READ, AUTHORING_WRITE } from '../import-export/import-export.copy';
import {
  MAX_APPLY_SELECTION,
  PENDING_COPY,
  isStale,
  pendingFailure,
  pendingPreview,
  rowBlockedReason,
  selectionBlockedReason,
  type PendingPreview,
} from './pending-changes.copy';
import {
  createPendingBookkeeping,
  isCancellation,
  signatureOf,
  unexpectedFailure,
} from './pending-changes.intent';

export type PendingQueueStatus = 'idle' | 'loading' | 'ready' | 'error';

export interface PendingConflict {
  readonly code: string;
  readonly pointers: readonly { readonly path: string | null; readonly reason: string }[];
}

export interface PendingRow {
  readonly row: AuthoringChangeListItem;
  readonly blockedReason: string | null;
}

const NO_TITLES: Readonly<Record<string, string>> = {};

export const PendingChangesStore = signalStore(
  withState({
    projectId: null as string | null,
    rows: [] as readonly AuthoringChangeListItem[],
    nextCursor: null as string | null,
    queueStatus: 'idle',
    queueError: null as ApiError | null,
    queueStale: false,
    loadToken: 0,
    selectedIds: [] as readonly string[],
    selectionTouched: false,
    previewId: null as string | null,
    localReason: '',
    targetTitles: NO_TITLES,
    applying: false,
    applyError: null as ApiError | null,
    conflict: null as PendingConflict | null,
    receipt: null as ApplyAuthoringChangesResponse | null,
    discardingId: null as string | null,
    discardError: null as ApiError | null,
    queueing: false,
    enqueueError: null as ApiError | null,
    retryAfterSeconds: null as number | null,
  }),
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
    canRead: computed(() => store._session.has(AUTHORING_READ)),
    canWrite: computed(() => store._session.has(AUTHORING_WRITE)),
    empty: computed(() => store.queueStatus() === 'ready' && store.rows().length === 0),
    notFound: computed(
      () => store.queueStatus() === 'error' && store.queueError()?.kind === 'not_found',
    ),
    forbidden: computed(
      () => store.queueStatus() === 'error' && store.queueError()?.kind === 'forbidden',
    ),
    selectedRows: computed(() => {
      const selected = new Set(store.selectedIds());
      return store.rows().filter((row) => selected.has(row.change_id));
    }),
    selectedCount: computed(() => store.selectedIds().length),
    allSelected: computed(() => {
      const loaded = store.rows().slice(0, MAX_APPLY_SELECTION);
      const selected = new Set(store.selectedIds());
      return loaded.length > 0 && loaded.every((row) => selected.has(row.change_id));
    }),
    previewRow: computed<AuthoringChangeListItem | null>(() => {
      const id = store.previewId();
      return id === null ? null : (store.rows().find((row) => row.change_id === id) ?? null);
    }),
    rowsWithBlocks: computed<readonly PendingRow[]>(() =>
      store.rows().map((row) => ({ row, blockedReason: rowBlockedReason(row) })),
    ),
    retryReady: computed(() => store._wait.ready()),
  })),
  withComputed((store) => ({
    staleSelectionIds: computed(() =>
      store
        .selectedRows()
        .filter((row) => isStale(row))
        .map((row) => row.change_id),
    ),
    preview: computed<PendingPreview | null>(() => {
      const row = store.previewRow();
      return row === null ? null : pendingPreview(row, store.targetTitles()[row.target_id] ?? null);
    }),
    applyBlockedReason: computed<string | null>(() => {
      if (!store.canWrite()) return PENDING_COPY.readOnly;
      if (store.applying()) return PENDING_COPY.applying;
      return selectionBlockedReason(store.rows(), store.selectedIds());
    }),
    discardBlocked: computed(() => !store.canWrite() || store.discardingId() !== null),
  })),
  withMethods((store) => {
    function fail(error: unknown, fallback: string): ApiError {
      return isApiRequestError(error)
        ? { ...error.error, message: pendingFailure(error.error, fallback) }
        : unexpectedFailure('pending_unexpected', fallback);
    }

    function noteWait(error: ApiError): void {
      patchState(store, { retryAfterSeconds: error.retryAfterSeconds });
      store._wait.start(error.retryAfterSeconds);
    }

    function rememberTitles(items: readonly ChecklistItemDto[]): void {
      const titles = { ...store.targetTitles() };
      for (const item of items) titles[item.item_id] = item.title;
      patchState(store, { targetTitles: titles });
    }

    function adoptPage(items: readonly AuthoringChangeListItem[], cursor: string | null): void {
      const present = new Set(items.map((row) => row.change_id));
      const kept = store.selectedIds().filter((id) => present.has(id));
      const selectedIds = store.selectionTouched()
        ? kept
        : items.slice(0, MAX_APPLY_SELECTION).map((row) => row.change_id);
      const previewId = store.previewId();
      patchState(store, {
        rows: items,
        nextCursor: cursor,
        selectedIds,
        previewId: previewId !== null && present.has(previewId) ? previewId : null,
        queueStale: false,
      });
    }

    async function read(project: string, cursor: string | null, replace: boolean): Promise<void> {
      const token = store.loadToken() + 1;
      patchState(store, { loadToken: token, queueStatus: 'loading', queueError: null });
      const signal = store._run.startRead();
      try {
        const page = await store._data.listPending(project, signal, cursor ?? undefined);
        if (store.loadToken() !== token || !store._run.ownsRead(signal)) return;
        if (replace) adoptPage(page.items, page.next_cursor);
        else {
          const seen = new Set(store.rows().map((row) => row.change_id));
          patchState(store, {
            rows: [...store.rows(), ...page.items.filter((row) => !seen.has(row.change_id))],
            nextCursor: page.next_cursor,
          });
        }
        patchState(store, { queueStatus: 'ready' });
      } catch (error) {
        if (store.loadToken() !== token || !store._run.ownsRead(signal)) return;
        const failure = fail(error, PENDING_COPY.queueFailed);
        patchState(store, {
          queueStatus: store.rows().length > 0 ? 'ready' : 'error',
          queueError: failure,
          queueStale: store.rows().length > 0,
        });
        noteWait(failure);
      }
    }

    async function reload(): Promise<void> {
      const project = store.projectId();
      if (project !== null) await read(project, null, true);
    }

    /** Reads the exact selection again: the only source of the apply `If-Match`. */
    async function refresh(project: string, ids: readonly string[]): Promise<string | null> {
      const signal = store._run.startRead();
      const { items, batchEtag } = await store._data.selection(project, ids, signal);
      if (!store._run.ownsRead(signal)) return null;
      const byId = new Map(items.map((row) => [row.change_id, row]));
      patchState(store, {
        rows: store.rows().map((row) => byId.get(row.change_id) ?? row),
      });
      return batchEtag;
    }

    async function apply(): Promise<boolean> {
      const project = store.projectId();
      const ids = store.selectedIds();
      if (project === null || store.applying()) return false;
      const blocked = selectionBlockedReason(store.rows(), ids);
      if (blocked !== null || !store.canWrite()) {
        patchState(store, {
          applyError: unexpectedFailure('selection_blocked', blocked ?? PENDING_COPY.readOnly),
        });
        return false;
      }
      patchState(store, { applying: true, applyError: null, conflict: null });
      const signal = store._run.call();
      try {
        const etag = await refresh(project, ids);
        if (etag === null) return false;
        if (store.selectedRows().some((row) => isStale(row))) {
          patchState(store, {
            applyError: unexpectedFailure('selection_stale', PENDING_COPY.blockedStale),
          });
          return false;
        }
        const key = store._keys.apply(signatureOf('apply', '', etag, ids));
        const result = await store._data.apply(project, etag, ids, key, signal);
        const applied = new Set(result.applied_change_ids);
        store._keys.dropApply();
        const previewId = store.previewId();
        patchState(store, {
          rows: store.rows().filter((row) => !applied.has(row.change_id)),
          selectedIds: store.selectedIds().filter((id) => !applied.has(id)),
          previewId: previewId !== null && applied.has(previewId) ? null : previewId,
          receipt: result,
          applyError: null,
          conflict: null,
        });
        return true;
      } catch (error) {
        if (isCancellation(error)) return false;
        const failure = fail(error, PENDING_COPY.applyFailed);
        if (failure.kind === 'precondition_failed') {
          store._keys.dropApply();
          patchState(store, {
            conflict: {
              code: failure.code,
              pointers: failure.details.map((detail) => ({
                path: detail.path ?? null,
                reason: detail.reason,
              })),
            },
            applyError: null,
          });
        } else {
          patchState(store, { applyError: failure });
          if (!failure.retryable) store._keys.dropApply();
          if (failure.kind === 'conflict') void reload();
          noteWait(failure);
        }
        return false;
      } finally {
        store._run.release(signal);
        patchState(store, { applying: false });
      }
    }

    return {
      async init(projectId: string): Promise<void> {
        if (store.projectId() !== projectId) {
          store._keys.reset();
          store._run.stop();
          patchState(store, {
            projectId,
            rows: [],
            nextCursor: null,
            queueStatus: 'idle',
            queueError: null,
            queueStale: false,
            selectedIds: [],
            selectionTouched: false,
            previewId: null,
            localReason: '',
            targetTitles: {},
            receipt: null,
            conflict: null,
            applyError: null,
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
        if (project === null || cursor === null || store.queueStatus() === 'loading') return;
        await read(project, cursor, false);
      },
      toggle(id: string): void {
        const selected = new Set(store.selectedIds());
        if (selected.has(id)) selected.delete(id);
        else selected.add(id);
        patchState(store, { selectedIds: [...selected], selectionTouched: true });
      },
      selectAll(): void {
        patchState(store, {
          selectedIds: store
            .rows()
            .slice(0, MAX_APPLY_SELECTION)
            .map((row) => row.change_id),
          selectionTouched: true,
        });
      },
      clearSelection(): void {
        patchState(store, { selectedIds: [], selectionTouched: true });
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
        if (project === null || store.discardingId() !== null) return false;
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
        const document: AuthoringChangeV1 = {
          target_kind: 'checklist_item',
          target_id: targetId,
          change_type: 'comment',
          payload: { body },
          ...(reason === undefined ? {} : { reason }),
        };
        return queue(document);
      },
      async queueDelete(targetId: string, reason?: string): Promise<boolean> {
        const document: AuthoringChangeV1 = {
          target_kind: 'checklist_item',
          target_id: targetId,
          change_type: 'delete',
          payload: {},
          ...(reason === undefined ? {} : { reason }),
        };
        return queue(document);
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
