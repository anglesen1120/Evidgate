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
import type { ApiError } from '../../../core/http/api-error';
import { createRetryAfterWait } from '../../../core/http/retry';
import { SessionStore } from '../../../core/session/session.store';
import { AUTHORING_READ, AUTHORING_WRITE } from '../import-export/import-export.copy';
import {
  MAX_APPLY_SELECTION,
  PENDING_COPY,
  isStale,
  pendingPreview,
  rowBlockedReason,
  selectionBlockedReason,
  type PendingPreview,
} from './pending-changes.copy';
import {
  createPendingBookkeeping,
  isCancellation,
  sameSelection,
  signatureOf,
  unexpectedFailure,
  pendingDocument,
  canReplayApply,
  pendingRequestFailure as fail,
  type PendingApplyReplay,
} from './pending-changes.intent';
import { applyBlockedReason as blockedApplyReason, discardRefused } from './pending-changes.guards';

export interface PendingConflict {
  readonly code: string;
  readonly pointers: readonly { readonly path: string | null; readonly reason: string }[];
}

const NO_TITLES: Readonly<Record<string, string>> = {};

export const PendingChangesStore = signalStore(
  withState({
    projectId: null as string | null,
    targetId: null as string | null,
    rows: [] as readonly AuthoringChangeListItem[],
    nextCursor: null as string | null,
    queueStatus: 'idle',
    queueError: null as ApiError | null,
    queueStale: false,
    queueLoading: false,
    loadToken: 0,
    selectedIds: [] as readonly string[],
    selectionTouched: false,
    /** The batch ETag the reviewed selection was read at; null until that read, and after it moves. */
    batchEtag: null as string | null,
    previewId: null as string | null,
    localReason: '',
    targetTitles: NO_TITLES,
    applying: false,
    applyError: null as ApiError | null,
    applyRequest: null as PendingApplyReplay | null,
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

    function blockApply(reason: string): false {
      patchState(store, { applyError: unexpectedFailure('selection_blocked', reason) });
      return false;
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
        batchEtag: null,
        previewId: previewId !== null && present.has(previewId) ? previewId : null,
        queueStale: false,
      });
    }

    async function read(project: string, cursor: string | null, replace: boolean): Promise<void> {
      const token = store.loadToken() + 1;
      patchState(store, {
        loadToken: token,
        queueLoading: true,
        queueStatus: store.queueStatus() === 'ready' ? 'ready' : 'loading',
        queueError: null,
      });
      const signal = store._run.startRead();
      try {
        // An unscoped read keeps the original three-argument call.
        const scope = store.targetId();
        const page =
          scope === null
            ? await store._data.listPending(project, signal, cursor ?? undefined)
            : await store._data.listPending(project, signal, cursor ?? undefined, scope);
        if (store.loadToken() !== token || !store._run.ownsRead(signal)) return;
        if (replace) adoptPage(page.items, page.next_cursor);
        else {
          const seen = new Set(store.rows().map((row) => row.change_id));
          patchState(store, {
            rows: [...store.rows(), ...page.items.filter((row) => !seen.has(row.change_id))],
            nextCursor: page.next_cursor,
          });
        }
        patchState(store, {
          queueStatus: 'ready',
          applyError: store.applyError()?.kind === 'forbidden' ? null : store.applyError(),
          discardError: store.discardError()?.kind === 'forbidden' ? null : store.discardError(),
        });
        if (replace && store.applyRequest() !== null) {
          store._keys.dropApply();
          patchState(store, { applyRequest: null, applyError: null });
        }
      } catch (error) {
        if (store.loadToken() !== token || !store._run.ownsRead(signal)) return;
        const failure = fail(error, PENDING_COPY.queueFailed);
        const denied = failure.kind === 'forbidden' || failure.kind === 'not_found';
        patchState(store, {
          queueStatus: !denied && store.rows().length > 0 ? 'ready' : 'error',
          queueError: failure,
          queueStale: !denied && store.rows().length > 0,
          ...(denied ? { rows: [], targetTitles: NO_TITLES, receipt: null, previewId: null } : {}),
        });
        noteWait(failure);
      } finally {
        if (store.loadToken() === token) patchState(store, { queueLoading: false });
      }
    }

    async function reload(): Promise<void> {
      const project = store.projectId();
      if (project !== null && !store.applying()) await read(project, null, true);
    }

    /** Reads the exact selection again: the only source of the apply `If-Match`. */
    async function refresh(
      project: string,
      ids: readonly string[],
      signal: AbortSignal,
    ): Promise<string | null> {
      const { items, batchEtag } = await store._data.selection(project, ids, signal);
      if (signal.aborted) return null;
      const byId = new Map(items.map((row) => [row.change_id, row]));
      patchState(store, {
        rows: store.rows().map((row) => byId.get(row.change_id) ?? row),
        batchEtag,
      });
      return batchEtag;
    }

    async function apply(): Promise<boolean> {
      const project = store.projectId();
      const ids = store.selectedIds();
      if (project === null || store.applying() || store.queueLoading()) return false;
      let request = store.applyRequest();
      if (request !== null && !sameSelection(request.ids, ids))
        return blockApply(PENDING_COPY.replaySelectionChanged);
      const blocked = request === null ? selectionBlockedReason(store.rows(), ids) : null;
      if (blocked !== null || !store.canWrite())
        return blockApply(blocked ?? PENDING_COPY.readOnly);
      patchState(store, { applying: true, applyError: null, conflict: null });
      const signal = store._run.call();
      try {
        if (request === null) {
          const etag = await refresh(project, ids, signal);
          if (etag === null) return false;
          if (store.selectedRows().some((row) => isStale(row))) {
            patchState(store, {
              applyError: unexpectedFailure('selection_stale', PENDING_COPY.blockedStale),
            });
            return false;
          }
          request = {
            ids: [...ids],
            rows: store.selectedRows(),
            etag,
            key: store._keys.apply(signatureOf('apply', '', etag, ids)),
          };
          patchState(store, { applyRequest: request });
        }
        const result = await store._data.apply(
          project,
          request.etag,
          request.ids,
          request.key,
          signal,
        );
        const applied = new Set(result.applied_change_ids);
        store._keys.dropApply();
        const previewId = store.previewId();
        patchState(store, {
          rows: store.rows().filter((row) => !applied.has(row.change_id)),
          selectedIds: store.selectedIds().filter((id) => !applied.has(id)),
          batchEtag: null,
          previewId: previewId !== null && applied.has(previewId) ? null : previewId,
          receipt: result,
          applyRequest: null,
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
            applyRequest: null,
          });
        } else {
          patchState(store, { applyError: failure });
          if (!canReplayApply(failure)) {
            store._keys.dropApply();
            patchState(store, { applyRequest: null });
          }
          if (failure.kind === 'conflict') await read(project, null, true);
          noteWait(failure);
        }
        return false;
      } finally {
        store._run.release(signal);
        patchState(store, { applying: false });
      }
    }

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
