import { InteractivityChecker } from '@angular/cdk/a11y';
import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { mappingReviewRoutes } from './mapping-review.routes';
import { mappingResolutionFixture } from './mapping-resolution.fixture';
import { MappingResolutionPage } from './mapping-resolution.page';

class MappingHarness extends ComponentHarness {
  static hostSelector = 'app-mapping-resolution-review';
  readonly text = async () => (await this.host()).text();
  readonly titles = this.locatorForAll('h1');
  readonly header = this.locatorFor('app-page-header');
  readonly filterConflict = this.locatorFor('button[data-mapping-filter="conflicted"]');
  readonly footer = this.locatorFor('[data-queue] footer');
  readonly selection = this.locatorFor('[data-mapping-identity][aria-current="true"]');
  readonly pickConflict = this.locatorFor('button[aria-label="View Checkout confirmation"]');
  readonly pickResolved = this.locatorFor('button[data-row="map-64"]');
  readonly table = this.locatorFor('[role="table"]');
  readonly caption = this.locatorFor('app-data-table .caption');
  readonly columns = this.locatorForAll('[role="columnheader"]');
  readonly suggestion = (id: string) =>
    this.locatorFor(`[data-row-id="${id}"] [data-column-id="candidates"]`)();
  readonly modal = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  readonly dialogTitle = this.documentRootLocatorFactory().locatorFor('[role="dialog"] h2');
  readonly footerClose = this.documentRootLocatorFactory().locatorFor(
    '[role="dialog"] footer button',
  );
  readonly headerClose = this.documentRootLocatorFactory().locatorFor(
    '[data-mapping-header-close]',
  );
  readonly trapAnchors = this.documentRootLocatorFactory().locatorForAll('.cdk-focus-trap-anchor');
  readonly resolve = this.locatorForOptional('[data-resolve]');
  readonly openDecision = this.locatorFor('[data-resolve]');
  readonly chooseCase = this.documentRootLocatorFactory().locatorFor('input[type="radio"]');
  readonly confirm = this.documentRootLocatorFactory().locatorFor('[data-confirm]');
  readonly stateHeading = this.locatorFor('#mapping-state-title');
  readonly statusIcon = this.locatorFor('app-status-pill hlm-icon');
  readonly neutral = this.locatorForOptional('app-status-pill .status-pill--neutral');
  readonly denied = this.locatorForOptional('app-forbidden-pattern');
  readonly queue = this.locatorForOptional('[data-queue]');
  readonly verdict = this.locatorForOptional('app-decision-chip');
}

const desktop = signal(true);
const scope = '/review/orgs/lotus/projects/p-storefront';
async function open(query = '') {
  const router = await RouterTestingHarness.create(scope + query);
  return TestbedHarnessEnvironment.loader(router.fixture).getHarness(MappingHarness);
}

