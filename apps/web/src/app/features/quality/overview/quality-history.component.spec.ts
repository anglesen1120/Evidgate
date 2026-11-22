import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { qualityFixture } from './quality-overview.fixture';
import { QualityHistoryComponent } from './quality-history.component';
import { QualityReviewHarness } from './quality-review.harness';

describe('QualityHistoryComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('renders a shared empty state instead of chart geometry or an outcomes list for no days', async () => {
    const fixture = TestBed.createComponent(QualityHistoryComponent);
    fixture.componentRef.setInput('days', []);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, QualityReviewHarness);
    expect(await panel.text('app-empty-pattern')).toContain('No data');
    expect(await panel.exists('figure, app-data-table, [data-outcomes]')).toBe(false);
    fixture.componentRef.setInput('days', qualityFixture.days);
    expect(await panel.exists('figure')).toBe(true);
  });

  it.each([
    ['vi', '12/10/2026'],
    ['en-US', '10/12/2026'],
  ])('labels the date axis with a four-digit year in %s', async (locale, label) => {
    registerLocaleData(localeVi);
    TestBed.configureTestingModule({ providers: [{ provide: LOCALE_ID, useValue: locale }] });
    const fixture = TestBed.createComponent(QualityHistoryComponent);
    fixture.componentRef.setInput('days', qualityFixture.days);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, QualityReviewHarness);
    expect(await panel.text('figcaption time[datetime="2026-10-12"]')).toBe(label);
  });

  it('draws no line segment across a missing day', async () => {
    const fixture = TestBed.createComponent(QualityHistoryComponent);
    const observations = qualityFixture.days.filter((day) => day.observation !== null);
    const missing = { date: '2026-10-11', observation: null };
    const days = [
      ...observations.slice(0, 1).map((day) => ({ ...day, date: '2026-10-10' })),
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
