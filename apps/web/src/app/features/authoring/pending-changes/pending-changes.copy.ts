import type { AuthoringChangeListItem } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';

export const PENDING_COPY = {
  title: $localize`:@@authoring.pending-changes.title:Pending changes`,
  description: $localize`:@@authoring.pending-changes.description:Review the queued changes and apply the confirmed set as one batch. There is no per-row apply and no partial success.`,
  back: $localize`:@@authoring.pending-changes.back:Back to feature tree`,
  queueTitle: $localize`:@@authoring.pending-changes.queue-title:Queued changes`,
  queueScope: $localize`:@@authoring.pending-changes.queue-scope:Loaded pending changes`,
  previewTitle: $localize`:@@authoring.pending-changes.preview-title:Selected change`,
  previewNone: $localize`:@@authoring.pending-changes.preview-none:Choose a queued change to read its preview.`,
  selectAll: $localize`:@@authoring.pending-changes.select-all:Select every loaded change`,
  clearSelection: $localize`:@@authoring.pending-changes.clear-selection:Clear the selection`,
  selectionSummary: $localize`:@@authoring.pending-changes.selection-summary:Selected changes`,
  reasonLabel: $localize`:@@authoring.pending-changes.reason-label:Batch reason`,
  reasonHint: $localize`:@@authoring.pending-changes.reason-hint:Kept in this page only. The apply request carries the selected ids, so this note is not stored.`,
  reasonUnset: $localize`:@@authoring.pending-changes.reason-unset:Not set`,
  applyAction: $localize`:@@authoring.pending-changes.apply-action:Apply all changes as one atomic batch`,
  applyConfirmTitle: $localize`:@@authoring.pending-changes.apply-confirm-title:Apply the selected changes?`,
  applyConfirmBody: $localize`:@@authoring.pending-changes.apply-confirm-body:Every selected pending change is applied in one transaction. Nothing is applied if the batch is stale.`,
  applyConfirm: $localize`:@@authoring.pending-changes.apply-confirm:Apply changes`,
  applying: $localize`:@@authoring.pending-changes.applying:Applying the batch…`,
  refreshing: $localize`:@@authoring.pending-changes.refreshing:Refreshing the queue… Previously loaded changes remain available.`,
  cancel: $localize`:@@authoring.pending-changes.cancel:Cancel`,
  discardAction: $localize`:@@authoring.pending-changes.discard-action:Discard this change`,
  discardConfirmTitle: $localize`:@@authoring.pending-changes.discard-confirm-title:Discard this pending change?`,
  discardConfirmBody: $localize`:@@authoring.pending-changes.discard-confirm-body:Only this queued change is discarded. Every other queued change is kept.`,
  discardConfirm: $localize`:@@authoring.pending-changes.discard-confirm:Discard change`,
  receiptTitle: $localize`:@@authoring.pending-changes.receipt-title:Applied batch`,
  receiptBatch: $localize`:@@authoring.pending-changes.receipt-batch:Batch`,
  receiptCommitted: $localize`:@@authoring.pending-changes.receipt-committed:Committed`,
  receiptVersions: $localize`:@@authoring.pending-changes.receipt-versions:Written versions`,
  receiptCommentOnly: $localize`:@@authoring.pending-changes.receipt-comment-only:This batch wrote no entity version because every change was a comment.`,
  receiptLineage: $localize`:@@authoring.pending-changes.receipt-lineage:Lineage`,
  conflictTitle: $localize`:@@authoring.pending-changes.conflict-title:The batch changed before it was applied`,
  conflictBody: $localize`:@@authoring.pending-changes.conflict-body:Nothing was applied. Every queued change and the local reason are kept. Reload is an explicit read; there is no automatic retry or merge.`,
  conflictReload: $localize`:@@authoring.pending-changes.conflict-reload:Reload the queue`,
  conflictClose: $localize`:@@authoring.pending-changes.conflict-close:Close`,
  readOnly: $localize`:@@authoring.pending-changes.read-only:You can read this queue but not change it. Applying and discarding need the authoring write permission.`,
  blockedStructural: $localize`:@@authoring.pending-changes.blocked-structural:This selection contains a merge or split. Structural changes cannot be applied yet.`,
  blockedStale: $localize`:@@authoring.pending-changes.blocked-stale:An affected item changed after this change was queued. Reload the queue before applying.`,
  blockedEmpty: $localize`:@@authoring.pending-changes.blocked-empty:Select at least one pending change.`,
  blockedTooMany: $localize`:@@authoring.pending-changes.blocked-too-many:One apply takes at most 200 exact changes.`,
  replaySelectionChanged: $localize`:@@authoring.pending-changes.replay-selection-changed:The earlier apply never got an answer, so its exact selection must be retried. Restore the original selection to retry it, or reload the queue to start a new batch.`,
  emptyTitle: $localize`:@@authoring.pending-changes.empty-title:No pending changes`,
  emptyBody: $localize`:@@authoring.pending-changes.empty-body:This project has no queued authoring change.`,
  notFoundTitle: $localize`:@@authoring.pending-changes.not-found-title:Project not found`,
  notFoundBody: $localize`:@@authoring.pending-changes.not-found-body:This project is unknown or not available to this account.`,
  forbiddenTitle: $localize`:@@authoring.pending-changes.forbidden-title:Authoring changes are not available`,
  forbiddenBody: $localize`:@@authoring.pending-changes.forbidden-body:This account cannot read this project's authoring changes.`,
  retryAfter: $localize`:@@authoring.pending-changes.retry-after:The server asked to wait before trying again.`,
  applyFailed: $localize`:@@authoring.pending-changes.apply-failed:The batch was not applied.`,
  discardFailed: $localize`:@@authoring.pending-changes.discard-failed:The change was not discarded.`,
  queueFailed: $localize`:@@authoring.pending-changes.queue-failed:The change was not queued.`,
  duplicatePending: $localize`:@@authoring.pending-changes.duplicate-pending:An affected item already has a pending structural change.`,
  commentAction: $localize`:@@authoring.pending-changes.comment-action:Queue a comment`,
  deleteAction: $localize`:@@authoring.pending-changes.delete-action:Queue a delete`,
  deleteConsequence: $localize`:@@authoring.pending-changes.delete-consequence:Applying this delete removes the named target item.`,
  quotaExceeded: $localize`:@@authoring.pending-changes.quota-reached:The pending-change limit has been reached. Review or discard existing changes before queueing another.`,
  // Resolved per render: a module value would freeze the locale loaded at import.
  get commentLabel(): string {
    return $localize`:@@authoring.pending-changes.comment-label:Comment`;
  },
  get deleteLabel(): string {
    return $localize`:@@authoring.pending-changes.delete-label:Delete`;
  },
  get mergeLabel(): string {
    return $localize`:@@authoring.pending-changes.merge-label:Merge`;
  },
  get splitLabel(): string {
    return $localize`:@@authoring.pending-changes.split-label:Split`;
  },
  targetLabel: $localize`:@@authoring.pending-changes.target-label:Target item`,
  sourceLabel: $localize`:@@authoring.pending-changes.source-label:Merge sources`,
  survivorLabel: $localize`:@@authoring.pending-changes.survivor-label:Surviving item`,
  newItemLabel: $localize`:@@authoring.pending-changes.new-item-label:New item`,
  lineageLabel: $localize`:@@authoring.pending-changes.lineage-label:Lineage`,
  authorLabel: $localize`:@@authoring.pending-changes.author-label:Author`,
  sourceLabelQueued: $localize`:@@authoring.pending-changes.queued-source:Queued source`,
  impactUnavailable: $localize`:@@authoring.pending-changes.impact-unavailable:The affected cases and the full before/after impact are not supplied by this response.`,
  loadedScope: $localize`:@@authoring.pending-changes.loaded-scope:Loaded count, not the project total`,
} as const;

