import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideGalleryLayout } from './gallery-layout.testing';
import { GalleryExampleComponent } from './gallery-example.component';
import type { GallerySpecimen, GalleryState } from './gallery-sections';

class ExampleHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-example';
  readonly filter = this.locatorFor('[role="checkbox"]');
  readonly search = this.locatorFor('input[type="search"]');
  readonly destinations = this.locatorForAll('nav a');
  readonly statuses = this.locatorForAll('app-review-status-chip [data-slot="badge"]');
  readonly record = this.locatorFor('code');
  readonly next = this.locatorFor('nav button:last-child');
  readonly previous = this.locatorFor('nav button:first-child');
  readonly tableRows = this.locatorForAll('table tbody tr');
  readonly sparkline = this.locatorForOptional('table svg.trend[aria-hidden="true"]');
}

async function render(kind: GallerySpecimen['id'], state: GalleryState) {
  const fixture = TestBed.createComponent(GalleryExampleComponent);
  fixture.componentRef.setInput('kind', kind);
  fixture.componentRef.setInput('state', state);
  return TestbedHarnessEnvironment.harnessForFixture(fixture, ExampleHarness);
}

describe('GalleryExampleComponent', () => {
  provideGalleryLayout();

  it('shows the evidence rows and accessible trend text until the table has no samples', async () => {
    const fixture = TestBed.createComponent(GalleryExampleComponent);
    fixture.componentRef.setInput('kind', 'table');
    fixture.componentRef.setInput('state', 'default');
    const sample = await TestbedHarnessEnvironment.harnessForFixture(fixture, ExampleHarness);
    const rows = await sample.tableRows();
    expect(rows).toHaveLength(2);
    expect(await rows[0]?.text()).toContain('Duration rose from 2 to 5 seconds.');
    expect(await rows[1]?.text()).toContain('Not evaluated');
    expect(await sample.sparkline()).not.toBeNull();

    fixture.componentRef.setInput('state', 'empty');
    const emptyRows = await sample.tableRows();
    expect(emptyRows).toHaveLength(1);
    expect(await emptyRows[0]?.text()).toBe('No records match this view.');
    expect(await sample.sparkline()).toBeNull();
  });

  it('filters the miniature navigation without changing its real destinations', async () => {
    const sample = await render('shell', 'default');
    const input = await sample.search();
    expect(await sample.destinations()).toHaveLength(2);
    await input.setInputValue('release');
    await input.dispatchEvent('input');
    const matches = await sample.destinations();
    expect(matches).toHaveLength(1);
    expect(await matches[0]?.getAttribute('href')).toBe('#gallery-decisions');
    await input.setInputValue('no-match');
    await input.dispatchEvent('input');
    expect(await sample.destinations()).toHaveLength(0);
  });

  it('filters the snapshot without changing the overdue status of a recorded item', async () => {
    const sample = await render('queue', 'active');
    const tones = async () =>
      Promise.all((await sample.statuses()).map((badge) => badge.getAttribute('data-tone')));
    expect(await tones()).toEqual(['warning']);
    await (await sample.filter()).click();
    expect(await tones()).toEqual(['pending', 'warning']);
    await (await sample.filter()).click();
    expect(await tones()).toEqual(['warning']);
  });

  it('never turns an on-time record overdue just by applying the filter', async () => {
    const sample = await render('queue', 'default');
    expect(await sample.statuses()).toHaveLength(2);
    await (await sample.filter()).click();
    expect(await sample.statuses()).toHaveLength(0);
  });

  it('moves between actual example records and disables the unavailable direction', async () => {
    const sample = await render('pagination', 'default');
    expect(await (await sample.previous()).getProperty('disabled')).toBe(true);
    expect(await (await sample.record()).text()).toBe('cand-73');
    await (await sample.next()).click();
    expect(await (await sample.record()).text()).toBe('cand-77');
    expect(await (await sample.next()).getProperty('disabled')).toBe(true);
    await (await sample.previous()).click();
    expect(await (await sample.record()).text()).toBe('cand-73');
  });
});
