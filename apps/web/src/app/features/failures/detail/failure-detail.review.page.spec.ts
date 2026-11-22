import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it } from 'vitest';

import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { FailureDetailReviewPage } from './failure-detail.review.page';

provideLayoutTestStubs();

class PageHarness extends ComponentHarness {
  static hostSelector = 'app-failure-detail-review';
  async text(): Promise<string> {
    return (await this.host()).text();
  }
  readonly pills = this.locatorForAll('app-status-pill');
  readonly subtitle = this.locatorFor('app-page-header p');
  readonly icons = this.locatorForAll('app-status-pill hlm-icon');
  readonly links = this.locatorForAll('a');
  readonly pendingBadge = this.locatorFor('app-status-pill .status-pill--neutral');
  readonly reason = this.documentRootLocatorFactory().locatorFor('#triage-reason');

  async button(label: string, overlay = false) {
    const buttons = overlay
      ? await this.documentRootLocatorFactory().locatorForAll('[role="dialog"] button')()
      : await this.locatorForAll('button')();
    for (const button of buttons) if ((await button.text()).trim() === label) return button;
    throw new Error(`Missing button: ${label}`);
  }
}

async function mount(query = '') {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'orgs/:orgId/projects/:projectId', component: FailureDetailReviewPage },
      ]),
      {
        provide: NarrowMutationPolicy,
        useValue: {
          canMutate: signal(true),
          narrow: signal(false),
          allowsMutation: () => true,
        },
      },
    ],
  });
  const route = await RouterTestingHarness.create('/orgs/lotus/projects/shop?' + query);
  return TestbedHarnessEnvironment.loader(route.fixture).getHarness(PageHarness);
}

describe('FailureDetailReviewPage', () => {
  it.each([
    ['default', 'Expected order confirmation; received HTTP 500.'],
    ['loading', 'Loading incident'],
    ['empty', 'No incident to triage'],
    ['nomatch', 'No events match this filter'],
    ['error', 'Could not load this incident'],
    ['401', 'Session expired'],
    ['403', 'Triage permission revoked'],
    ['404', 'Not found'],
    ['412', 'Someone else changed this incident'],
    ['429', 'Rate limited'],
    ['pending', 'Collecting context'],
    ['complete', 'Triage context complete'],
    ['infra', 'Infrastructure error'],
    ['unknown', 'Unknown status'],
    ['stale', 'Stale collection epoch'],
    ['missing', 'Trace missing'],
    ['409', 'Already closed'],
    ['first', 'Label first'],
    ['422', 'Invalid assignee'],
    ['submitting', 'Submitting triage'],
    ['success', 'Human-confirmed'],
    ['closed', 'Incident closed'],
    ['eligible', 'Eligible proposal'],
    ['v1', 'No proposal action'],
    ['latest', 'Reloaded'],
  ])('renders the %s snapshot', async (state, text) => {
    const page = await mount('state=' + state);
    expect(await page.text()).toContain(text);
    if (state === 'loading' || state === '404') {
      expect(await (await page.subtitle()).text()).not.toContain('storefront-web');
    }
  });
  it('keeps the failed original before the passing retry and classification', async () => {
    const page = await mount();
    expect(await (await page.subtitle()).text()).toContain('storefront-web · SHOP');
    const text = await page.text();
    expect(text).toMatch(/att-1842-01[\s\S]*HTTP 500[\s\S]*att-1842-02[\s\S]*Passed/);
    expect(text.indexOf('Original failure')).toBeLessThan(text.indexOf('Classification'));
    expect(text).not.toMatch(/\bPASS\b/);
    expect((await page.icons()).length).toBe((await page.pills()).length);
    expect((await page.icons()).length).toBeGreaterThan(0);
  });

  it('links address failure to address history without checkout evidence', async () => {
    const page = await mount('id=inc-92');
    expect(await page.text()).toContain(
      'Required delivery address field not found before timeout.',
    );
    expect(await page.text()).not.toMatch(/att-1842-01|art-shot-92/);
    const hrefs = await Promise.all((await page.links()).map((link) => link.getAttribute('href')));
    expect(hrefs).toContain(
      '/dev/investigation-history/orgs/lotus/projects/shop?id=case-address-8',
    );
  });

  it('previews a reason without confirming a human decision or enabling closure', async () => {
    const page = await mount();
    expect(await (await page.button('Close incident')).getProperty('disabled')).toBe(true);
    await (await page.button('Save triage')).click();
    const field = await page.reason();
    await field.setInputValue('   ');
    await field.dispatchEvent('input');
    expect(await (await page.button('Preview save', true)).getProperty('disabled')).toBe(true);
    await field.setInputValue('Server returned HTTP 500.');
    await field.dispatchEvent('input');
    await (await page.button('Preview save', true)).click();
    expect(await page.text()).toContain('Preview only · nothing submitted');
    expect(await page.text()).toContain('Server returned HTTP 500.');
    expect(await page.text()).not.toContain('Human-confirmed');
    expect(await (await page.button('Close incident')).getProperty('disabled')).toBe(true);
  });

  it('retains readable evidence but disables triage after permission is revoked', async () => {
    const page = await mount('state=403');
    expect(await page.text()).toContain('att-1842-01');
    expect(await (await page.button('Save triage')).getProperty('disabled')).toBe(true);
  });

  it('presents the pending log scan with a neutral text badge', async () => {
    const page = await mount();
    expect(await (await page.pendingBadge()).text()).toContain('Pending');
  });
});
