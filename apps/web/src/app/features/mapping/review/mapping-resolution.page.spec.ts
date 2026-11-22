import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { mappingReviewRoutes } from './mapping-review.routes';

class MappingHarness extends ComponentHarness {
  static hostSelector = 'app-mapping-resolution-review';
  readonly text = async () => (await this.host()).text();
  readonly titles = this.locatorForAll('h1');
  readonly selection = this.locatorFor('[aria-pressed="true"]');
  readonly pickConflict = this.locatorFor('[data-row="map-63"]');
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

  it('starts on the unmapped row and switches the details without changing the failed attempt', async () => {
    const page = await open();
    expect(await page.titles()).toHaveLength(1);
    expect(await (await page.selection()).text()).toContain('map-62');
    expect(await page.statusIcon()).toBeTruthy();
    await (await page.pickConflict()).click();
    expect(await (await page.selection()).text()).toContain('map-63');
    expect(await page.text()).toContain('case-checkout-17 / case-checkout-22');
    expect(await page.text()).toContain('failed · attempt 1');
    expect(await (await page.verdict())?.text()).toContain('FAIL');
    expect(await page.text()).not.toMatch(/\bPASS\b/);
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
