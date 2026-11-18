import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { resolveTableColumns } from './data-table-column';
import { DataTableComponent } from './data-table.component';
import { DATA_TABLE_FIXTURE_COLUMNS, dataTableFixtureRows } from './data-table.fixture';
import type { DataTableFixtureRow } from './data-table.fixture';

class VirtualTrackHarness extends ComponentHarness {
  static hostSelector = 'app-data-table';
  readonly table = this.locatorFor('table.header-table');
  readonly subject = this.locatorFor('table.header-table col');
}

describe('Shared virtual table explicit column tracks', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DataTableComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });

  it('keeps the default subject track and applies an explicit width only in virtual mode', async () => {
    const fixture = TestBed.createComponent(DataTableComponent<DataTableFixtureRow>);
    fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    fixture.componentRef.setInput('rowId', (row: DataTableFixtureRow) => row.id);
    fixture.componentRef.setInput('caption', 'Loaded identities');
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, VirtualTrackHarness);
    expect(fixture.componentInstance.virtual()).toBe(true);
    expect(await (await harness.table()).getAttribute('style')).toContain('9.5rem');
    const columns = DATA_TABLE_FIXTURE_COLUMNS.map((column, index) =>
      index === 0 ? { ...column, virtualWidth: '48ch' } : column,
    );
    fixture.componentRef.setInput('columns', columns);
    await fixture.whenStable();
    expect(await (await harness.table()).getAttribute('style')).toContain('48ch');
    expect(await (await harness.subject()).getAttribute('style')).toContain('48ch');
    fixture.componentRef.setInput('rows', dataTableFixtureRows(200));
    await fixture.whenStable();
    expect(fixture.componentInstance.virtual()).toBe(false);
    expect((await (await harness.subject()).getAttribute('style')) ?? '').not.toContain('48ch');
    expect((await (await harness.table()).getAttribute('style')) ?? '').not.toContain('48ch');
    expect(resolveTableColumns(columns)[0]?.virtualWidth).toBeNull();
    expect(resolveTableColumns(columns, dataTableFixtureRows(1))[0]?.virtualWidth).toBe('48ch');
  });
});
