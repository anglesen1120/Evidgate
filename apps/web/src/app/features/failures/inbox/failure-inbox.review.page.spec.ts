import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { FailureInboxReviewPage } from './failure-inbox.review.page';

class InboxHarness extends ComponentHarness {
  static hostSelector = 'app-failure-inbox-review';
  readonly preview = this.locatorFor('article[aria-labelledby="inbox-preview-title"]');
  readonly rows = this.locatorForAll('app-inbox-queue [data-row-id]');
  readonly range = this.locatorFor('app-inbox-queue footer span');
  readonly links = this.locatorForAll('app-inbox-queue [data-row-id] a[aria-label^="View "]');
  readonly status = this.locatorFor('app-inbox-queue [data-row-id] app-status-pill');
  readonly icon = this.locatorFor('app-inbox-queue [data-row-id] app-status-pill hlm-icon');
  readonly previewAddress = this.locatorFor('[data-row-id="inc-92"] button[aria-pressed]');
  readonly headers = this.locatorForAll('app-inbox-queue [role="columnheader"]');
  readonly pending = this.locatorFor('[data-comparison-pending]');
  readonly checks = this.locatorForAll('app-inbox-queue [data-row-id] [role="checkbox"]');
  readonly bulk = this.locatorFor('[data-selected-ids]');
  readonly search = this.locatorFor('input[type="search"]');
  readonly more = this.locatorFor('summary');
  readonly review = this.locatorFor('[aria-describedby="inbox-selection"]');
  readonly list = this.documentRootLocatorFactory().locatorFor('hlm-dialog-content ul');
  readonly confirm = this.documentRootLocatorFactory().locatorFor(
    'hlm-dialog-content footer button:last-child',
  );
  readonly next = this.locatorFor('app-inbox-queue footer button:last-child');
  async text(): Promise<string> {
    return (await this.host()).text();
  }
}

const path = 'dev/failure-inbox/orgs/:orgId/projects/:projectId';
async function mount(query = '') {
  const router = await RouterTestingHarness.create();
  await router.navigateByUrl(
    `/dev/failure-inbox/orgs/org-test/projects/project-test${query}`,
    FailureInboxReviewPage,
  );
  return TestbedHarnessEnvironment.loader(router.fixture).getHarness(InboxHarness);
}

provideLayoutTestStubs();
beforeEach(() => {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path, component: FailureInboxReviewPage }]),
      {
        provide: NarrowMutationPolicy,
        useValue: { canMutate: signal(true), narrow: signal(false), allowsMutation: () => true },
      },
    ],
  });
});
afterEach(() => {
  TestBed.resetTestingModule();
});

