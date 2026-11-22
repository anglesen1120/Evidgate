import { INBOX_COPY as c } from './failure-inbox.copy';

export type FailureLabel =
  'product_defect' | 'test_drift' | 'environment' | 'data_issue' | 'unknown';
export type InboxOwner = 'unassigned' | 'minh' | 'linh';
export type IncidentId = 'inc-91' | 'inc-92' | 'inc-93' | 'inc-94' | 'inc-95' | 'inc-96';
export interface IncidentViewModel {
  readonly id: IncidentId;
  readonly title: string;
  readonly label: FailureLabel;
  readonly drift: 'none' | 'locator' | 'ui' | 'ambiguous';
  readonly owner: InboxOwner;
  readonly severity: 'critical' | 'high' | 'medium';
  readonly status: 'open' | 'triaged';
  readonly context: 'ready' | 'partial' | 'incomplete';
  readonly flaky: 'yes' | 'no';
  readonly source: 'confirmed' | 'rule' | 'pending';
  readonly revision: string;
  readonly captured?: string;
  readonly caseRef?: string;
  readonly reason: string;
  readonly next: string;
}

// inc-93 through inc-96 are fixture-only examples from the approved inbox. They exercise
// additional filters and pending context without inventing a run, attempt or candidate.
export const INCIDENTS: readonly IncidentViewModel[] = [
  {
    id: 'inc-91',
    title: c.checkout,
    label: 'product_defect',
    drift: 'none',
    owner: 'unassigned',
    severity: 'critical',
    status: 'open',
    context: 'ready',
    flaky: 'no',
    source: 'rule',
    revision: 'ev-3',
    captured: '2026-10-13T09:42:06Z',
    caseRef: 'SHOP-TC-0042 v5 · case-checkout-17',
    reason: c.whyCheckout,
    next: c.noRepair,
  },
  {
    id: 'inc-92',
    title: c.address,
    label: 'test_drift',
    drift: 'locator',
    owner: 'minh',
    severity: 'high',
    status: 'triaged',
    context: 'partial',
    flaky: 'no',
    source: 'confirmed',
    revision: 'ev-3',
    captured: '2026-10-13T09:43:10Z',
    caseRef: 'SHOP-TC-0043 · case-address-8',
    reason: c.whyAddress,
    next: c.candidate,
  },
  {
    id: 'inc-93',
    title: c.discount,
    label: 'test_drift',
    drift: 'ui',
    owner: 'linh',
    severity: 'medium',
    status: 'triaged',
    context: 'ready',
    flaky: 'no',
    source: 'confirmed',
    revision: 'ev-3',
    caseRef: 'SHOP-TC-0051 v2',
    reason: c.whyDiscount,
    next: c.reviewCase,
  },
  {
    id: 'inc-94',
    title: c.seed,
    label: 'environment',
    drift: 'none',
    owner: 'linh',
    severity: 'medium',
    status: 'open',
    context: 'incomplete',
    flaky: 'no',
    source: 'confirmed',
    revision: 'ev-1',
    reason: c.whySeed,
    next: c.fixSeed,
  },
  {
    id: 'inc-95',
    title: c.cart,
    label: 'data_issue',
    drift: 'none',
    owner: 'minh',
    severity: 'high',
    status: 'open',
    context: 'ready',
    flaky: 'no',
    source: 'confirmed',
    revision: 'ev-2',
    reason: c.whyCart,
    next: c.fixCart,
  },
  {
    id: 'inc-96',
    title: c.pendingTitle,
    label: 'unknown',
    drift: 'ambiguous',
    owner: 'unassigned',
    severity: 'medium',
    status: 'open',
    context: 'incomplete',
    flaky: 'yes',
    source: 'pending',
    revision: 'ev-3',
    reason: c.whyMobile,
    next: c.compareMobile,
  },
];

export const AS_OF = '2026-10-23T10:00:00Z';
export const PROJECT_CONTEXT = ' · storefront-web · SHOP';
const frozenRun = 'run-1842 / ev-3';
export const RUN_EVIDENCE = {
  binding: frozenRun,
  completeness: `${c.completeness}: ${c.incomplete} · shard-3`,
  outcomeLabel: `${c.testOutcome}:`,
  outcome: 'has_failures',
  decisionLabel: `${c.decision}:`,
  decision: 'FAIL',
  producer: `${frozenRun} · ${c.partial}`,
} as const;
export const TRIAGE_PRECONDITION = { original: 'If-Match: 7', updated: 'If-Match: 8' } as const;
export const ORIGINAL = 'att-1842-01 · POST /orders → HTTP 500 · req-901';
export const CANDIDATE = '1 · cand-73';
export const LABELS = [
  'product_defect',
  'test_drift',
  'environment',
  'data_issue',
  'unknown',
] as const;
export const FILTERS = [
  { key: 'label', title: c.failureLabel, choices: LABELS },
  { key: 'drift', title: c.drift, choices: ['locator', 'ui'] },
  { key: 'owner', title: c.owner, choices: ['unassigned', 'minh', 'linh'] },
  { key: 'severity', title: c.severity, choices: ['critical', 'high', 'medium'] },
  { key: 'flaky', title: c.flaky, choices: ['yes', 'no'] },
] as const;
export type FilterKey = (typeof FILTERS)[number]['key'] | 'q' | 'status';
