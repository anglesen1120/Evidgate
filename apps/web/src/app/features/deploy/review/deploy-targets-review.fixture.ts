import { copy } from './deploy-targets-review.copy';

export const deployStates = [
  'ready',
  'loading',
  'empty',
  'no-match',
  'error',
  'denied',
  '401',
  '404',
  '429',
  '412',
  'submitting',
  'success',
  'validation',
  'unavailable',
  'stale',
  'read-only',
  'long',
  'empty-history',
  'no-policy-read',
] as const;
export type DeployState = (typeof deployStates)[number];
export interface DeployTargetViewModel {
  readonly key: string;
  readonly name: string;
  readonly order: number;
  readonly revision: string;
  readonly policy: string | null;
  readonly policyHash: string | null;
}
export interface PromotionCheckViewModel {
  readonly ref: string;
  readonly targetKey: string;
  readonly checkedAt: string | null;
  readonly release: string;
  readonly result: 'blocked' | 'pending' | 'allowed';
  readonly decisionRef: string | null;
  readonly policy: string;
  readonly reason: string;
}
export interface DeployTargetsViewModel {
  readonly state: DeployState;
  readonly project: string;
  readonly code: string;
  readonly projectRef: string;
  readonly asOf: string;
  readonly selectedKey: string;
  readonly targets: readonly DeployTargetViewModel[];
  readonly checks: readonly PromotionCheckViewModel[];
  readonly releases: readonly string[];
}
export const deployTargetsFixture: DeployTargetsViewModel = {
  state: 'ready',
  project: 'storefront-web',
  code: 'SHOP',
  projectRef: 'p-storefront',
  asOf: '2026-10-13T11:42:00Z',
  selectedKey: 'production',
  targets: [
    {
      key: 'staging',
      name: copy.staging,
      order: 1,
      revision: 'dt-6',
      policy: null,
      policyHash: null,
    },
    {
      key: 'preproduction',
      name: copy.preproduction,
      order: 2,
      revision: 'dt-6',
      policy: 'pr-12',
      policyHash: 'ph-114a…a38e',
    },
    {
      key: 'production',
      name: copy.production,
      order: 3,
      revision: 'dt-6',
      policy: 'pr-13',
      policyHash: 'ph-51c9…a771',
    },
  ],
  checks: [
    {
      ref: 'pc-91',
      targetKey: 'production',
      checkedAt: '2026-10-13T11:42:00Z',
      release: 'rel-2026.10.13',
      result: 'blocked',
      decisionRef: 'rd-14',
      policy: 'pr-13',
      reason: copy.blockedReason,
    },
    {
      ref: 'pc-90',
      targetKey: 'production',
      checkedAt: null,
      release: 'rel-2026.10.13',
      result: 'pending',
      decisionRef: null,
      policy: 'pr-13',
      reason: copy.pendingReason,
    },
    {
      ref: 'pc-89',
      targetKey: 'production',
      checkedAt: '2026-10-12T18:00:00Z',
      release: 'rel-2026.10.12',
      result: 'allowed',
      decisionRef: 'rd-13',
      policy: 'pr-12',
      reason: copy.allowedReason,
    },
  ],
  releases: ['rel-2026.10.13', 'rel-2026.10.12', 'rel-2026.10.11'],
};
