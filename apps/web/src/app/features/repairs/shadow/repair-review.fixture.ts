export interface CandidateRevisionViewModel {
  readonly revision: number;
  readonly hash: string;
  readonly validation: { readonly sample: string; readonly shadowRun: string } | null;
}

export const REPAIR = {
  id: 'cand-73',
  incident: 'inc-92',
  classification: 'test_drift',
  run: 'run-1842',
  evidence: 'ev-3',
  caseId: 'case-address-8',
  manual: 'SHOP-TC-0043 · v2',
  completeness: 'incomplete',
  outcome: 'has_failures',
  decision: 'FAIL',
  raw: 'failure / failed',
  captured: '2026-10-13T09:43:10Z',
  base: '9af317c',
  author: 'Linh Nguyễn',
  reviewer: 'Minh Trần',
  role: 'test_maintainer',
  approvalPermission: 'healing:approve',
  revisionHeader: 'If-Match',
  expires: '2026-10-14T09:00Z',
  before: "getByTestId('delivery-address-required')",
  after: "getByRole('textbox', { name: 'Required delivery address' })",
  assertion: "expect(deliveryAddressInput).toHaveValue('12 Sample Street')",
  assertionHash: 'ah-92fd…6241',
  fullHash: 'sha256:ch-8af2c41e73b90d11c6e8f207b5343e20',
  knowledge: 'kr-22',
  manifest: 'mf-7d2c…91ab · pr-12',
  build: 'web · sha256:a72c…09de',
  domBefore: 'div[data-testid="delivery-address-required"] → input[aria-label="[redacted]"]',
  domAfter: 'fieldset → label: Required delivery address → input[type=text][aria-required=true]',
} as const;

export const REVIEWED: CandidateRevisionViewModel = {
  revision: 7,
  hash: 'ch-8af2…3e20',
  validation: { sample: '24/24', shadowRun: 'sh-73' },
};
export const REPLACEMENT: CandidateRevisionViewModel = {
  revision: 8,
  hash: 'ch-91d4…63fc',
  validation: null,
};
