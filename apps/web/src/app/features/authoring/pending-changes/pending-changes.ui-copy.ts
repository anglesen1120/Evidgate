import type { AuthoringChangeListItem } from '@evidgate/sdk';
import { PENDING_COPY } from './pending-changes.copy';

export const PENDING_UI = {
  atomic: $localize`:@@authoring.pending-changes.atomic-scope:One atomic batch, exact reviewed selection`,
  atomicBody: $localize`:@@authoring.pending-changes.atomic-body:Only the selected change ids are submitted. Unselected and unloaded changes stay queued. Applying does not publish or approve drafts.`,
  operation: $localize`:@@authoring.pending-changes.operation:Operation`,
  state: $localize`:@@authoring.pending-changes.state:State`,
  pending: $localize`:@@authoring.pending-changes.pending-state:Queued`,
  stale: $localize`:@@authoring.pending-changes.stale-state:Stale`,
  unavailable: $localize`:@@authoring.pending-changes.unavailable-state:Unavailable`,
  structural: $localize`:@@authoring.pending-changes.structural-unavailable:Unavailable until structural apply lands. Merge and split changes stay queued and block Apply when selected.`,
  staleQueue: $localize`:@@authoring.pending-changes.stale-queue:Refresh failed. These are previously loaded changes, not a confirmed current queue.`,
  select: $localize`:@@authoring.pending-changes.select-change:Select change`,
  preview: $localize`:@@authoring.pending-changes.preview-action:Preview`,
  changeId: $localize`:@@authoring.pending-changes.change-id:Change id`,
  loadMore: $localize`:@@authoring.pending-changes.load-more:Load more changes`,
  batchSummary: $localize`:@@authoring.pending-changes.batch-summary:Batch summary`,
  exactIds: $localize`:@@authoring.pending-changes.exact-ids:Exact reviewed change ids`,
  metadata: $localize`:@@authoring.pending-changes.metadata:Implementation metadata · read-only`,
  batchEtag: $localize`:@@authoring.pending-changes.batch-etag:Batch ETag`,
  etagNotRead: $localize`:@@authoring.pending-changes.etag-not-read:The exact-selection ETag is read immediately before applying. No batch token has been read for this selection yet.`,
  idempotency: $localize`:@@authoring.pending-changes.idempotency-note:The same apply intent reuses one idempotency key. The batch ETag comes from the exact-selection read, never from an individual item version.`,
  manual: $localize`:@@authoring.pending-changes.manual-source:Manual`,
  ai: $localize`:@@authoring.pending-changes.ai-source:AI`,
  imported: $localize`:@@authoring.pending-changes.import-source:Import`,
  baseVersion: $localize`:@@authoring.pending-changes.base-version:Target base version`,
  queuedAt: $localize`:@@authoring.pending-changes.queued-at:Queued at (UTC)`,
  appliedIds: $localize`:@@authoring.pending-changes.applied-ids:Applied change ids`,
  entity: $localize`:@@authoring.pending-changes.entity:Entity`,
  commentHint: $localize`:@@authoring.pending-changes.comment-hint:Queue a plain-text comment for review. This does not apply it.`,
  queueReason: $localize`:@@authoring.pending-changes.queue-reason:Change reason (optional)`,
  commentRequired: $localize`:@@authoring.pending-changes.comment-required:Enter a comment before queueing it.`,
  reviewTab: $localize`:@@authoring.pending-changes.review-tab-title:Pending changes for this item`,
  itemEmpty: $localize`:@@authoring.pending-changes.item-empty:No loaded pending change targets this item.`,
  itemScope: $localize`:@@authoring.pending-changes.item-scope:This list includes loaded changes for this item. Load more to inspect older pending changes.`,
  openQueue: $localize`:@@authoring.pending-changes.open-queue:Review the project pending queue`,
  queueDeleteTitle: $localize`:@@authoring.pending-changes.queue-delete-title:Queue a delete for this item?`,
  queueDeleteBody: $localize`:@@authoring.pending-changes.queue-delete-body:This only queues a delete for review. The item is not deleted until an authorized reviewer applies the batch.`,
  queued: $localize`:@@authoring.pending-changes.queued-success:The change was queued for review.`,
  reasonRejected: $localize`:@@authoring.pending-changes.reason-rejected:The server refused the change reason. Review it before trying again.`,
  quota: PENDING_COPY.quotaExceeded,
  writeDenied: $localize`:@@authoring.pending-changes.write-denied:Write access was refused. The queue stays readable; reload after your permissions have been restored.`,
};
export function operationLabel(kind: AuthoringChangeListItem['change_type']): string {
  return kind === 'comment'
    ? PENDING_COPY.commentLabel
    : kind === 'delete'
      ? PENDING_COPY.deleteLabel
      : kind === 'merge'
        ? PENDING_COPY.mergeLabel
        : PENDING_COPY.splitLabel;
}
export function sourceLabel(source: AuthoringChangeListItem['source']): string {
  return source === 'manual'
    ? PENDING_UI.manual
    : source === 'ai'
      ? PENDING_UI.ai
      : PENDING_UI.imported;
}
