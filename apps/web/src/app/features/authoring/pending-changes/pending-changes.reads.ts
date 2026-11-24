import { patchState } from '@ngrx/signals';
import type { AuthoringChangeListItem } from '@evidgate/sdk';
import { ApiRequestError } from '../../../core/http/api-error';
import { takeKeysetPage } from '../../../shared/keyset-walk';
import type { ApiError } from '../../../core/http/api-error';
import { MAX_APPLY_SELECTION, PENDING_COPY } from './pending-changes.copy';
import { pendingRequestFailure as fail, unexpectedFailure } from './pending-changes.intent';
import { NO_TITLES, type PendingStoreHost } from './pending-changes.state';

export function createPendingReads(store: PendingStoreHost, noteWait: (error: ApiError) => void) {
  function adoptPage(
    items: readonly AuthoringChangeListItem[],
    cursor: string | null,
    changedPage: boolean,
  ): void {
    const present = new Set(items.map((row) => row.change_id));
    const kept = store.selectedIds().filter((id) => present.has(id));
    const selectedIds =
      store.selectionTouched() && !changedPage
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
      selectionTouched: changedPage ? false : store.selectionTouched(),
    });
  }

  function pageRows(
    items: readonly AuthoringChangeListItem[],
    cursor: string | null,
    nextCursor: string | null,
    targetPage: number,
    replace: boolean,
    history: readonly (string | null)[],
  ): readonly AuthoringChangeListItem[] {
    const repeated = unexpectedFailure('cursor_repeated', PENDING_COPY.queueFailed);
    const step = takeKeysetPage({
      cursor,
      items,
      nextCursor,
      seen: new Set(
        replace
          ? []
          : store
              .pageRowIds()
              .slice(0, targetPage - 1)
              .flat(),
      ),
      keyOf: (row) => row.change_id,
      refusals: {
        cursorRepeated: repeated,
        pageWithoutNewRows: unexpectedFailure('page_without_new_rows', PENDING_COPY.queueFailed),
      },
    });
    if (step.outcome === 'refused') throw new ApiRequestError(step.error);
    if (nextCursor !== null && history.includes(nextCursor)) throw new ApiRequestError(repeated);
    return step.fresh;
  }

  async function read(
    project: string,
    cursor: string | null,
    replace: boolean,
    targetPage = replace ? 1 : store.page() + 1,
  ): Promise<void> {
    const token = store.loadToken() + 1;
    patchState(store, {
      loadToken: token,
      queueLoading: true,
      queueStatus: store.queueStatus() === 'ready' ? 'ready' : 'loading',
      queueError: null,
      pendingPage: { page: targetPage, cursor },
    });
    const signal = store._run.startRead();
    try {
      const page = await store._data.listPending(
        project,
        signal,
        cursor ?? undefined,
        store.targetId() ?? undefined,
        store.pageSize(),
      );
      if (store.loadToken() !== token || !store._run.ownsRead(signal)) return;
      const history = [...store.requestCursors().slice(0, targetPage - 1), cursor];
      const rows = pageRows(page.items, cursor, page.next_cursor, targetPage, replace, history);
      adoptPage(rows, page.next_cursor, targetPage !== store.page());
      patchState(store, {
        queueStatus: 'ready',
        requestCursors: history,
        pageRowIds: [
          ...store.pageRowIds().slice(0, targetPage - 1),
          rows.map((row) => row.change_id),
        ],
        page: targetPage,
        pendingPage: null,
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
