import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it } from 'vitest';

import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { InvestigationHistoryReviewPage } from './investigation-history.review.page';

provideLayoutTestStubs();

class PageHarness extends ComponentHarness {
  static hostSelector = 'app-investigation-history-review';
  async text(): Promise<string> {
    return (await this.host()).text();
  }
  readonly links = this.locatorForAll('a');
}

async function mount(query = '') {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'orgs/:orgId/projects/:projectId', component: InvestigationHistoryReviewPage },
      ]),
    ],
  });
  const route = await RouterTestingHarness.create('/orgs/lotus/projects/shop?' + query);
  return TestbedHarnessEnvironment.loader(route.fixture).getHarness(PageHarness);
}

describe('InvestigationHistoryReviewPage', () => {
  it.each([
    ['default', 'Expected order confirmation; received HTTP 500.'],
    ['loading', 'Loading case history'],
    ['empty', 'No retained history'],
    ['nomatch', 'No matching revisions'],
    ['error', 'Could not load case history'],
    ['401', 'Session expired'],
    ['403', 'Case history is unavailable'],
    ['404', 'Not found'],
    ['412', 'Linked catalog edit is stale'],
    ['429', 'Rate limited'],
    ['one', 'Only one revision available'],
    ['retention', 'History expired under retention policy'],
    ['equal', 'No recorded changes'],
    ['changed', 'Equal totals, changed message'],
    ['missing', 'Screenshot unavailable'],
    ['partial', 'Context is partial'],
  ])('renders the %s snapshot', async (state, text) => {
    expect(await (await mount('state=' + state)).text()).toContain(text);
  });
  it('never substitutes checkout attempts for the address case', async () => {
    const page = await mount('id=case-address-8');
    expect(await page.text()).toMatch(/case-address-8[\s\S]*Required delivery address field/);
    expect(await page.text()).not.toMatch(/att-1842-01|art-shot-92|case-checkout-17/);
  });
  it('retains search identity without fabricating attempt history', async () => {
    const page = await mount('id=case-search-4');
    expect(await page.text()).toContain('case-search-4');
    expect(await page.text()).not.toMatch(/att-1842|inc-9/);
  });
  it('never upgrades the original after a retry or reprocessing', async () => {
    const page = await mount('revision=ev-4');
    expect(await page.text()).toMatch(
      /Original failure[\s\S]*HTTP 500[\s\S]*Reprocessed[\s\S]*att-1842-02/,
    );
    expect(await page.text()).not.toMatch(/\bPASS\b/);
  });

  it('opens the checkout screenshot with the selected route scope', async () => {
    const page = await mount();
    const hrefs = await Promise.all((await page.links()).map((link) => link.getAttribute('href')));
    expect(hrefs).toContain('/dev/artifact-viewer/orgs/lotus/projects/shop?id=art-shot-92');
  });

  it('does not supply a checkout screenshot when the selected artifact is missing', async () => {
    const page = await mount('state=missing');
    const hrefs = await Promise.all((await page.links()).map((link) => link.getAttribute('href')));
    expect(hrefs.some((href) => href?.includes('/dev/artifact-viewer/'))).toBe(false);
    expect(await page.text()).toContain('stored FAIL is unchanged');
  });

  it('does not substitute a case for an unknown stable ID', async () => {
    const page = await mount('id=case-missing');
    expect(await page.text()).toContain('case-missing');
    expect(await page.text()).not.toContain('att-1842-01');
  });
});
