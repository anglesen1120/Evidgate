import { LINEAGE_COPY as c } from './knowledge-lineage.copy';

export const lineageStates = [
  'default',
  'active',
  'loading',
  'empty',
  'filtered',
  'error',
  '403',
  '404',
  '401',
  '429',
  '412',
  'reloaded',
  'revoked',
  'applied',
  'ineligible',
  'historical',
  'expired',
  'unavailable',
  'none',
  'previewloading',
  'previewerror',
  'refused',
  'submitting',
  '422',
  'prepared',
] as const;
export type LineageState = (typeof lineageStates)[number];
export interface DependentViewModel {
  readonly id: string;
  readonly hash: string;
  readonly case: string;
  readonly context: string;
  readonly status: 'proposed' | 'validated' | 'applied';
}
export const dependents: readonly DependentViewModel[] = [
  {
    id: 'cand-81',
    hash: 'ch-4b10…9d77',
    case: 'SHOP-TC-0042 v5',
    context: c.address,
    status: 'proposed',
  },
  {
    id: 'cand-82',
    hash: 'ch-77e1…2c04',
    case: 'SHOP-TC-0043 v2',
    context: c.locale,
    status: 'validated',
  },
  {
    id: 'cand-83',
    hash: 'ch-1d39…e6a8',
    case: 'SHOP-TC-0043 v2',
    context: c.retainedUse,
    status: 'applied',
  },
];
export const lineage = {
  id: 'kr-22',
  incident: 'inc-92',
  classification: 'test_drift',
  candidate: 'cand-73',
  hash: 'ch-8af2…3e20',
  fullHash: 'sha256:ch-8af2c41e73b90d11c6e8f207b5343e20',
  base: '9af317c',
  reviewer: 'Minh Trần',
  reviewedAt: '2026-10-13T10:00:00Z',
  capturedAt: '2026-10-13T09:43:10Z',
  run: 'run-1842',
  evidence: 'ev-3',
  outcome: 'has_failures',
  decision: 'FAIL',
  case: 'SHOP-TC-0043 v2',
  scope: 'checkout · storefront-web',
  manifest: 'mf-7d2c…91ab · pr-12',
  build: 'web · sha256:a72c…09de',
  revision: 'kr-22 · rev 7',
  reloadedRevision: 'kr-22 · rev 8',
  activeSummary: '2 · cand-81 / cand-82',
  historicalSummary: '1 · cand-83',
  chain: 'inc-92 → cand-73 → kr-22',
} as const;
export function dependencySnapshot(state: LineageState): 'unknown' | 'empty' | 'known' {
  if (['unavailable', 'previewloading', 'previewerror', '412', 'reloaded'].includes(state))
    return 'unknown';
  return state === 'none' ? 'empty' : 'known';
}
export function revoked(state: LineageState): boolean {
  return state === 'revoked' || state === 'refused';
}
