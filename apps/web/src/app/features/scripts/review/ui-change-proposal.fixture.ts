import type { ReviewMetricViewModel } from './scripts-review-state';
import type { UiChangeProposalState } from './ui-change-proposal.states';
import { proposalCopy as c } from './ui-change-proposal.copy';

export interface UiChangeProposalViewModel {
  readonly status: UiChangeProposalState;
  readonly project: string;
  readonly asOf: string;
  readonly metrics: readonly ReviewMetricViewModel[];
  readonly id: string;
  readonly incident: string;
  readonly run: string;
  readonly revision: string;
  readonly incidentClass: string;
  readonly manualCase: string;
  readonly approvedVersion: string;
  readonly baseVersion: string;
  readonly currentVersion: string;
  readonly author: string;
  readonly reviewer: string;
  readonly originalResult: string;
  readonly assertion: string;
  readonly attemptEvidence: null;
  readonly completeness: 'incomplete';
  readonly outcome: 'has_failures';
  readonly storedDecision: 'FAIL';
  readonly sourceStep: string;
  readonly frozen: string;
  readonly profile: string;
  readonly build: string;
  readonly comparison: readonly {
    readonly field: string;
    readonly approved: string;
    readonly proposed: string;
  }[];
}
export const uiChangeProposalFixture: UiChangeProposalViewModel = {
  status: 'ready',
  project: 'storefront-web · SHOP · p-storefront',
  asOf: '2026-10-13T09:59:00Z',
  metrics: [
    { title: c.proposal, value: 'ucp-12', detail: c.manualReview, icon: 'lucideActivity' },
    { title: c.sourceIncident, value: 'inc-93', detail: c.uiChange, icon: 'lucideInfo' },
    { title: c.approvedBase, value: 'v2', detail: c.approvedCase, icon: 'lucideCircleCheck' },
    { title: c.sourceRun, value: 'run-1842', detail: c.incidentContext, icon: 'lucideActivity' },
  ],
  id: 'ucp-12',
  incident: 'inc-93',
  run: 'run-1842',
  revision: 'ev-3',
  manualCase: 'SHOP-TC-0051',
  approvedVersion: 'v2',
  baseVersion: 'v2',
  currentVersion: 'v3',
  author: 'Linh Nguyễn',
  reviewer: 'Minh Trần',
  incidentClass: 'test_drift',
  originalResult: 'failure → failed',
  assertion: "expect(getByTestId('discount-field')).toBeVisible() → received: hidden",
  attemptEvidence: null,
  completeness: 'incomplete',
  outcome: 'has_failures',
  storedDecision: 'FAIL',
  sourceStep: '7 · 9af317c',
  frozen: '2026-10-13T09:47:00Z',
  profile: 'mf-7d2c…91ab · pr-12 · spec v3',
  build: 'sha256:a72c…09de',
  comparison: [
    { field: c.action, approved: c.enter, proposed: c.openField },
    { field: c.expected, approved: c.visible, proposed: c.accepts },
    { field: c.data, approved: c.redacted, proposed: c.unchanged },
  ],
};