const MAX_SELECTION = 200;

/** Raised rows a merge or split needs before apply can write them (structural apply is unavailable). */
export function isStructural(row: AuthoringChangeListItem): boolean {
  return row.change_type === 'merge' || row.change_type === 'split';
}

export function isStale(row: AuthoringChangeListItem): boolean {
  return row.stale === true;
}

export function rowBlockedReason(row: AuthoringChangeListItem): string | null {
  if (isStructural(row)) return PENDING_COPY.blockedStructural;
  if (isStale(row)) return PENDING_COPY.blockedStale;
  return null;
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function uuidList(value: unknown): readonly string[] {
  return Array.isArray(value)
    ? value.filter((entry): entry is string => typeof entry === 'string')
    : [];
}

export interface PendingPreview {
  readonly kindLabel: string;
  readonly targetId: string;
  readonly lines: readonly string[];
  readonly lineage: readonly string[];
  readonly unavailable: string | null;
}

/** One queued change as plain text. Unknown payload members are never rendered as if known. */
export function pendingPreview(
  row: AuthoringChangeListItem,
  targetTitle: string | null,
): PendingPreview {
  const target = targetTitle ?? row.target_id;
  const lineage: string[] = [];
  let lines: string[] = [];
  if (row.change_type === 'comment') {
    const body = text(row.payload['body']);
    lines = [body ?? PENDING_COPY.impactUnavailable];
  } else if (row.change_type === 'delete') {
    lines = [`${PENDING_COPY.targetLabel}: ${target}`];
  } else if (row.change_type === 'merge') {
    const survivor = text(row.payload['survivor_item_id']);
    const sources = uuidList(row.payload['source_item_ids']);
    lines = [
      `${PENDING_COPY.sourceLabel}: ${sources.length ? sources.join(', ') : PENDING_COPY.impactUnavailable}`,
      `${PENDING_COPY.survivorLabel}: ${survivor ?? PENDING_COPY.impactUnavailable}`,
    ];
    if (survivor !== null)
      lineage.push(`${PENDING_COPY.lineageLabel}: ${sources.join(' → ')} → ${survivor}`);
  } else {
    const items = Array.isArray(row.payload['new_items']) ? row.payload['new_items'] : [];
    const titles = items
      .map((item) =>
        item !== null && typeof item === 'object'
          ? text((item as Record<string, unknown>)['title'])
          : null,
      )
      .filter((title): title is string => title !== null);
    lines = [
      titles.length
        ? titles
            .map((title, index) => `${PENDING_COPY.newItemLabel} ${index + 1}: ${title}`)
            .join('\n')
        : PENDING_COPY.impactUnavailable,
    ];
  }
  const kind =
    row.change_type === 'comment'
      ? PENDING_COPY.commentLabel
      : row.change_type === 'delete'
        ? PENDING_COPY.deleteLabel
        : row.change_type === 'merge'
          ? PENDING_COPY.mergeLabel
          : PENDING_COPY.splitLabel;
  return {
    kindLabel: kind,
    targetId: row.target_id,
    lines,
    lineage,
    unavailable: isStructural(row)
      ? PENDING_COPY.blockedStructural
      : PENDING_COPY.impactUnavailable,
  };
}

/** Why the current selection cannot be applied, or null when it can. */
export function selectionBlockedReason(
  rows: readonly AuthoringChangeListItem[],
  selectedIds: readonly string[],
): string | null {
  if (selectedIds.length === 0) return PENDING_COPY.blockedEmpty;
  if (selectedIds.length > MAX_SELECTION) return PENDING_COPY.blockedTooMany;
  const selected = new Set(selectedIds);
  const rowsById = new Map(rows.map((row) => [row.change_id, row]));
  if (selectedIds.some((id) => !rowsById.has(id))) return PENDING_COPY.blockedEmpty;
  if ([...selected].some((id) => isStructural(rowsById.get(id) as AuthoringChangeListItem)))
    return PENDING_COPY.blockedStructural;
  if ([...selected].some((id) => isStale(rowsById.get(id) as AuthoringChangeListItem)))
    return PENDING_COPY.blockedStale;
  return null;
}

/** The server text for a failed mutation, or a static sentence when the server sent none. */
export function pendingFailure(error: ApiError, fallback: string): string {
  if (error.code === 'quota_exceeded') return PENDING_COPY.quotaExceeded;
  return error.message.trim().length > 0 ? error.message : fallback;
}

export const MAX_APPLY_SELECTION = MAX_SELECTION;