describe('Failure inbox review', () => {
  it.each([
    ['loading', 'Retrieving stored records'],
    ['empty', 'not a release decision'],
    ['nomatch', 'No results match'],
    ['error', 'req-901'],
    ['401', 'Session expired'],
    ['403', 'Not authorized'],
    ['permission-denied', 'Not authorized'],
    ['404', 'Not found'],
    ['412', 'Retained draft'],
    ['429', '60 s'],
    ['collect', 'Comparison is pending'],
    ['closed', 'still failed'],
    ['model', 'not confirmed'],
    ['submitting', 'Submitting triage'],
    ['saved', 'assigned to Minh Trần'],
    ['snapshot', 'case-address-8'],
    ['latest', 'Retained assignment draft'],
  ])('renders the %s specimen', async (state, expected) => {
    const inbox = await mount(`?state=${state}`);
    expect(await inbox.text()).toContain(expected);
    if (['403', 'permission-denied', '404', 'empty', 'loading'].includes(state))
      expect(await inbox.rows()).toHaveLength(0);
  });

  it('previews the picked row independently of checkbox selection', async () => {
    const inbox = await mount();
    const [checkout] = await inbox.checks();
    if (!checkout) throw new Error('Checkout incident selection is missing');
    await checkout.click();
    await (await inbox.previewAddress()).click();
    expect(await (await inbox.preview()).text()).toContain('inc-92');
    expect(await (await inbox.bulk()).text()).toContain('inc-91');
    expect(await (await inbox.bulk()).text()).not.toContain('inc-92');
  });

  it('links to the scoped incident and pairs severity with an icon', async () => {
    const inbox = await mount();
    expect(await inbox.rows()).toHaveLength(3);
    expect(await Promise.all((await inbox.headers()).map((header) => header.text()))).toEqual([
      'Select incident',
      'Incident',
      'Incident status',
      'Severity',
      'Failure label',
      'View',
    ]);
    const links = await inbox.links();
    for (const link of links) {
      expect(await link.text()).toBe('');
      expect(await link.getAttribute('aria-label')).toMatch(/^View .+/);
      await link.focus();
      expect(await link.isFocused()).toBe(true);
    }
    expect(await Promise.all(links.map((link) => link.getAttribute('href')))).toEqual(
      ['inc-91', 'inc-92', 'inc-93'].map(
        (id) => `/dev/failure-detail/orgs/org-test/projects/project-test?id=${id}`,
      ),
    );
    expect(await links[0]?.text()).toBe('');
    expect(await links[0]?.getAttribute('aria-label')).toContain('Place order returns HTTP 500');
    expect(await (await inbox.status()).text()).toContain('Critical');
    expect(await inbox.icon()).toBeTruthy();
    const detail = await (await inbox.preview()).text();
    expect(detail).toContain('att-1842-01');
    expect(detail).toContain('product_defect');
    expect(detail).not.toContain('cand-73');
  });

  it('shows the address failure before its candidate without borrowing the checkout attempt', async () => {
    const detail = await (await (await mount('?id=inc-92')).preview()).text();
    expect(detail).toContain('Required delivery address field not found before timeout.');
    expect(detail).toContain('Original outcome: failed');
    expect(detail).toContain('cand-73');
    expect(detail.indexOf('Original outcome: failed')).toBeLessThan(detail.indexOf('cand-73'));
    expect(detail).not.toMatch(/att-1842-01|\bPASS\b/);
  });

  it('uses a neutral pending indicator and does not infer a pass from missing context', async () => {
    const inbox = await mount('?id=inc-96');
    const chip = await inbox.pending();
    expect(await chip.text()).toContain('Comparison pending');
    expect(await chip.hasClass('bg-[var(--eg-pending-bg)]')).toBe(true);
    expect(await (await inbox.preview()).text()).not.toMatch(/\bPASS\b/);
  });

  it('names checked IDs, never the highlighted row or a filtered-out selection', async () => {
    const inbox = await mount();
    const checks = await inbox.checks();
    const address = checks[1];
    if (!address) throw new Error('Address incident selection is missing');
    await address.click();
    expect(await (await inbox.bulk()).text()).toContain('inc-92');
    expect(await (await inbox.bulk()).text()).not.toContain('inc-91');
    await (await inbox.more()).click();
    const search = await inbox.search();
    expect(await search.getProperty('maxLength')).toBe(200);
    await search.setInputValue('HTTP 500');
    await search.dispatchEvent('input');
    expect(await inbox.rows()).toHaveLength(1);
    expect(await inbox.text()).not.toContain('inc-92');
  });

  it('confirms only the checked revision as a preview without assigning it', async () => {
    const inbox = await mount();
    const [checkout] = await inbox.checks();
    if (!checkout) throw new Error('Checkout incident selection is missing');
    await checkout.click();
    await (await inbox.review()).click();
    const list = await (await inbox.list()).text();
    expect(list).toContain('inc-91');
    expect(list).toContain('If-Match: 7');
    expect(list).not.toContain('inc-92');
    await (await inbox.confirm()).click();
    expect(await inbox.text()).toContain(
      'Preview confirmed. No incident changed. inc-91 → Minh Trần',
    );
    expect(await checkout.getAttribute('aria-checked')).toBe('true');
    const [first] = await inbox.rows();
    expect(await first?.text()).toContain('Unassigned');
  });

  it('uses all filter dimensions together and paginates only matching incidents', async () => {
    const inbox = await mount(
      '?status=open&owner=unassigned&flaky=yes&label=unknown&severity=medium',
    );
    const rows = await inbox.rows();
    expect(rows).toHaveLength(1);
    expect(await rows[0]?.text()).toContain('inc-96');
    expect(await (await inbox.range()).text()).toBe('Showing 1–1 of 1');
    expect(await (await inbox.next()).getProperty('disabled')).toBe(true);
  });

  it('reports zero matches without inventing a first row', async () => {
    const inbox = await mount('?q=no-such-incident');
    expect(await inbox.rows()).toHaveLength(0);
    expect(await (await inbox.range()).text()).toBe('0 matching incidents');
  });

  it('opens the next page with its own preview, not the previous page’s incident', async () => {
    const inbox = await mount();
    expect(await (await inbox.range()).text()).toBe('Showing 1–3 of 6');
    await (await inbox.next()).click();
    expect(await (await inbox.range()).text()).toBe('Showing 4–6 of 6');
    expect(await (await inbox.preview()).text()).toContain('inc-94');
    expect(await (await inbox.preview()).text()).not.toContain('att-1842-01');
  });

  it('does not substitute another incident for an unavailable deep link', async () => {
    const inbox = await mount('?id=not-an-incident');
    expect(await inbox.text()).toContain('Not found');
    expect(await inbox.rows()).toHaveLength(0);
  });
});
