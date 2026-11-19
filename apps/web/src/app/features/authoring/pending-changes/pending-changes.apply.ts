import { patchState } from '@ngrx/signals';
import type { ApiError } from '../../../core/http/api-error';
import { PENDING_COPY, isStale, selectionBlockedReason } from './pending-changes.copy';
import {
  canReplayApply,
  isCancellation,
  pendingRequestFailure as fail,
  sameSelection,
  signatureOf,
  unexpectedFailure,
} from './pending-changes.intent';
import type { PendingStoreHost } from './pending-changes.state';

export function createPendingApply(
  store: PendingStoreHost,
  read: (project: string, cursor: string | null, replace: boolean) => Promise<void>,
  noteWait: (error: ApiError) => void,
) {
  function blockApply(reason: string): false {
    patchState(store, { applyError: unexpectedFailure('selection_blocked', reason) });
    return false;
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
    if (blocked !== null || !store.canWrite()) return blockApply(blocked ?? PENDING_COPY.readOnly);
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

  return apply;
}
