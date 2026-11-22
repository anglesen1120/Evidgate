import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { qualityFixture } from './quality-overview.fixture';
import { QualityHistoryComponent } from './quality-history.component';
import { QualityReviewHarness } from './quality-review.harness';

describe('QualityHistoryComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('draws no line segment across a missing day', async () => {
    const fixture = TestBed.createComponent(QualityHistoryComponent);
    const observations = qualityFixture.days.filter((day) => day.observation !== null);
    const missing = { date: '2026-10-11', dayLabel: '11 Oct', observation: null };
    const days = [
      ...observations
        .slice(0, 1)
        .map((day) => ({ ...day, date: '2026-10-10', dayLabel: '10 Oct' })),
      missing,
      ...observations.slice(1),
    ];
    fixture.componentRef.setInput('days', days);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, QualityReviewHarness);
    expect(await panel.exists('circle.sample')).toBe(true);
    expect(await panel.text('figcaption')).toContain('No data');
    expect(await panel.exists('polyline.series')).toBe(false);
    expect(await panel.exists('polygon.area')).toBe(false);
  });

  it('provides the same missing day and two observed rates in chart and table modes', async () => {
    const fixture = TestBed.createComponent(QualityHistoryComponent);
    fixture.componentRef.setInput('days', qualityFixture.days);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, QualityReviewHarness);
    for (const label of ['No data', '80%', '90%'])
      expect(await panel.text('figcaption')).toContain(label);
    expect(await panel.text('[data-outcomes]')).toContain('18 passed · 2 failed · 0 other');
    expect(await panel.text('[data-outcomes]')).toContain('16 passed · 3 failed · 1 other');
    await panel.click('button[aria-controls]');
    expect(await panel.exists('figure')).toBe(false);
    const table = await panel.text('app-data-table');
    for (const label of ['2026-10-10', 'No data', '2026-10-11', '80%', '2026-10-12', '90%']) {
      expect(table).toContain(label);
    }
    expect(table).not.toContain('100%');
    await panel.click('button[aria-controls]');
    expect(await panel.exists('figure')).toBe(true);
  });
});
