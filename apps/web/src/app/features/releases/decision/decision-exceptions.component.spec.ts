import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { TestBed } from '@angular/core/testing';
import { stubLayoutApis } from '../../../shared/testing/layout.testing';
import { DecisionExceptions } from './decision-exceptions.component';
import { decisionFixture } from './decision-review.fixture';
import { DecisionReviewHarness } from './decision-review.harness';

describe('DecisionExceptions', () => {
  beforeEach(() => {
    stubLayoutApis();
    TestBed.configureTestingModule({
      imports: [DecisionExceptions],
      providers: [{ provide: NarrowMutationPolicy, useValue: { canMutate: signal(true) } }],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.unstubAllGlobals();
  });
  async function render(state: string) {
    const fixture = TestBed.createComponent(DecisionExceptions);
    fixture.componentRef.setInput('view', decisionFixture(state));
    return TestbedHarnessEnvironment.harnessForFixture(fixture, DecisionReviewHarness);
  }
  it('selects each scoped record without replacing its original failure', async () => {
    const h = await render('default');
    expect(await h.all('app-waiver-banner')).toHaveLength(1);
    for (const id of ['q-19', 'checkout-vi-chromium', 'w-28']) {
      await (await h.find('input[value="' + id + '"]')).click();
      const detail = await (await h.find('[data-detail]')).text();
      expect(detail).toContain(id);
      expect(detail).toContain('run-1842 remains FAIL');
      expect(detail).not.toMatch(/\bPASS\b/);
    }
  });
  it('renders the exception reason as text rather than creating markup', async () => {
    const fixture = TestBed.createComponent(DecisionExceptions);
    const vm = decisionFixture('default');
    const reason = '<strong data-preview-reason>INC-204</strong>';
    fixture.componentRef.setInput('view', {
      ...vm,
      records: vm.records.map((record) => ({ ...record, reason })),
    });
    const h = await TestbedHarnessEnvironment.harnessForFixture(fixture, DecisionReviewHarness);
    expect(await (await h.find('[data-detail]')).text()).toContain(reason);
    expect(await h.all('[data-preview-reason]')).toHaveLength(0);
  });
  it.each(['requested', 'self'])(
    'marks %s neutral with text and icon; self approval is refused',
    async (state) => {
      const h = await render(state);
      const badge = await h.find('[data-detail] .status-pill--neutral');
      expect(await badge.text()).toContain('Requested');
      expect(await h.all('[data-detail] .status-pill--neutral hlm-icon')).toHaveLength(1);
      expect(await (await h.find('[data-action=decline]')).text()).toContain('Preview only');
      expect(await (await h.find('[data-action=approve]')).getProperty('disabled')).toBe(
        state === 'self',
      );
    },
  );
  it.each(['q-removed', 'q-expired'])(
    'does not offer to remove an inactive quarantine in %s',
    async (state) => {
      const h = await render(state);
      expect(await (await h.find('[data-action=remove]')).getProperty('disabled')).toBe(true);
      expect(await h.all('[data-action=add]')).toHaveLength(1);
    },
  );
});
