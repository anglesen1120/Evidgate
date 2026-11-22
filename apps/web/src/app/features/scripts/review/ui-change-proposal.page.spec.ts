import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { UiChangeProposalPage } from './ui-change-proposal.page';

class ScreenHarness extends ComponentHarness {
  static hostSelector = 'app-ui-change-proposal-review';
  readonly headings = this.locatorForAll('h1');
  readonly title = this.locatorFor('h1');
  readonly text = async () => (await this.host()).text();
  readonly evidence = this.locatorFor('app-evidence-badge');
  readonly outcome = this.locatorFor('app-outcome-pill');
  readonly decision = this.locatorFor('app-decision-chip');
  readonly waiting = this.locatorFor('[data-review-status]');
  readonly clock = this.locatorFor('[data-review-status] hlm-icon');
  readonly comparison = this.locatorForOptional('[data-comparison]');
  readonly actions = this.locatorForAll('button[hlmDialogTrigger]');
}

describe('UiChangeProposalPage', () => {
  provideLayoutTestStubs();
  const params = new BehaviorSubject(convertToParamMap({}));
  beforeEach(() => {
    params.next(convertToParamMap({}));
    TestBed.configureTestingModule({
      imports: [UiChangeProposalPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: params } },
      ],
    });
  });
  async function mount(state = 'ready') {
    params.next(convertToParamMap({ state }));
    const fixture = TestBed.createComponent(UiChangeProposalPage);
    fixture.detectChanges();
    const screen = await TestbedHarnessEnvironment.harnessForFixture(fixture, ScreenHarness);
    return { fixture, screen };
  }
  it.each([
    ['loading', 'Loading'],
    ['error', 'Proposal could not be refreshed'],
    ['401', 'Your session expired'],
    ['429', 'Too many requests'],
    ['404', 'Not found'],
    ['empty', 'No proposal'],
    ['queueing', 'Queueing'],
    ['queued', 'Queued in pending changes'],
    ['duplicate', 'Already queued'],
    ['rejected', 'Rejected · reason retained'],
    ['stale', 'Stale base · 412'],
    ['comparison', 'Comparison unavailable'],
    ['evidence', 'Evidence unavailable'],
    ['denied', 'Review permission revoked'],
    ['reject-error', 'Rejection failed'],
    ['rejecting', 'Submitting rejection'],
    ['long', 'Long step wording'],
    ['success', 'Queued in pending changes'],
    ['unavailable', 'Requested record unavailable'],
  ])('presents the %s review state', async (state, heading) => {
    const { screen } = await mount(state);
    expect(await screen.headings()).toHaveLength(1);
    expect(await screen.text()).toContain(heading);
  });
  it('does not substitute inc-93 for an unavailable incident', async () => {
    const { screen } = await mount();
    params.next(convertToParamMap({ incident: 'inc-92' }));
    expect(await screen.text()).toContain('Requested record unavailable');
    expect(await screen.text()).not.toContain('inc-93');
  });
  it.each([
    'ready',
    'error',
    'queueing',
    'queued',
    'duplicate',
    'rejected',
    'stale',
    'comparison',
    'evidence',
    'denied',
    'reject-error',
    'rejecting',
    'long',
    'success',
  ])(
    'keeps the original failure, pinned versions and absent attempt evidence in %s',
    async (state) => {
      const { screen } = await mount(state);
      const text = await screen.text();
      for (const value of [
        'inc-93',
        'SHOP-TC-0051',
        'v2',
        'v3 awaiting review',
        'Discount field was hidden',
        'no attempt ID or artifact',
        'has_failures',
        'ev-3',
      ])
        expect(text).toContain(value);
      expect(await (await screen.evidence()).text()).toContain('Incomplete');
      expect(await (await screen.decision()).text()).toContain('FAIL');
      expect(text).not.toMatch(/\bPASS\b|att-1842|art-shot|proposal applied/i);
    },
  );
  it('marks an undecided proposal with a neutral clock and readable label', async () => {
    const { screen } = await mount();
    expect(await (await screen.waiting()).text()).toContain('Awaiting review');
    expect(await (await screen.waiting()).getAttribute('data-tone')).toBe('pending');
    expect(await (await screen.clock()).getAttribute('aria-hidden')).toBe('true');
  });
  it('removes the comparison and decision actions when no comparison is supplied', async () => {
    const { screen } = await mount();
    expect(await screen.comparison()).not.toBeNull();
    params.next(convertToParamMap({ state: 'comparison' }));
    expect(await screen.comparison()).toBeNull();
    expect(await screen.actions()).toHaveLength(0);
  });
});
