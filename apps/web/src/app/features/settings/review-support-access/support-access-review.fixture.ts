import { copy } from './support-access-review.copy';

export const grantStatuses = ['requested', 'approved', 'expired', 'revoked'] as const;
export type GrantStatus = (typeof grantStatuses)[number];
export const supportStates = [
  'ready',
  'loading',
  'empty',
  'filtered',
  'error',
  'denied',
  '401',
  '403',
  '404',
  '412',
  '429',
  'pending',
  'long',
  'approved',
  'expired',
  'revoked',
  'readonly',
  'self',
  'stale',
  'gone',
  'banner',
] as const;
export type SupportState = (typeof supportStates)[number];
export interface GrantViewModel {
  readonly ref: string;
  readonly subject: string;
  readonly operator: string | null;
  readonly project: string | null;
  readonly status: GrantStatus;
  readonly scope: string | null;
  readonly reason: string;
  readonly until: string | null;
  readonly requester: string | null;
  readonly requestedAt: string | null;
  readonly requestRef: string | null;
}
export interface SupportAccessViewModel {
  readonly state: SupportState;
  readonly organization: string;
  readonly asOf: string;
  readonly grants: readonly GrantViewModel[];
  readonly approver: string;
  readonly selectedRef: string;
}
export const supportAccessFixture: SupportAccessViewModel = {
  state: 'ready',
  organization: 'Lotus Commerce',
  asOf: '2026-10-13T10:42:00Z',
  approver: 'Hà Lê',
  selectedRef: 'sg-41',
  grants: [
    {
      ref: 'sg-41',
      subject: 'INC-204',
      operator: 'Support-07',
      project: 'storefront-web',
      status: 'requested',
      scope: copy.metadata,
      reason: copy.incident,
      until: '2026-10-13T11:00:00Z',
      requester: 'user-op-7',
      requestedAt: '2026-10-13T10:42:00Z',
      requestRef: 'req-sg-41',
    },
    {
      ref: 'sg-40',
      subject: 'evidgate-e2e',
      operator: 'Support-03',
      project: 'evidgate-e2e',
      status: 'expired',
      scope: copy.runMetadata,
      reason: copy.historical,
      until: '2026-10-12T18:00:00Z',
      requester: null,
      requestedAt: null,
      requestRef: null,
    },
    {
      ref: 'sg-39',
      subject: copy.resolved,
      operator: null,
      project: null,
      status: 'revoked',
      scope: null,
      reason: copy.resolved,
      until: null,
      requester: null,
      requestedAt: null,
      requestRef: null,
    },
  ],
};
export const longGrant: GrantViewModel = {
  ref: 'sg-38',
  subject: copy.longScope,
  operator: 'Support-07',
  project: 'storefront-web-checkout-international-payment-reconciliation',
  status: 'requested',
  scope: copy.metadata,
  reason: copy.longScope,
  until: '2026-10-13T11:00:00Z',
  requester: null,
  requestedAt: null,
  requestRef: null,
};
