import type { AuthoringChangeListItem } from '@evidgate/sdk';
import { PENDING_COPY, selectionBlockedReason } from './pending-changes.copy';
import { sameSelection, type PendingApplyReplay } from './pending-changes.intent';

/** A queued row is only trustworthy while the page is settled and the writer may change it. */
export function discardRefused(state: {
  readonly canWrite: () => boolean;
  readonly discardingId: () => string | null;
  readonly queueLoading: () => boolean;
  readonly queueStale: () => boolean;
}): boolean {
  return (
    !state.canWrite() || state.discardingId() !== null || state.queueLoading() || state.queueStale()
  );
}

interface ApplyGate {
  readonly canWrite: () => boolean;
  readonly applying: () => boolean;
  readonly applyRequest: () => PendingApplyReplay | null;
  readonly selectedIds: () => readonly string[];
  readonly rows: () => readonly AuthoringChangeListItem[];
}

/**
 * Why an unresolved apply may not run with the current selection. Its key and batch already left
 * the page, so only that exact selection may replay them; anything else is a new batch.
 */
export function replayRefusal(
  request: PendingApplyReplay | null,
  selectedIds: readonly string[],
): string | null {
  if (request === null) return null;
  return sameSelection(request.ids, selectedIds) ? null : PENDING_COPY.replaySelectionChanged;
}

/** The reason apply is unavailable now, or null when it may run. */
export function applyBlockedReason(state: ApplyGate): string | null {
  if (!state.canWrite()) return PENDING_COPY.readOnly;
  if (state.applying()) return PENDING_COPY.applying;
  const request = state.applyRequest();
  return request !== null
    ? replayRefusal(request, state.selectedIds())
    : selectionBlockedReason(state.rows(), state.selectedIds());
}
