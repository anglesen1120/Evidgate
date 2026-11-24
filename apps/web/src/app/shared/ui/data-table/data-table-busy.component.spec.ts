import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DataTableComponent } from './data-table.component';
import { DATA_TABLE_FIXTURE_COLUMNS, dataTableFixtureRows } from './data-table.fixture';

class BusyTableHarness extends ComponentHarness {
  static hostSelector = 'app-data-table';
  readonly semantics = this.locatorFor('[role="table"]');
  readonly announcement = this.locatorFor('app-live-status [role="status"]');
  readonly rows = this.locatorForAll('[data-row-id]');
  readonly skeletons = this.locatorForAll('[hlmSkeleton]');
  readonly indicator = this.locatorForOptional('[data-table-progress]');
}

describe('Data table fetch feedback', () => {
  afterEach(() => vi.useRealTimers());

  function setup(count: number) {
    const fixture = TestBed.createComponent(DataTableComponent);
    fixture.componentRef.setInput('caption', 'Records');
    fixture.componentRef.setInput('busy', false);
    fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(count));
    fixture.componentRef.setInput('rowId', (row: { id: string }) => row.id);
    fixture.detectChanges();
    return {
      fixture,
      harness: TestbedHarnessEnvironment.harnessForFixture(fixture, BusyTableHarness),
    };
  }

  it('keeps served rows throughout a page read, showing slow progress without repeating the loading announcement', async () => {
    const { fixture, harness } = setup(2);
    const table = await harness;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    expect(await (await table.semantics()).getAttribute('aria-busy')).toBe('true');
    expect(await table.indicator()).toBeNull();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    expect(await table.rows()).toHaveLength(2);
    expect(await table.indicator()).not.toBeNull();
    expect(await (await table.indicator())?.getAttribute('aria-hidden')).toBeNull();
    expect(await (await table.indicator())?.getAttribute('role')).toBe('progressbar');
    expect(await (await table.indicator())?.getAttribute('aria-label')).toBe('Loading…');
    fixture.componentRef.setInput('progressAtPager', true);
    fixture.detectChanges();
    expect(await (await table.indicator())?.getAttribute('aria-hidden')).toBe('true');
    expect(await (await table.indicator())?.getAttribute('role')).toBeNull();
    expect(await (await table.announcement()).text()).toBe('');
    fixture.componentRef.setInput('busy', false);
    fixture.detectChanges();
    expect(await table.indicator()).toBeNull();
    expect(await (await table.announcement()).text()).toBe('');
  });

  it('cancels a fast read and shows decorative skeletons only for a slow initial read', async () => {
    const { fixture, harness } = setup(0);
    const table = await harness;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    vi.advanceTimersByTime(100);
    fixture.componentRef.setInput('busy', false);
    fixture.detectChanges();
    vi.advanceTimersByTime(200);
    expect(await table.indicator()).toBeNull();
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    expect(await (await table.announcement()).text()).toBe('Loading…');
    expect(await table.skeletons()).toHaveLength(3);
    for (const row of await table.skeletons())
      expect(await row.getAttribute('aria-hidden')).toBe('true');
  });
});
