import type { StatusTone } from '../../../shared/ui/chips';
import type {
  Completeness,
  DecisionValue,
  Outcome,
} from '../../../shared/ui/verdict/verdict-values';
import { decisionCopy as c } from './decision-review.copy';

const states = [
  'default',
  'loading',
  'empty',
  'no-match',
  'error',
  '401',
  '403',
  '404',
  '412',
  '429',
  'early',
  'pending',
  'missing',
  'pass',
  'stale',
  'shadow',
  'requested',
  'self',
  'expired',
  'revoked',
  'q-expired',
  'q-removed',
  'after-load',
  'terminal',
  'critical',
  'q-conflict',
  'removed404',
  'submitted',
  'long',
] as const;
export type DecisionState = (typeof states)[number];
export type PreviewAction = 'request' | 'approve' | 'decline' | 'revoke' | 'remove' | 'add';
type RecordKey = 'w-28' | 'q-19' | 'checkout-vi-chromium' | 'w-29' | 'w-27';
export interface ExceptionRecord {
  readonly id: RecordKey;
  readonly kind: string;
  readonly status: string;
  readonly tone: StatusTone;
  readonly scope: string;
  readonly owner: string;
  readonly expiry: string;
  readonly reason: string;
}
interface Assessment {
  readonly title: string;
  readonly completeness?: Completeness;
  readonly outcome?: Outcome;
  readonly outcomeNote?: string;
  readonly decision: DecisionValue | 'unavailable' | 'pending';
  readonly availability?: string;
}
interface Notice {
  readonly title: string;
  readonly body: string;
  readonly action?: string;
}
export interface ReleaseDecisionViewModel {
  readonly state: DecisionState;
  readonly live: boolean;
  readonly assessment: Assessment | undefined;
  readonly notice: Notice | undefined;
  readonly records: readonly ExceptionRecord[];
  readonly initialRecord: RecordKey;
  readonly allowPreview: boolean;
}
export const runFacts = {
  completeness: 'incomplete',
  outcome: 'has_failures',
  decision: 'FAIL',
  policy: 'pr-12',
  shipping: 'shipping-estimate',
  address: 'address-label',
  expiry: '2026-10-15T00:00Z',
  requester: 'Linh Nguyễn',
  approver: 'An Phạm',
  reasons: [
    ['required_assertion_failed', c.attempt],
    ['missing_shard', c.artifacts],
  ],
  inputs: [
    [c.manifest, 'mf-7d2c…91ab'],
    [c.revision, 'ev-3'],
    [c.mode, c.live],
    [c.policyHash, c.shortPolicyHash],
    [c.exceptionsHash, 'ex-763a…fc21'],
    [c.asOf, '2026-10-13T09:47Z'],
  ],
} as const;
const approved: ExceptionRecord = {
  id: 'w-28',
  kind: c.waiver,
  status: c.approved,
  tone: 'success',
  scope: runFacts.shipping,
  owner: runFacts.approver,
  expiry: runFacts.expiry,
  reason: c.reasonShipping,
};
const quarantine: ExceptionRecord = {
  id: 'q-19',
  kind: c.quarantine,
  status: c.active,
  tone: 'info',
  scope: runFacts.address,
  owner: 'Minh Trần',
  expiry: '2026-10-20T00:00Z',
  reason: c.drift,
};
const checkout: ExceptionRecord = {
  id: 'checkout-vi-chromium',
  kind: c.criticalCase,
  status: c.failed,
  tone: 'danger',
  scope: 'checkout-vi-chromium',
  owner: '—',
  expiry: '—',
  reason: c.assertion,
};
const requested: ExceptionRecord = {
  ...approved,
  id: 'w-29',
  kind: c.waiverRequest,
  status: c.requested,
  tone: 'neutral',
  owner: runFacts.requester,
  reason: c.reasonRequest,
};
const terminal: ExceptionRecord = {
  id: 'w-27',
  kind: c.terminalWaiver,
  scope: 'order-total',
  status: c.expired,
  tone: 'neutral',
  owner: runFacts.requester,
  expiry: '2026-10-10T00:00Z',
  reason: c.terminalReason,
};
const assessments: Partial<Record<DecisionState, Assessment>> = {
  pending: { title: c.pending, availability: c.notSupplied, decision: 'pending' },
  missing: {
    title: c.missing,
    completeness: 'incomplete',
    outcomeNote: c.noFailure,
    decision: 'INCONCLUSIVE',
  },
  pass: {
    title: c.completeRecord,
    completeness: 'complete',
    outcome: 'all_passed',
    decision: 'PASS',
  },
  stale: { title: c.stale, availability: c.unavailable, decision: 'unavailable' },
  shadow: { title: c.shadow, availability: c.notSupplied, decision: 'pending' },
};
const notices: Partial<Record<DecisionState, Notice>> = {
  error: { title: c.error, body: c.errorHelp, action: c.retry },
  '401': { title: c.session, body: c.sessionHelp },
  '403': { title: c.forbidden, body: c.forbiddenHelp },
  '404': { title: c.notFound, body: c.notFoundHelp },
  '429': { title: c.limited, body: c.limitedHelp, action: c.retry },
  early: { title: c.early, body: c.earlyHelp },
  empty: { title: c.empty, body: c.emptyHelp },
  'no-match': { title: c.noMatch, body: c.noMatchHelp, action: c.clear },
  '412': { title: c.conflict, body: c.conflictHelp, action: c.reload },
  self: { title: c.self, body: c.selfHelp },
  requested: { title: c.requestId, body: c.requestedHelp },
  terminal: { title: c.terminal, body: c.terminalHelp },
  critical: { title: c.critical, body: c.criticalHelp },
  'q-conflict': { title: c.qConflict, body: c.qConflictHelp },
  removed404: { title: c.removed404, body: c.removed404Help },
  'after-load': { title: c.afterLoad, body: c.afterLoadHelp },
  long: { title: c.incident, body: c.longReason },
};
const withoutRun: Partial<Record<DecisionState, true>> = {
  loading: true,
  '401': true,
  '403': true,
  '404': true,
  '429': true,
  early: true,
  error: true,
};
const withoutQueue: Partial<Record<DecisionState, true>> = {
  empty: true,
  'no-match': true,
  'after-load': true,
};
function recordsFor(state: DecisionState): readonly ExceptionRecord[] {
  const records = [state === 'long' ? { ...approved, reason: c.longReason } : approved];
  if (state !== 'removed404')
    records.push(
      state === 'q-expired' || state === 'q-removed'
        ? { ...quarantine, status: state === 'q-expired' ? c.expired : c.removed, tone: 'neutral' }
        : quarantine,
    );
  records.push(checkout);
  if (state === 'requested' || state === 'self') records.push(requested);
  if (state === 'expired' || state === 'terminal' || state === 'revoked')
    records.push(state === 'revoked' ? { ...terminal, status: c.revoked } : terminal);
  return records;
}
function selectedFor(state: DecisionState): RecordKey {
  switch (state) {
    case 'self':
    case 'requested':
      return requested.id;
    case 'expired':
    case 'terminal':
    case 'revoked':
      return terminal.id;
    case 'q-removed':
    case 'q-expired':
    case 'q-conflict':
      return quarantine.id;
    case 'critical':
      return checkout.id;
    default:
      return approved.id;
  }
}
export function decisionFixture(value: string | null): ReleaseDecisionViewModel {
  const state = states.find((s) => s === value) ?? 'default';
  const assessment = assessments[state];
  const live = !assessment && !withoutRun[state];
  const showRecords = live && !withoutQueue[state];
  return {
    state,
    live,
    assessment,
    notice: notices[state],
    records: showRecords ? recordsFor(state) : [],
    initialRecord: selectedFor(state),
    allowPreview: showRecords && state !== 'submitted',
  };
}
