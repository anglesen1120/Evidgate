import type { ReviewNoticeViewModel } from './scripts-review-state';

export const proposalNotices = {
  unavailable: {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.requested-record:Requested record unavailable`,
    description: $localize`:@@review.ui-change-proposal.no-record:No matching record in this preview.`,
  },
  loading: {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-loading:Loading`,
    description: $localize`:@@review.ui-change-proposal.state-loading-detail:Reading the requested project scope.`,
  },
  error: {
    replace: false,
    tone: 'destructive',
    title: $localize`:@@review.ui-change-proposal.state-error:Proposal could not be refreshed`,
    description: $localize`:@@review.ui-change-proposal.state-error-detail:The pinned failure and stored comparison remain visible. Retry is deliberate and queues nothing.`,
  },
  '401': {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-401:Your session expired`,
    description: $localize`:@@review.ui-change-proposal.state-401-detail:Sign in again. The safe return route and any typed rejection reason are preserved.`,
  },
  '429': {
    replace: true,
    tone: 'warning',
    title: $localize`:@@review.ui-change-proposal.state-429:Too many requests`,
    description: $localize`:@@review.ui-change-proposal.state-429-detail:Nothing was queued. Retry only after the server-supplied Retry-After interval.`,
  },
  '404': {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-404:Not found`,
    description: $localize`:@@review.ui-change-proposal.state-404-detail:This resource is not available.`,
  },
  empty: {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-empty:No proposal`,
    description: $localize`:@@review.ui-change-proposal.state-empty-detail:No proposal is available in this scope.`,
  },
  queueing: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-queueing:Queueing`,
    description: $localize`:@@review.ui-change-proposal.state-queueing-detail:Preparing the pending item. No authoring version exists yet.`,
  },
  queued: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-queued:Queued in pending changes`,
    description: $localize`:@@review.ui-change-proposal.state-queued-detail:ucp-12 was queued once at base v2. No version was applied.`,
  },
  duplicate: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-duplicate:Already queued`,
    description: $localize`:@@review.ui-change-proposal.state-duplicate-detail:The same pending item exists. No duplicate or new version was created.`,
  },
  rejected: {
    replace: false,
    tone: 'destructive',
    title: $localize`:@@review.ui-change-proposal.state-rejected:Rejected · reason retained`,
    description: $localize`:@@review.ui-change-proposal.state-rejected-detail:Only the proposal was rejected. The original result and manual case are unchanged.`,
  },
  stale: {
    replace: false,
    tone: 'warning',
    title: $localize`:@@review.ui-change-proposal.state-stale:Stale base · 412`,
    description: $localize`:@@review.ui-change-proposal.state-stale-detail:Review base v2 differs from current v3. Reload explicitly; your draft is retained.`,
  },
  comparison: {
    replace: false,
    tone: 'warning',
    title: $localize`:@@review.ui-change-proposal.state-comparison:Comparison unavailable`,
    description: $localize`:@@review.ui-change-proposal.state-comparison-detail:The step comparison was not supplied. Queueing is unavailable.`,
  },
  evidence: {
    replace: false,
    tone: 'warning',
    title: $localize`:@@review.ui-change-proposal.state-evidence:Evidence unavailable`,
    description: $localize`:@@review.ui-change-proposal.state-evidence-detail:The evidence reference is unavailable. The recorded failure stays failed.`,
  },
  denied: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-denied:Review permission revoked`,
    description: $localize`:@@review.ui-change-proposal.state-denied-detail:Read-only: no authoring-write grant. No decision can be submitted.`,
  },
  'reject-error': {
    replace: false,
    tone: 'destructive',
    title: $localize`:@@review.ui-change-proposal.state-reject-error:Rejection failed`,
    description: $localize`:@@review.ui-change-proposal.state-reject-error-detail:Reopen the rejection dialog to retry. The reason is retained.`,
  },
  rejecting: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-rejecting:Submitting rejection`,
    description: $localize`:@@review.ui-change-proposal.state-rejecting-detail:The rejection is not confirmed.`,
  },
  long: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.ui-change-proposal.state-long:Long step wording`,
    description: $localize`:@@review.ui-change-proposal.state-long-detail:Full text is retained for review.`,
  },
} as const satisfies Readonly<Record<string, ReviewNoticeViewModel>>;

export type UiChangeProposalState = 'ready' | keyof typeof proposalNotices;
