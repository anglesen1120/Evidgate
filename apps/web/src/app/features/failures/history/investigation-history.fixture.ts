import { HISTORY_COPY as c } from './investigation-history.copy';

interface HistoryEventViewModel {
  readonly revision: string;
  readonly time: string;
  readonly description: string;
}

export const checkoutHistory = {
  id: 'case-checkout-17',
  key: 'SHOP-TC-0042 v5',
  incident: 'inc-91',
  run: 'run-1842',
  original: 'att-1842-01',
  retry: 'att-1842-02',
  raw: 'POST /orders → HTTP 500',
  request: 'req-901',
  manifest: 'mf-7d2c…91ab',
  environment: 'build-723 · Chromium 126 · vi-VN',
  source: '8e2a91b',
  artifact: 'art-shot-92',
  events: [
    { revision: 'ev-2', time: '09:46:12 UTC', description: c.tracePending },
    { revision: 'ev-3', time: '09:47:00 UTC', description: c.attached },
    { revision: 'ev-4', time: '09:49:20 UTC', description: c.sameExecution },
  ] satisfies readonly HistoryEventViewModel[],
} as const;
export const addressHistory = {
  id: 'case-address-8',
  incident: 'inc-92',
  key: 'SHOP-TC-0043',
  classification: 'test_drift',
  captured: '2026-10-13 09:43:10 UTC',
} as const;

export const historyText = {
  snapshot: `${c.asOf} 2026-10-13 10:15 UTC · storefront-web · SHOP`,
  originalLink: `${c.original} · ${checkoutHistory.incident}`,
  addressHeading: `${c.original} · ${addressHistory.id}`,
  addressContext: `${addressHistory.key} · ${addressHistory.incident} · ${checkoutHistory.run} / ev-3 · ${addressHistory.captured}`,
  addressLink: `${c.incident} ${addressHistory.incident}`,
  attemptPath: `${checkoutHistory.id} / ${checkoutHistory.run} / checkout-vi-chromium / ${checkoutHistory.original}`,
  storedContext: `2026-10-13 09:42:06 UTC · ${checkoutHistory.request} · ${checkoutHistory.manifest}`,
  raw: `${checkoutHistory.raw} · ${checkoutHistory.request}`,
  build: `${checkoutHistory.source} · ${checkoutHistory.environment}`,
  firstAttempt: `#1 · ${checkoutHistory.original} · ${checkoutHistory.run}`,
  retryAttempt: `#2 · ${checkoutHistory.retry} · 09:45:00–09:45:02 UTC · 1,630 ms`,
  parentResult: `· ${checkoutHistory.run} / ev-3 · has_failures · ${c.decision}: FAIL`,
  rerunResult: `· run-1845 / ev-3 · has_failures · ${c.decision}: FAIL`,
  authored: `${c.authored}: Linh Nguyễn · 2026-10-12 08:40 UTC`,
  priorCase: `cr-8 · ${c.oldCase}`,
  currentCase: `cr-9 · ${c.newCase}`,
  base: `${c.base} · 9af317c`,
  checkoutLink: `${c.incident} ${checkoutHistory.incident}`,
} as const;

export const compareRows = [
  [c.test, 'failure / failed', '＝ failure / failed'],
  [c.message, c.checkout, c.sameAssertion],
  [c.set, c.beforeSet, c.afterSet],
  [c.source, checkoutHistory.source, checkoutHistory.source],
  [c.build, checkoutHistory.environment, checkoutHistory.environment],
] as const;
export const historyNotices = {
  one: [c.one, c.oneBody],
  equal: [c.equal, c.equalBody],
  changed: [c.changed, ''],
  missing: [c.missing, c.missingBody],
  partial: [c.partial, c.partialBody],
} as const;
export function historyNotice(state: string): readonly [string, string] | null {
  return Object.hasOwn(historyNotices, state)
    ? historyNotices[state as keyof typeof historyNotices]
    : null;
}
