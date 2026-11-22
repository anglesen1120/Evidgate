import { MERGE_COPY as c } from './studio-review-merge.copy';

const states = [
  'default',
  'loading',
  'empty',
  'error',
  '401',
  '403',
  '404',
  '412',
  '429',
  'nodiff',
  'unchanged',
  'long',
  'failed',
  'requested',
  'open',
  'approved',
  'eligible',
  'withdrawn',
  'closed',
  'merged',
  'prout',
  'conflict',
  'awaiting-review',
  'candidate-approved',
  'reloaded',
] as const;
export type StudioReviewState = (typeof states)[number];
export type ReviewAction = 'submit' | 'approve' | 'changes' | 'merge';

interface Fact {
  readonly label: string;
  readonly value: string;
  readonly identifier?: boolean;
}
interface Line {
  readonly number: string;
  readonly source: string;
  readonly added?: boolean;
}
interface Pane {
  readonly label: string;
  readonly revision: string;
  readonly lines: readonly Line[];
}
interface Decision {
  readonly reviewer: string;
  readonly hash: string;
  readonly at: string | null;
}

export interface StudioReviewMergeViewModel {
  readonly state: StudioReviewState;
  readonly references: typeof references;
  readonly sandboxOutcome: 'dry_failed' | 'dry_passed' | null;
  readonly record: 'studio' | 'candidate';
  readonly identity: readonly Fact[];
  readonly panes: readonly Pane[];
  readonly hashes: string;
  readonly hash: string;
  readonly reviewer: string;
  readonly fullPatch: string;
  readonly patchEmpty: string | null;
  readonly decision: Decision | null;
  readonly validated: boolean;
  readonly prStatus: string | null;
  readonly prUrlMissing: boolean;
  readonly headerAction: 'submit' | 'merge' | null;
  readonly inspectActions: boolean;
  readonly notice: string | null;
}

const reviewer = 'Minh Trần';
const author = 'Linh Nguyễn';
const baseHash = 'ah-92fd…6241';
const draftHash = 'ah-1f9b…473a';
const oldHash = 'ch-8af2…3e20';
const replacementHash = 'ch-91d4…63fc';
const references = {
  project: 'SHOP · p-storefront',
  reviewerInitials: 'MT',
  reviewScope: 'draft:merge_request',
  repository: 'lotus-commerce/storefront',
  pullRequest: '#128',
  sandbox: 'dry-114 · dr-5',
  originalRun: 'run-1842 / ev-3 · inc-92',
  originalOutcome: 'has_failures',
  originalDecision: 'FAIL',
  replacement: `${oldHash} → ${replacementHash}`,
} as const;
const receipt = "const receipt = page.getByTestId('order-receipt');";
const visible = 'await expect(receipt).toBeVisible();';
const total = 'await expect(receipt).toContainText(orderTotal);';
const added = 'await expect(receipt).toContainText(orderId);';
const context = [
  { number: '22', source: receipt },
  { number: '23', source: visible },
];
const studioPanes: readonly Pane[] = [
  {
    label: c.base,
    revision: 'cr-9',
    lines: [...context, { number: '—', source: '' }, { number: '24', source: total }],
  },
  {
    label: c.candidate,
    revision: 'dr-5',
    lines: [
      ...context,
      { number: '+24', source: added, added: true },
      { number: '25', source: total },
    ],
  },
];
const studioSource = [
  "test('checkout receipt shows the total', async ({ page }) => {",
  "  await page.goto('/checkout');",
  "  await page.getByTestId('place-order').click();",
  ...[receipt, visible, added, total].map((line) => `  ${line}`),
  '});',
].join('\n');
const locatorPanes: readonly Pane[] = [
  {
    label: c.base,
    revision: '9af317c',
    lines: [{ number: '−', source: "getByTestId('delivery-address-required')" }],
  },
  {
    label: c.candidateLabel,
    revision: oldHash,
    lines: [
      {
        number: '+',
        source: "getByRole('textbox', { name: 'Required delivery address' })",
        added: true,
      },
    ],
  },
];
const proposed: Partial<Record<StudioReviewState, string>> = {
  requested: c.prRequested,
  open: c.prOpen,
  approved: c.prApproved,
  eligible: c.prEligible,
  closed: c.prClosed,
  withdrawn: c.prWithdrawn,
  merged: c.prMerged,
  prout: c.prUnavailable,
};
const notices: Partial<Record<StudioReviewState, string>> = {
  '403': c.forbidden,
  '412': c.stale,
  '429': c.limited,
  failed: c.failed,
  eligible: c.eligible,
  conflict: c.conflictNote,
  'candidate-approved': c.sampleDecision,
};

