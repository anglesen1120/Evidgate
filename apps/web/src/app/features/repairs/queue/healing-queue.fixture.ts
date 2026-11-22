import { QUEUE_COPY as c } from './healing-queue.copy';

export interface QueueCandidateViewModel {
  readonly id: string;
  readonly hash: string;
  readonly title: string;
  readonly kind: 'locator' | 'wait';
  readonly failure: string;
  readonly failureRef: string;
  readonly createdAt: string | null;
  readonly samples: { readonly passed: number; readonly total: number } | null;
  readonly validated: boolean;
  readonly linkable: boolean;
}

export const SNAPSHOT = '2026-10-13T09:59:00Z';
export const SLA_SPECIMEN = '2026-10-14T09:59:00Z';
export const ADDRESS = {
  id: 'cand-73',
  hash: 'ch-8af2…3e20',
  title: c.address,
  kind: 'locator',
  failure: c.addressFailure,
  failureRef: 'inc-92 · run-1842 / ev-3',
  createdAt: '2026-10-13T09:50:00Z',
  samples: { passed: 24, total: 24 },
  validated: true,
  linkable: true,
} as const satisfies QueueCandidateViewModel;

const decision = 'FAIL';
const evidenceRevision = 'ev-3';
export const QUEUE_CONTEXT = {
  project: 'storefront-web · SHOP · p-storefront',
  addressIdentity: `SHOP-TC-0043 · ${c.revision} 7 · ${ADDRESS.hash}`,
  replacementIdentity: `${ADDRESS.id} · ch-91d4…63fc`,
  recordedShadow: `${ADDRESS.id} · ${ADDRESS.hash} · ${ADDRESS.samples.passed}/${ADDRESS.samples.total}`,
  outcome: 'has_failures',
  decision,
  decisionRevision: `· ${evidenceRevision}`,
  storedDecision: `${decision} · ${evidenceRevision}`,
  incident: 'inc-92',
  failureAt: '2026-10-13T09:43:10Z',
  validatedAt: '2026-10-13T09:58:00Z',
  author: 'Linh Nguyễn',
  reviewer: 'Minh Trần',
} as const;

export const CANDIDATES: readonly QueueCandidateViewModel[] = [
  ADDRESS,
  {
    id: 'cand-77',
    hash: 'ch-27ca…512e',
    title: c.cart,
    kind: 'wait',
    failure: c.cartFailure,
    failureRef: 'inc-96 · run-1849',
    createdAt: null,
    samples: { passed: 8, total: 8 },
    validated: true,
    linkable: false,
  },
  {
    id: 'cand-74',
    hash: 'ch-51de…094f',
    title: c.label,
    kind: 'locator',
    failure: c.labelFailure,
    failureRef: 'run-1842',
    createdAt: null,
    samples: null,
    validated: false,
    linkable: false,
  },
  {
    id: 'cand-75',
    hash: 'ch-3c42…8591',
    title: c.searchCase,
    kind: 'locator',
    failure: c.searchFailure,
    failureRef: 'inc-95 · run-1851',
    createdAt: null,
    samples: null,
    validated: false,
    linkable: false,
  },
];

export function eligibleForReview(record: QueueCandidateViewModel): boolean {
  return record.validated && (record.samples?.total ?? 0) > 0;
}

export function ageMinutes(record: QueueCandidateViewModel, asOf: string): number | null {
  return record.createdAt === null
    ? null
    : Math.floor((Date.parse(asOf) - Date.parse(record.createdAt)) / 60_000);
}

export const BATCHES = [2, 4, 3, 6, 4, 5] as const;
export const BEFORE = "getByTestId('delivery-address-required')";
export const AFTER = "getByRole('textbox', { name: 'Required delivery address' })";
