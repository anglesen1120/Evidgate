import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { ReleaseViewReviewPage } from './release-view-review.page';

class ReleaseHarness extends ComponentHarness {
  static hostSelector = 'app-release-view-review';
  readonly summary = this.locatorForOptional('[data-assessment]');
  readonly failed = this.locatorForOptional('app-decision-chip [data-tone="danger"]');
  readonly allText = async () => (await this.host()).text();
}

describe('ReleaseViewReviewPage', () => {
  provideLayoutTestStubs();

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'release', component: ReleaseViewReviewPage }])],
    });
  });

  it.each([
    ['default', 'Release assessment', true],
    ['loading', 'Loading release record', false],
    ['401', 'Your session expired', false],
    ['403', 'Not authorized', false],
    ['404', 'Not found', false],
    ['412', 'Someone else changed this — reload', true],
    ['429', 'Too many requests', false],
    ['empty', 'Unbound release · no components', false],
    ['no-match', 'No results match these filters', true],
    ['error', 'Could not load this snapshot', false],
    ['early', 'Available in G4', false],
    ['stale', 'Stale digest', true],
    ['missing', 'Missing component evidence', true],
    ['newer', 'Newer evidence available', true],
    ['expired', 'Late-evidence window closed', true],
    ['partial', 'Partial component snapshot', true],
    ['restricted', 'Restricted evidence metadata', true],
    ['long', 'Recorded digest detail', true],
    ['pending', 'Pending evaluation', false],
    ['canceled', 'Canceled bound run', false],
    ['failure', 'Separate run evaluation', false],
    ['waived', 'Separate run evaluation', false],
    ['unknown', 'Release assessment', true],
  ] as const)('renders %s without inferring a release PASS', async (state, message, snapshot) => {
    const route = await RouterTestingHarness.create(`/release?state=${state}`);
    const page = await TestbedHarnessEnvironment.loader(route.fixture).getHarness(ReleaseHarness);
    const text = await page.allText();
    expect(text).toContain(message);
    const assessment = await page.summary();
    if (snapshot) {
      expect(await assessment?.text()).toContain('INCONCLUSIVE');
      expect(await assessment?.text()).not.toContain('PASS');
    } else {
      expect(assessment).toBeNull();
      expect(text).not.toContain('sha256:a72c');
    }
    if (state === 'failure' || state === 'waived') {
      expect(await (await page.failed())?.text()).toContain('FAIL');
      expect(text).toContain('Incomplete');
      expect(text).toContain('Has failures');
      expect(text.includes('w-28')).toBe(state === 'waived');
    }
  });

  it('withdraws the previous snapshot when the query changes to forbidden', async () => {
    const route = await RouterTestingHarness.create('/release');
    const page = await TestbedHarnessEnvironment.loader(route.fixture).getHarness(ReleaseHarness);
    expect(await page.allText()).toContain('rd-14');
    await route.navigateByUrl('/release?state=403', ReleaseViewReviewPage);
    expect(await page.allText()).not.toContain('rd-14');
    expect(await page.allText()).not.toContain('storefront-web');
  });
});
