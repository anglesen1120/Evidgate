import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { QualityBreakdownComponent } from './quality-breakdown.component';
import { qualityFixture } from './quality-overview.fixture';
import { QualityReviewHarness } from './quality-review.harness';

describe('QualityBreakdownComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('keeps suite failure and the unbound worker visible independently of pass rate', async () => {
    const fixture = TestBed.createComponent(QualityBreakdownComponent);
    fixture.componentRef.setInput('model', qualityFixture);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, QualityReviewHarness);
    expect(await panel.text('[data-binding="worker"]')).toContain('No eligible run');
    expect(await panel.text('[data-binding="worker"]')).not.toContain('PASS');
    const suite = await panel.text('[data-binding="sr-27"]');
    for (const label of ['run-1842', 'Incomplete', 'Has failures', 'FAIL']) {
      expect(suite).toContain(label);
    }
    expect(await panel.exists('[data-binding="sr-27"] hlm-icon')).toBe(true);
  });

  it('shows the long case name as escaped text without inventing a last-flaked timestamp', async () => {
    const fixture = TestBed.createComponent(QualityBreakdownComponent);
    const row = {
      id: 'case-search-4',
      name: 'Search',
      longName: '<em>long case</em>',
      runs: 36,
      flakePercent: 8.3,
      lastFlaked: null,
    };
    fixture.componentRef.setInput('model', { ...qualityFixture, cases: [row] });
    fixture.componentRef.setInput('longNames', true);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, QualityReviewHarness);
    expect(await panel.text('app-data-table')).toContain('<em>long case</em>');
    expect(await panel.text('app-data-table')).toContain('Not supplied');
    expect(await panel.exists('em')).toBe(false);
  });
});
