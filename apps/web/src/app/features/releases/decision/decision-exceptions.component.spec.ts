import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { TestBed } from '@angular/core/testing';
import { stubLayoutApis } from '../../../shared/testing/layout.testing';
import { DecisionExceptions } from './decision-exceptions.component';
import { decisionFixture } from './decision-review.fixture';
import { DecisionDialogHarness, DecisionReviewHarness } from './decision-review.harness';

describe('DecisionExceptions', () => {
  beforeEach(() => {
    stubLayoutApis();
    TestBed.configureTestingModule({
      imports: [DecisionExceptions],
      providers: [{ provide: NarrowMutationPolicy, useValue: { canMutate: signal(true) } }],
    });
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });
  async function render(state: string, vm = decisionFixture(state)) {
    const fixture = TestBed.createComponent(DecisionExceptions);
    fixture.componentRef.setInput('view', vm);
    const h = await TestbedHarnessEnvironment.harnessForFixture(fixture, DecisionReviewHarness);
    const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
    return { h, loader };
  }
  it('lists every record and restores focus to its View button without replacing the failure', async () => {
    const { h, loader } = await render('default');
    const table = await h.find('[role=table]');
    const caption = await table.getAttribute('aria-labelledby');
    expect(await (await h.find(`[id="${caption}"]`)).text()).toBe('Exception queue');
    const headers = await h.all('[role=columnheader]');
    expect(await Promise.all(headers.map((header) => header.text()))).toEqual([
      'Exception reference',
      'Status',
      'Case / owner',
      'Expires · UTC',
      'Actions',
    ]);
    expect(await h.all('[data-view]')).toHaveLength(3);
    expect(await (await h.find('footer')).text()).toContain('Showing 3');
    expect(await (await h.find('[data-row-id="w-28"]')).text()).toContain('Waiver');
    expect(await loader.getAllHarnesses(DecisionDialogHarness)).toHaveLength(0);
    for (const id of ['q-19', 'checkout-vi-chromium', 'w-28']) {
      const opener = await h.find(`[data-view="${id}"]`);
      expect(await opener.getAttribute('aria-label')).toBe(`View ${id}`);
      await opener.click();
      expect(await loader.getAllHarnesses(DecisionDialogHarness)).toHaveLength(1);
      const dialog = await loader.getHarness(DecisionDialogHarness);
      const heading = await dialog.find('h2');
      expect(await heading.text()).toBe('Exception details');
      expect(await (await dialog.host()).getAttribute('aria-labelledby')).toBe(
        await heading.getAttribute('id'),
      );
      expect(await heading.isFocused()).toBe(true);
      const detail = await dialog.text();
      expect(detail).toContain(id);
      expect(detail).toContain('run-1842 remains FAIL');
      expect(detail).not.toMatch(/\bPASS\b/);
      expect(await dialog.all('app-waiver-banner')).toHaveLength(id === 'w-28' ? 1 : 0);
      if (id === 'w-28') {
        for (const fact of [
          'Waiver',
          'Approved',
          'shipping-estimate',
          'An Phạm',
          'INC-204',
          '2026-10-15T00:00Z',
          'Linh Nguyễn',
          'pr-12',
        ]) {
          expect(detail).toContain(fact);
        }
      }
      await heading.sendKeys(TestKey.ESCAPE);
      expect(await loader.getAllHarnesses(DecisionDialogHarness)).toHaveLength(0);
      expect(await opener.isFocused()).toBe(true);
      if (id === 'q-19') expect(await h.all('[data-action=remove]')).toHaveLength(1);
    }
  });
  it('renders the exception reason as text rather than creating markup', async () => {
    const id = 'w-28';
    const vm = decisionFixture('default');
    const reason = '<strong data-preview-reason>INC-204</strong>';
    const { h, loader } = await render('default', {
      ...vm,
      records: vm.records.map((record) => ({ ...record, reason })),
    });
    await (await h.find(`[data-view="${id}"]`)).click();
    const dialog = await loader.getHarness(DecisionDialogHarness);
    expect(await dialog.text()).toContain(reason);
    expect(await dialog.all('[data-preview-reason]')).toHaveLength(0);
  });
  it.each(['requested', 'self'])(
    'marks %s neutral with text and icon; self approval is refused',
    async (state) => {
      const { h, loader } = await render(state);
      await (await h.find('[data-view="w-29"]')).click();
      const dialog = await loader.getHarness(DecisionDialogHarness);
      const badge = await dialog.find('.status-pill--neutral');
      expect(await badge.text()).toContain('Requested');
      expect(await dialog.all('.status-pill--neutral hlm-icon')).toHaveLength(1);
      await (await dialog.find('button[data-close]')).click();
      expect(await (await h.find('[data-action=decline]')).text()).toContain('Preview only');
      expect(await (await h.find('[data-action=approve]')).getProperty('disabled')).toBe(
        state === 'self',
      );
    },
  );
  it.each(['q-removed', 'q-expired'])(
    'does not offer to remove an inactive quarantine in %s',
    async (state) => {
      const { h } = await render(state);
      expect(await (await h.find('[data-action=remove]')).getProperty('disabled')).toBe(true);
      expect(await h.all('[data-action=add]')).toHaveLength(1);
    },
  );
});
