export interface ReleaseStateNotice {
  readonly title: string;
  readonly description: string;
  readonly action?: 'retry' | 'reload' | 'clear' | 'signin' | 'authoring';
}

const notices = {
  '401': {
    title: $localize`:@@review.release.state.code-401.title:Your session expired`,
    description: $localize`:@@review.release.state.code-401.description:Sign in again to return to this view.`,
    action: 'signin',
  },
  '403': {
    title: $localize`:@@review.release.state.code-403.title:Not authorized`,
    description: $localize`:@@review.release.state.code-403.description:Read permission is required in this project. No protected data is displayed.`,
  },
  '404': {
    title: $localize`:@@review.release.state.code-404.title:Not found`,
    description: $localize`:@@review.release.state.code-404.description:Check the address or return to the project.`,
  },
  '412': {
    title: $localize`:@@review.release.state.code-412.title:Someone else changed this — reload`,
    description: $localize`:@@review.release.state.code-412.description:Linked policy edit, not a release GET error: pinned pr-12 → current pr-13. Retained draft: freshness 48 h (saved: 24 h). No merge or resubmission.`,
    action: 'reload',
  },
  '429': {
    title: $localize`:@@review.release.state.code-429.title:Too many requests`,
    description: $localize`:@@review.release.state.code-429.description:Server Retry-After: 30 seconds. No automatic retry.`,
    action: 'retry',
  },
  empty: {
    title: $localize`:@@review.release.state.empty.title:Unbound release · no components`,
    description: $localize`:@@review.release.state.empty.description:There are no component records. An empty matrix is not a release PASS.`,
  },
  'no-match': {
    title: $localize`:@@review.release.state.no-match.title:No results match these filters`,
    description: $localize`:@@review.release.state.no-match.description:Records exist outside this selection. Clear the filters to see them.`,
    action: 'clear',
  },
  error: {
    title: $localize`:@@review.release.state.error.title:Could not load this snapshot`,
    description: $localize`:@@review.release.state.error.description:No stored decision was substituted · req-rel-503`,
    action: 'retry',
  },
  early: {
    title: $localize`:@@review.release.state.early.title:Available in G4`,
    description: $localize`:@@review.release.state.early.description:This reference is hidden in G1–G3. No assessment is available here.`,
    action: 'authoring',
  },
  stale: {
    title: $localize`:@@review.release.state.stale.title:Stale digest`,
    description: $localize`:@@review.release.state.stale.description:API evidence predates the recorded build. Its historical PASS is unchanged.`,
  },
  missing: {
    title: $localize`:@@review.release.state.missing.title:Missing component evidence`,
    description: $localize`:@@review.release.state.missing.description:Worker has no eligible run. Pending is not PASS.`,
  },
  newer: {
    title: $localize`:@@review.release.state.newer.title:Newer evidence available`,
    description: $localize`:@@review.release.state.newer.description:A newer revision exists. The release record still references the original digest and ev-2 evaluation.`,
  },
  expired: {
    title: $localize`:@@review.release.state.expired.title:Late-evidence window closed`,
    description: $localize`:@@review.release.state.expired.description:The supplied 15-minute window has expired. The pinned evaluation is retained.`,
  },
  partial: {
    title: $localize`:@@review.release.state.partial.title:Partial component snapshot`,
    description: $localize`:@@review.release.state.partial.description:No worker metadata returned for this read. Other rows are unaffected. Request ID: req-worker-503.`,
    action: 'retry',
  },
  restricted: {
    title: $localize`:@@review.release.state.restricted.title:Restricted evidence metadata`,
    description: $localize`:@@review.release.state.restricted.description:Evidence links are withheld. No preview or download is available.`,
  },
  long: {
    title: $localize`:@@review.release.state.long.title:Recorded digest detail`,
    description: $localize`:@@review.release.state.long.description:Expand a digest to inspect availability. The source supplies abbreviated values, not full hashes.`,
  },
  pending: {
    title: $localize`:@@review.release.state.pending.title:Pending evaluation`,
    description: $localize`:@@review.release.state.pending.description:No stored release evaluation was supplied. There is no aggregate verdict.`,
  },
  canceled: {
    title: $localize`:@@review.release.state.canceled.title:Canceled bound run`,
    description: $localize`:@@review.release.state.canceled.description:Separate component binding: canceled. No completed outcome or stored evaluation supplied.`,
  },
} as const satisfies Record<string, ReleaseStateNotice>;

export type ReleaseReviewState =
  keyof typeof notices | 'default' | 'loading' | 'failure' | 'waived';

export function releaseState(value: string | null): ReleaseReviewState {
  if (value === 'loading' || value === 'failure' || value === 'waived') return value;
  return value && Object.hasOwn(notices, value) ? (value as keyof typeof notices) : 'default';
}

export function releaseNotice(state: ReleaseReviewState): ReleaseStateNotice | null {
  switch (state) {
    case 'default':
    case 'loading':
    case 'failure':
    case 'waived':
      return null;
    default:
      return notices[state];
  }
}

export function showsReleaseSnapshot(state: ReleaseReviewState): boolean {
  switch (state) {
    case '412':
    case 'default':
    case 'expired':
    case 'long':
    case 'missing':
    case 'newer':
    case 'no-match':
    case 'partial':
    case 'restricted':
    case 'stale':
      return true;
    default:
      return false;
  }
}
