import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { stubLayoutApis } from '../../../shared/testing/layout.testing';
import { DecisionReviewHarness } from './decision-review.harness';
import { ReleaseDecisionReviewPage } from './decision-review.page';

const examples = {
  default: 'w-28',
  loading: 'Loading stored evaluation',
  empty: 'No exception records',
  'no-match': 'No results match these filters',
  error: 'req-decision-06',
  '401': 'Your session expired',
  '403': 'Not authorized',
  '404': 'Not found',
  '412': 'pr-12 → pr-13',
  '429': '30 seconds',
  early: 'G1–G3',
  pending: 'Pending evaluation',
  missing: 'INCONCLUSIVE',
  pass: 'PASS',
  stale: 'Historical evidence · ev-2',
  shadow: 'Shadow evaluation slot',
  requested: 'Awaiting another approver',
  self: 'waiver_self_approval',
  expired: 'Terminal waiver',
  revoked: 'Revoked',
  'q-expired': 'Expired',
  'q-removed': 'Removed',
  'after-load': '403 · policy:read',
  terminal: '409 · terminal_revoke',
  critical: 'critical_case_not_quarantinable',
  'q-conflict': '409 · quarantine_conflict',
  removed404: 'Already removed',
  submitted: 'Submitting request',
  long: 'localized postal codes',
};

describe('ReleaseDecisionReviewPage', () => {
  const route = new BehaviorSubject(convertToParamMap({}));
  beforeEach(() => {
    stubLayoutApis();
    TestBed.configureTestingModule({
      imports: [ReleaseDecisionReviewPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: route } },
      ],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.unstubAllGlobals();
  });
  it.each(Object.entries(examples))('renders the %s snapshot', async (state, needle) => {
    route.next(convertToParamMap({ state }));
    const fixture = TestBed.createComponent(ReleaseDecisionReviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      DecisionReviewHarness,
    );
    const text = await harness.text();
    expect(text).toContain(needle);
    expect(text).toContain('Illustrative fixture, not live data.');
    if (state !== 'pass') expect(text).not.toMatch(/\bPASS\b/);
    if (
      [
        'early',
        '401',
        '403',
        '404',
        'error',
        'pending',
        'missing',
        'pass',
        'stale',
        '429',
        'loading',
      ].includes(state)
    ) {
      expect(text).not.toContain('shipping-estimate');
      expect(text).not.toContain('run-1842');
    }
    if (state === 'after-load') {
      expect(text).toContain('FAIL');
      expect(text).not.toContain('An Phạm');
      expect(await harness.all('app-decision-preview')).toHaveLength(0);
    }
    if (state === 'removed404') expect(await harness.all('input[value="q-19"]')).toHaveLength(0);
    if (state === 'shadow') expect(text).toContain('Original run-1842 / ev-3: FAIL');
  });
  it('reacts to the query changing in place and falls back for unknown values', async () => {
    route.next(convertToParamMap({ state: 'pass' }));
    const fixture = TestBed.createComponent(ReleaseDecisionReviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      DecisionReviewHarness,
    );
    expect(await harness.text()).toContain('PASS');
    route.next(convertToParamMap({ state: 'constructor' }));
    expect(await (await harness.find('app-evidence-badge')).text()).toContain('Incomplete');
    expect(await (await harness.find('app-outcome-pill')).text()).toContain('Has failures');
    expect(await (await harness.find('app-decision-chip')).text()).toContain('FAIL');
    expect(await harness.text()).not.toContain('PASS');
  });
});