describe('Mapping resolution review', () => {
  provideLayoutTestStubs();
  beforeEach(() => {
    desktop.set(true);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'review/orgs/:orgId/projects/:projectId', children: mappingReviewRoutes },
        ]),
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: desktop, narrow: () => !desktop(), allowsMutation: desktop },
        },
      ],
    });
  });
  afterEach(() => vi.restoreAllMocks());

  it('starts on the unmapped row and switches the details without changing the failed attempt', async () => {
    const page = await open();
    expect(await page.titles()).toHaveLength(1);
    expect(await (await page.selection()).text()).toContain('map-62');
    expect(await page.statusIcon()).toBeTruthy();
    await (await page.pickConflict()).click();
    expect(await (await page.selection()).text()).toContain('map-63');
    expect(await (await page.modal())?.text()).toContain('case-checkout-17 / case-checkout-22');
    await (await page.footerClose()).click();
    expect(await page.text()).toContain('failed · attempt 1');
    expect(await (await page.verdict())?.text()).toContain('FAIL');
    expect(await page.text()).not.toMatch(/\bPASS\b/);
  });

  it('names the table and the View dialog, presents loaded candidates, and traps focus', async () => {
    const page = await open('?state=readonly');
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
    expect(await (await page.table()).getAttribute('aria-labelledby')).toBe(
      await (await page.caption()).getAttribute('id'),
    );
    expect(await (await page.caption()).text()).toBe('Mapping records');
    expect(await Promise.all((await page.columns()).map((column) => column.text()))).toEqual([
      'Imported identity',
      'Mapping state',
      'Suggested case',
      'Actions',
    ]);
    expect(await (await page.suggestion('map-62')).text()).toBe('No proposed case');
    expect(await (await page.suggestion('map-63')).text()).toBe(
      'case-checkout-17 / case-checkout-22',
    );
    const trigger = await page.pickConflict();
    expect(await trigger.text()).toBe('');
    expect(await trigger.getAttribute('aria-label')).toBe('View Checkout confirmation');
    const url = TestBed.inject(Router).url;
    await trigger.focus();
    await trigger.click();
    const dialog = await page.modal();
    const title = await page.dialogTitle();
    expect(await dialog?.getAttribute('aria-labelledby')).toBe(await title.getAttribute('id'));
    expect(await dialog?.getAttribute('aria-modal')).toBe('true');
    expect(await title.text()).toBe('Mapping details');
    expect(await title.isFocused()).toBe(true);
    const text = await dialog?.text();
    expect(text).toContain('Checkout confirms order');
    expect(text).toContain('Suggested stable identity');
    expect(text).toContain('Candidate cases 2');
    expect(text).not.toContain('Address formats postcode');
    expect(text).toContain('failed · attempt 1');
    expect(text).toContain('case:write');
    expect(await (await page.headerClose()).getAttribute('aria-label')).toBe('Close');
    const anchors = await page.trapAnchors();
    expect(anchors).toHaveLength(2);
    for (const anchor of anchors) {
      await anchor.focus();
      expect(
        (await (await page.headerClose()).isFocused()) ||
          (await (await page.footerClose()).isFocused()),
      ).toBe(true);
    }
    await (await page.footerClose()).sendKeys(TestKey.ESCAPE);
    expect(await page.modal()).toBeNull();
    expect(await trigger.isFocused()).toBe(true);
    expect(TestBed.inject(Router).url).toBe(url);
    expect(await (await page.resolve())?.getProperty<boolean>('disabled')).toBe(true);
  });

  it('shows resolved history without inventing candidates or offering an edit', async () => {
    const page = await open('?mapping=map-64');
    expect(await (await page.suggestion('map-64')).text()).toBe('SHOP-TC-0042 · v5');
    const trigger = await page.pickResolved();
    await trigger.click();
    expect(await (await page.modal())?.text()).toContain('No candidate cases supplied.');
    expect(await (await page.modal())?.text()).toContain('Candidate cases 0');
    expect(await (await page.modal())?.text()).not.toContain('Confirm preview');
    await (await page.headerClose()).click();
    expect(await page.modal()).toBeNull();
    expect(await trigger.isFocused()).toBe(true);
  });

  it('uses scoped header facts, counted filters, and an honest loaded-record footer', async () => {
    const page = await open();
    expect(await (await page.header()).text()).toContain(
      'storefront-web · junit-ci · rpt-junit-62',
    );
    expect(await (await page.footer()).text()).toContain('Loaded records · Showing 2 of 3');
    expect(await (await page.filterConflict()).text()).toMatch(/Conflicted\s*1/);
    await (await page.filterConflict()).click();
    expect(await (await page.filterConflict()).getAttribute('aria-pressed')).toBe('true');
    expect(await (await page.footer()).text()).toContain('Loaded records · Showing 1 of 3');
    expect(await (await page.selection()).text()).toContain('map-63');
  });

  it('derives KPI and context values from the supplied records rather than fixture totals', async () => {
    const fixture = TestBed.createComponent(MappingResolutionPage);
    fixture.componentRef.setInput('viewModel', {
      ...mappingResolutionFixture,
      project: 'new-project',
      producer: 'new-producer',
      report: 'report-2',
      rows: mappingResolutionFixture.rows.filter((row) => row.state === 'conflicted'),
    });
    await fixture.whenStable();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, MappingHarness);
    expect(await (await page.header()).text()).toContain('new-project · new-producer · report-2');
    expect(await page.text()).toContain('Unmapped: 0 · Conflicted: 1');
    expect(await (await page.footer()).text()).toContain('Loaded records · Showing 1 of 1');
    expect(await page.text()).not.toContain('GitHub Actions');
  });

  it('clears an open record when the route loses read access', async () => {
    const page = await open();
    await (await page.pickConflict()).click();
    expect(await (await page.modal())?.text()).toContain('Checkout confirms order');
    await TestBed.inject(Router).navigateByUrl(scope + '?state=denied');
    expect(await page.denied()).not.toBeNull();
    expect(await page.modal()).toBeNull();
    expect(await (await page.header()).text()).not.toContain('rpt-junit-62');
    expect(await page.text()).not.toContain('case-checkout-17');
  });

  it('moves the explicit selection to a neutral pending preview without changing evidence', async () => {
    const page = await open('?mapping=map-63');
    await (await page.openDecision()).click();
    await (await page.chooseCase()).click();
    await (await page.confirm()).click();
    expect(await page.text()).toContain('map-63');
    expect(await page.text()).toContain('case-checkout-17');
    expect(await page.text()).toContain('Original failed result and raw evidence stay unchanged.');
    expect(await page.neutral()).not.toBeNull();
    expect(await page.resolve()).toBeNull();
    expect(await (await page.stateHeading()).isFocused()).toBe(true);
  });

  it.each([
    ['loading', 'Reading the current scope'],
    ['empty', 'No unresolved mappings'],
    ['filtered', 'No results match these filters'],
    ['error', 'req-26-31'],
    ['denied', 'Not authorized'],
    ['missing', 'Not found'],
    ['expired', 'Sign in again'],
    ['limited', 'Retry-After: 30 s'],
    ['conflict', 'rv-12 → rv-13'],
    ['pending', 'awaiting server response'],
    ['unavail', 'No candidate may be inferred'],
    ['pickerload', 'Loading project cases'],
    ['pickerempty', 'No project cases match'],
    ['pickererror', 'req-map-31'],
    ['guard', '409 · Already resolved'],
    ['mapped', 'Original result remains failed'],
    ['success', 'Original result remains failed'],
    ['long', '訂正後の郵便番号を再検証'],
  ])('renders the %s state instead of the actionable queue', async (state, expected) => {
    const page = await open('?state=' + state);
    expect(await page.text()).toContain(expected);
    expect(await page.queue()).toBeNull();
    if (state === 'pending') expect(await page.neutral()).not.toBeNull();
  });

  it.each([
    ['imp-63', 'unsupported_status'],
    ['imp-64', 'no_stable_mapping'],
    ['imp-65', 'multiple_candidates'],
  ])('keeps raw and normalized results separate for %s', async (key, reason) => {
    const page = await open('?mapping=' + key);
    expect(await page.text()).toContain(reason);
    expect(await page.resolve()).toBeNull();
    expect(await page.text()).toContain('No mapping decision ID');
  });

  it('does not substitute a valid row for a mismatched frozen scope', async () => {
    const page = await open('?mapping=map-62&evidence=ev-4');
    expect(await page.text()).toContain('Requested record unavailable');
    expect(await page.queue()).toBeNull();
  });

  it('does not expose records when access is denied, even on a filtered import link', async () => {
    const page = await open('?state=denied&mapping=imp-63');
    expect(await page.denied()).not.toBeNull();
    expect(await page.text()).not.toContain('unsupported_status');
  });

  it('keeps supplied resolved history separate from unresolved work', async () => {
    const page = await open('?mapping=map-64');
    expect(await page.text()).toContain('Mapped · server-confirmed');
    expect(await page.text()).not.toContain('map-62');
    expect(await page.resolve()).toBeNull();
  });

  it('retains inspection but disables decisions without write permission', async () => {
    const page = await open('?state=readonly');
    expect(await page.text()).toContain('case:write');
    expect(await (await page.resolve())?.getProperty<boolean>('disabled')).toBe(true);
  });

  it('keeps the queue readable without write controls on narrow screens', async () => {
    desktop.set(false);
    const page = await open();
    expect(await page.selection()).toBeTruthy();
    expect(await page.resolve()).toBeNull();
  });
});
