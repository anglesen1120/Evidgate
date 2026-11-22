import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { GalleryQualitySnapshotComponent } from './gallery-quality-snapshot.component';

class SnapshotHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-quality-snapshot';
  readonly tableToggle = this.locatorFor('summary');
  readonly details = this.locatorFor('details');
  readonly rows = this.locatorForAll('tbody tr');
  readonly bars = this.locatorForAll('[data-bar]');
  readonly totals = this.locatorForAll('[data-breakdown] li');
  readonly limits = this.locatorFor('[data-limits]');
}

describe('Quality snapshot specimen', () => {
  it('shows the chart values as a table without promoting missing outcomes to zero or pass', async () => {
    const fixture = TestBed.createComponent(GalleryQualitySnapshotComponent);
    const sample = await TestbedHarnessEnvironment.harnessForFixture(fixture, SnapshotHarness);
    expect(await sample.bars()).toHaveLength(2);
    expect(await (await sample.tableToggle()).text()).toBe('View as table');
    expect(await (await sample.details()).getProperty('open')).toBe(false);
    await (await sample.tableToggle()).click();
    expect(await (await sample.details()).getProperty('open')).toBe(true);
    const rows = await sample.rows();
    expect(await rows[0]?.text()).toMatch(/2026-10-11\s*16\s*3\s*1/);
    expect(await rows[1]?.text()).toMatch(/2026-10-12\s*18\s*2\s*0/);
    const totals = await Promise.all((await sample.totals()).map((row) => row.text()));
    expect(totals[0]).toMatch(/Pass\s*34\s*85%/);
    expect(totals[2]).toMatch(/Other · unspecified\s*1\s*2\.5%/);
    expect(await (await sample.limits()).text()).toContain(
      'INCONCLUSIVE and Pending counts were not supplied',
    );
  });
});
