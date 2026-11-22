import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it } from 'vitest';
import { KnowledgeLineageReviewPage } from './knowledge-lineage.review.page';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';

class PageHarness extends ComponentHarness {
  static hostSelector = 'app-knowledge-lineage-review';
  readonly text = async () => (await this.host()).text();
  readonly find = (selector: string) => this.locatorForOptional(selector)();
}
class ConsentHarness extends ComponentHarness {
  static hostSelector = 'cdk-dialog-container[role="alertdialog"] hlm-dialog-content';
  readonly text = async () => (await this.host()).text();
  readonly reason = this.locatorFor('textarea');
  async enterReason(value: string): Promise<void> {
    const textarea = await this.reason();
    await textarea.setInputValue(value);
    await textarea.dispatchEvent('input');
  }
  readonly confirm = this.locatorFor('[data-confirm]');
  readonly cancel = this.locatorFor('[data-cancel]');
}

async function mount(state: string, id?: string) {
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'orgs/:orgId/projects/:projectId', component: KnowledgeLineageReviewPage },
      ]),
      {
        provide: NarrowMutationPolicy,
        useValue: { canMutate: signal(true), narrow: signal(false), allowsMutation: () => true },
      },
    ],
  }).compileComponents();
  const query = new URLSearchParams(id === undefined ? { state } : { state, id });
  const { fixture } = await RouterTestingHarness.create(`/orgs/org/projects/project?${query}`);
  const page = await TestbedHarnessEnvironment.loader(fixture).getHarness(PageHarness);
  return { page, overlays: TestbedHarnessEnvironment.documentRootLoader(fixture) };
}

describe('KnowledgeLineageReviewPage', () => {
  it.each([
    ['default', 'Active reuse'],
    ['active', 'Active reuse'],
    ['loading', 'Loading knowledge lineage'],
    ['empty', 'No knowledge record'],
    ['filtered', 'No matching records'],
    ['error', 'Unable to refresh'],
    ['403', 'Revocation not permitted'],
    ['404', 'Not found'],
    ['401', 'Session expired'],
    ['429', 'Retry-After: 30 s'],
    ['412', 'Dependency snapshot changed'],
    ['reloaded', 'kr-22 · rev 8'],
    ['revoked', 'Revoked'],
    ['applied', 'applied'],
    ['ineligible', 'Only approved or applied candidates'],
    ['historical', 'Historical snapshot'],
    ['expired', 'Authorization expired'],
    ['unavailable', 'Dependency snapshot unavailable'],
    ['none', '0 active dependents'],
    ['previewloading', 'Loading the dependency snapshot'],
    ['previewerror', 'Unable to load the effect preview'],
    ['refused', 'A second revocation was refused'],
    ['submitting', 'Preparing revocation'],
    ['prepared', 'Request preview prepared'],
  ])('shows the %s snapshot', async (state, expected) => {
    const { page } = await mount(state);
    expect(await page.text()).toContain(expected);
  });
  it('shows the original failure before sandbox validation and keeps the official FAIL', async () => {
    const { page } = await mount('default');
    const text = await page.text();
    const failure = text.indexOf('Required delivery address field not found');
    expect(failure).toBeGreaterThanOrEqual(0);
    expect(text.indexOf('24/24')).toBeGreaterThan(failure);
    expect(text).toContain('sandbox only');
    expect(text).not.toMatch(/att-1842-01|\bPASS\b/);
    expect(await page.find('app-decision-chip hlm-icon')).not.toBeNull();
  });
  it.each(['historical', 'expired', 'revoked', 'unavailable', 'ineligible'])(
    'does not offer a revocation in %s',
    async (state) => {
      const { page } = await mount(state);
      expect(await page.find('[data-revoke]:not(:disabled)')).toBeNull();
    },
  );
  it('flags the two active dependents but preserves the applied historical use', async () => {
    const { page } = await mount('revoked');
    const table = await page.find('table');
    const text = await table?.text();
    expect(text?.match(/Flagged · reuse blocked/g)).toHaveLength(2);
    expect(text).toContain('cand-83');
    expect(text).toContain('Historical use · retained');
    expect(text).toContain('applied');
  });
  it('does not convert an unknown effect into an empty dependency snapshot', async () => {
    const { page } = await mount('unavailable');
    expect(await page.text()).not.toMatch(/0 active dependents|cand-81|cand-83/);
    expect(await page.find('table')).toBeNull();
  });
  it('requires a reason and prepares a request for exact IDs without changing the recorded state', async () => {
    const { page, overlays } = await mount('default');
    const trigger = await page.find('[data-revoke]');
    expect(trigger).not.toBeNull();
    await trigger?.click();
    const dialog = await overlays.getHarness(ConsentHarness);
    for (const value of [
      'cand-73',
      'kr-22',
      'cand-81',
      'cand-82',
      'cand-83',
      'sha256:ch-8af2c41e73b90d11c6e8f207b5343e20',
      'Minh Trần',
      '9af317c',
    ])
      expect(await dialog.text()).toContain(value);
    expect(await (await dialog.reason()).getProperty('maxLength')).toBe(2000);
    await dialog.enterReason('   ');
    expect(await (await dialog.confirm()).getProperty('disabled')).toBe(true);
    await dialog.enterReason('Locator changed');
    expect(await (await dialog.confirm()).getProperty('disabled')).toBe(false);
    await (await dialog.confirm()).click();
    expect(await page.text()).toContain('Request preview prepared');
    expect(await page.text()).toContain('Active reuse');
    expect(await page.text()).toContain('Locator changed');
  });
  it('shows an invalid blank reason for the 422 specimen; cancel does not prepare a request', async () => {
    const { page, overlays } = await mount('422');
    const dialog = await overlays.getHarness(ConsentHarness);
    expect(await (await dialog.reason()).getAttribute('aria-invalid')).toBe('true');
    await (await dialog.cancel()).click();
    expect(await page.text()).not.toContain('Request preview prepared');
  });
  it('does not show another knowledge record for an unknown ID', async () => {
    const { page } = await mount('default', 'unknown');
    expect(await page.text()).toContain('Not found');
    expect(await page.text()).not.toContain('kr-22');
  });
});
