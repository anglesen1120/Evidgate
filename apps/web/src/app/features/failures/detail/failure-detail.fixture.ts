import { DETAIL_COPY as c } from './failure-detail.copy';

interface FailureDetailViewModel {
  readonly id: string;
  readonly caseId: string;
  readonly key: string;
  readonly classification: string;
  readonly message: string;
}

export const checkout: FailureDetailViewModel = {
  id: 'inc-91',
  caseId: 'case-checkout-17',
  key: 'SHOP-TC-0042 v5',
  classification: 'product_defect',
  message: c.checkout,
};
export const address: FailureDetailViewModel = {
  id: 'inc-92',
  caseId: 'case-address-8',
  key: 'SHOP-TC-0043',
  classification: 'test_drift',
  message: c.address,
};
export const checkoutEvidence = {
  run: 'run-1842',
  revision: 'ev-3',
  producer: 'checkout-vi-chromium',
  original: 'att-1842-01',
  retry: 'att-1842-02',
  raw: 'POST /orders → HTTP 500',
  request: 'req-901',
  manifest: 'mf-7d2c…91ab',
  build: 'pr-12 / web sha256:a72c…09de',
  screenshot: 'art-shot-92',
  screenshotMeta: 'image/png · 248 KiB · sha256:ad72…f18b',
  steps: [
    [c.place, '09:42:04 UTC'],
    [c.wait, '09:42:05 UTC'],
    [c.assertion, '09:42:06 UTC'],
  ],
} as const;

export const detailText = {
  snapshot: `${c.asOf} 2026-10-23 10:00 UTC · storefront-web · SHOP`,
  addressContext: `${address.id} · ${address.caseId} · ${address.key} · ${checkoutEvidence.run} / ${checkoutEvidence.revision}`,
  addressTime: '2026-10-13 09:43:10 UTC',
  classification: `${c.compare}: ${address.classification}. ${c.addressClass}`,
  candidateSnapshot: `cand-73 · ch-8af2…3e20 · ${c.asOf} 2026-10-13 09:59 UTC`,
  validation: `${c.sandbox} · 24/24`,
  candidateLink: `${c.candidate} cand-73`,
  discountContext: 'inc-93 · SHOP-TC-0051 v2 · test_drift',
  discountOwner: 'Linh Nguyễn · ucp-12',
  pinned: `${c.pinned} · ${checkoutEvidence.revision}`,
  attemptPath: `${checkoutEvidence.run} / ${checkoutEvidence.producer} / ${checkoutEvidence.original}`,
  caseContext: `${checkout.key} · ${checkout.caseId} ·`,
  originalTime: '2026-10-13 09:42:06 UTC',
  rawLabel: `${c.raw}:`,
  requestTiming: `· ${checkoutEvidence.request} · 1,840 ms`,
  outcomeLabel: `${c.outcome} · ${checkoutEvidence.revision}`,
  outcome: 'has_failures',
  decisionLabel: `${c.decision} · ${checkoutEvidence.revision}`,
  decision: 'FAIL',
  historyLink: `${c.history} · ${checkoutEvidence.original}`,
  logExcerpt: `09:42:05 UTC ${checkoutEvidence.raw}\nrequest_id=${checkoutEvidence.request}  elapsed=1840ms`,
  artifactsHeading: `${c.artifacts} · ${checkoutEvidence.revision}`,
  screenshotLabel: `${c.captured} · ${checkoutEvidence.screenshot}`,
  screenshotDetails: `${checkoutEvidence.screenshotMeta} · 2026-10-13 09:42:06.718 UTC`,
  trace: `${c.trace} · art-trace-1842`,
  dom: `${c.dom} · art-dom-1842`,
  scan: `${c.logScan}:`,
  firstAttempt: `#1 · ${checkoutEvidence.original}`,
  retryAttempt: `#2 · ${checkoutEvidence.retry}`,
  firstTiming: '· 09:42:06 UTC',
  retryTiming: '· 09:45:00–09:45:02 UTC · 1,630 ms',
  issue: '· INC-204',
  saveHeading: `${c.save} · ${checkout.id}`,
  closeHeading: `${c.close} · ${checkout.id}`,
  previewDescription: `${c.previewBody} · If-Match:`,
} as const;

export const detailNotices = {
  '403': [c.permission, c.readOnly],
  '412': [c.conflict, c.conflictBody],
  '409': [c.already, c.alreadyBody],
  '422': [c.invalid, c.invalidBody],
  pending: [c.pending, c.pendingBody],
  complete: [c.complete, c.completeBody],
  infra: [c.infra, c.infraBody],
  unknown: [c.unknown, c.unknownBody],
  stale: [c.stale, c.staleBody],
  missing: [c.missing, c.missingBody],
  first: [c.first, c.labelFirst],
  submitting: [c.submitting, c.submittingBody],
  success: [c.success, c.successBody],
  closed: [c.closed, c.closedBody],
  latest: [c.latest, c.latestBody],
  v1: [c.noProposal, c.notEligible],
  nomatch: [c.nomatch, c.clear],
} as const;

export function detailNotice(state: string): readonly [string, string] | null {
  return Object.hasOwn(detailNotices, state)
    ? detailNotices[state as keyof typeof detailNotices]
    : null;
}
