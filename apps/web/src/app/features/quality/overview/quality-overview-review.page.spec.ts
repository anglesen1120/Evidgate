import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { QualityOverviewReviewPage } from './quality-overview-review.page';
import { QualityReviewHarness } from './quality-review.harness';

const states: readonly [string, string, boolean][] = [
  ['default', '18 of 20', true],
  ['loading', 'Loading · last good snapshot retained', true],
  ['empty', 'No eligible runs', false],
  ['no-match', 'No results match', false],
  ['error', 'Could not load this snapshot', true],
  ['401', 'Your session expired', false],
  ['403', 'Not authorized', false],
  ['404', 'Not found', false],
  ['412', 'Draft threshold 3 retained, not merged', false],
  ['429', 'No automatic retry', false],
  ['early', 'Available in G4', false],
  ['partial', 'no interpolation', true],
  ['stale', '2026-10-13T10:00Z', true],
  ['purged', 'Historical data purged', false],
  ['unavailable', 'no estimate is shown', true],
  ['zero', 'there is no rate', true],
  ['long', 'exceptionally long multiword', true],
  ['invalid', 'Invalid date range', false],
  ['503', '503 · Service unavailable', true],
  ['range', 'Range changed', true],
  ['unprovided', 'No snapshot supplied', false],
  ['unknown', '18 of 20', true],
];

describe('QualityOverviewReviewPage', () => {
  provideLayoutTestStubs();
  const query = new BehaviorSubject(convertToParamMap({}));
  beforeEach(() => {
    query.next(convertToParamMap({}));
    TestBed.configureTestingModule({
      imports: [QualityOverviewReviewPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: query } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render() {
    const fixture = TestBed.createComponent(QualityOverviewReviewPage);
    return TestbedHarnessEnvironment.harnessForFixture(fixture, QualityReviewHarness);
  }

  it.each(states)('renders %s with only its applicable snapshot', async (state, label, visible) => {
    const page = await render();
    query.next(convertToParamMap({ state }));
    expect(await page.content()).toContain(label);
    expect(await page.exists('app-quality-history')).toBe(visible);
    if (!visible) expect(await page.content()).not.toContain('18 of 20');
  });

  it.each([
    [{ from: '2026-10-13', to: '2026-10-12' }, 'Invalid date range'],
    [{ from: '2025-10-12', to: '2026-10-13' }, 'Invalid date range'],
    [{ from: '2025-10-13', to: '2026-10-13' }, 'No snapshot supplied'],
    [{ from: '2026-10-01', to: '2026-10-13' }, 'No snapshot supplied'],
    [{ branch: 'release' }, 'No snapshot supplied'],
    [{ project: 'another-project' }, 'No snapshot supplied'],
  ] as const)(
    'never reuses historical rates for an unsupported scope: %o',
    async (params, text) => {
      const page = await render();
      expect(await page.exists('app-quality-history')).toBe(true);
      query.next(convertToParamMap(params));
      expect(await page.content()).toContain(text);
      expect(await page.exists('app-quality-history')).toBe(false);
    },
  );

  it('does not fill missing collection or latency measures with snapshot values', async () => {
    const page = await render();
    query.next(convertToParamMap({ state: 'zero' }));
    expect(await page.text('[data-metric="incomplete"]')).toContain('No data');
    expect(await page.text('[data-metric="incomplete"]')).not.toContain('%');
    query.next(convertToParamMap({ state: 'unavailable' }));
    expect(await page.text('[data-metric="p95"]')).toContain('Unavailable');
    expect(await page.content()).not.toContain('118 s');
  });

  it('retains the previous snapshot only while it is explicitly marked busy', async () => {
    query.next(convertToParamMap({ state: 'loading' }));
    const page = await render();
    expect(await page.attribute('[data-snapshot]', 'aria-busy')).toBe('true');
    expect(await page.exists('button[type="submit"]:disabled')).toBe(true);
    query.next(convertToParamMap({}));
    expect(await page.attribute('[data-snapshot]', 'aria-busy')).toBe('false');
  });
});
