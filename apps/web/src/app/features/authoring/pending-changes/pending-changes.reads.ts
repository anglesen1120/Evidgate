import { patchState } from '@ngrx/signals';
import type { AuthoringChangeListItem } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import { MAX_APPLY_SELECTION, PENDING_COPY } from './pending-changes.copy';
import { pendingRequestFailure as fail } from './pending-changes.intent';
import { NO_TITLES, type PendingStoreHost } from './pending-changes.state';

export function createPendingReads(store: PendingStoreHost, noteWait: (error: ApiError) => void) {
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

  return { read, reload };
}