function studio(state: StudioReviewState): StudioReviewMergeViewModel {
  const current = state === 'reloaded';
  const decision = ['approved', 'eligible', 'merged'].includes(state)
    ? { reviewer, hash: draftHash, at: null }
    : null;
  const patchEmpty = current
    ? c.reloaded
    : state === 'nodiff'
      ? c.noDiff
      : state === 'unchanged'
        ? c.unchanged
        : null;
  return {
    state,
    record: 'studio',
    references,
    sandboxOutcome: state === 'failed' ? 'dry_failed' : 'dry_passed',
    reviewer,
    decision,
    fullPatch: studioSource,
    hash: draftHash,
    hashes: `${baseHash} → ${draftHash}`,
    panes: studioPanes,
    patchEmpty,
    identity: [
      { label: c.caseLabel, value: 'case-checkout-17', identifier: true },
      { label: c.draft, value: 'd-114', identifier: true },
      { label: c.author, value: author },
      { label: c.base, value: current ? 'cr-10' : 'cr-9 / 9af317c', identifier: true },
      { label: c.candidate, value: current ? 'dr-6' : 'dr-5', identifier: true },
      ...(!current ? [{ label: c.sandbox, value: 'dry-114', identifier: true }] : []),
    ],
    validated: !['failed', '412', 'reloaded', 'nodiff', 'unchanged'].includes(state),
    prStatus: proposed[state] ?? c.prProposed,
    prUrlMissing: ['open', 'approved', 'eligible', 'merged', 'closed'].includes(state),
    headerAction:
      state === 'eligible'
        ? 'merge'
        : ['default', 'long', 'withdrawn'].includes(state)
          ? 'submit'
          : null,
    inspectActions: ['default', 'long', 'requested', 'open', 'withdrawn'].includes(state),
    notice: notices[state] ?? null,
  };
}

function candidate(
  state: 'conflict' | 'awaiting-review' | 'candidate-approved',
): StudioReviewMergeViewModel {
  const conflict = state === 'conflict';
  const hash = conflict ? replacementHash : oldHash;
  return {
    references,
    sandboxOutcome: null,
    state,
    record: 'candidate',
    reviewer,
    hash,
    hashes: `9af317c → ${hash}`,
    identity: [
      { label: c.caseLabel, value: 'case-address-8 / SHOP-TC-0043', identifier: true },
      { label: c.candidateLabel, value: 'cand-73', identifier: true },
      { label: c.author, value: author },
      { label: c.base, value: '9af317c', identifier: true },
      { label: c.revision, value: conflict ? '8' : '7', identifier: true },
      { label: c.reviewedHash, value: hash, identifier: true },
    ],
    panes: conflict ? [] : locatorPanes,
    fullPatch: '',
    patchEmpty: conflict ? c.replacementDiff : null,
    decision:
      state === 'candidate-approved' ? { reviewer, hash, at: '2026-10-13T10:00:00Z' } : null,
    validated: !conflict,
    prStatus: null,
    prUrlMissing: false,
    headerAction: null,
    inspectActions: state === 'awaiting-review',
    notice: notices[state] ?? null,
  };
}

export function mergeReviewView(raw: string | null): StudioReviewMergeViewModel {
  const state = states.find((known) => known === raw) ?? 'default';
  return state === 'conflict' || state === 'candidate-approved' || state === 'awaiting-review'
    ? candidate(state)
    : studio(state);
}
