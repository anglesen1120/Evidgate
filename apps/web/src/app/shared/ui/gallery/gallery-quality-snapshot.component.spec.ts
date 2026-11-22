import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { GalleryQualitySnapshotComponent } from './gallery-quality-snapshot.component';

registerLocaleData(localeVi);

class SnapshotHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-quality-snapshot';
  readonly tableToggle = this.locatorFor('summary');
  readonly details = this.locatorFor('details');
  readonly rows = this.locatorForAll('tbody tr');
  readonly bars = this.locatorForAll('[data-bar]');
  readonly totals = this.locatorForAll('[data-breakdown] li');
  readonly limits = this.locatorFor('[data-limits]');
  readonly dates = this.locatorForAll('time[datetime]');
  readonly period = this.locatorFor('header [data-period]');
  readonly rate = this.locatorFor('#gallery-rate-title');
  readonly comparison = this.locatorFor('[data-comparison]');
  readonly compare = this.locatorFor('[role="switch"]');
  readonly comparisonNotes = this.locatorForAll('[data-comparison]');
}

describe('Quality snapshot specimen', () => {
  it.each([
    ['en', '10/11/2026', '10/12/2026'],
    ['vi', '11/10/2026', '12/10/2026'],
  ])(
    'uses full %s calendar dates in every chart, table and comparison label',
    async (locale, first, last) => {
      TestBed.configureTestingModule({ providers: [{ provide: LOCALE_ID, useValue: locale }] });
      const fixture = TestBed.createComponent(GalleryQualitySnapshotComponent);
      const sample = await TestbedHarnessEnvironment.harnessForFixture(fixture, SnapshotHarness);
      await (await sample.tableToggle()).click();
      const dates = await sample.dates();
      expect(await Promise.all(dates.map((date) => date.text()))).toEqual([
        first,
        last,
        first,
        last,
      ]);
      expect(await Promise.all(dates.map((date) => date.getAttribute('datetime')))).toEqual([
        '2026-10-11',
        '2026-10-12',
        '2026-10-11',
        '2026-10-12',
      ]);
      expect(await (await sample.period()).text()).toContain(`${first}–${last}`);
      expect(await (await sample.rate()).text()).toContain(last);
      expect(await (await sample.comparison()).text()).toContain(first);
      const bars = await sample.bars();
      expect(await bars[0]?.getAttribute('aria-label')).toContain(first);
      expect(await bars[1]?.getAttribute('aria-label')).toContain(last);
      await (await sample.compare()).click();
      expect(await sample.comparisonNotes()).toHaveLength(0);
      expect(await (await sample.rate()).text()).toContain(last);
    },
  );

  it('shows the chart values as a table without promoting missing outcomes to zero or pass', async () => {
    const fixture = TestBed.createComponent(GalleryQualitySnapshotComponent);
    const sample = await TestbedHarnessEnvironment.harnessForFixture(fixture, SnapshotHarness);
    expect(await sample.bars()).toHaveLength(2);
    expect(await (await sample.tableToggle()).text()).toBe('View as table');
    expect(await (await sample.details()).getProperty('open')).toBe(false);
    await (await sample.tableToggle()).click();
    expect(await (await sample.details()).getProperty('open')).toBe(true);
    const rows = await sample.rows();
    expect(await rows[0]?.text()).toMatch(/10\/11\/2026\s*16\s*3\s*1/);
    expect(await rows[1]?.text()).toMatch(/10\/12\/2026\s*18\s*2\s*0/);
    const totals = await Promise.all((await sample.totals()).map((row) => row.text()));
    expect(totals[0]).toMatch(/Pass\s*34\s*85%/);
    expect(totals[2]).toMatch(/Other · unspecified\s*1\s*2\.5%/);
    expect(await (await sample.limits()).text()).toContain(
      'INCONCLUSIVE and Pending counts were not supplied',
    );
  });
});
