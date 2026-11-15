import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { CopyableIdentifierComponent } from '../values/copyable-identifier.component';
import { DataTableComponent } from './data-table.component';
import { DATA_TABLE_FIXTURE_COLUMNS, dataTableFixtureRows } from './data-table.fixture';
import type { DataTableFixtureRow } from './data-table.fixture';

describe('Shared table fixed-stride identifier recovery', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DataTableComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  async function render(width = 1024) {
    const fixture = TestBed.createComponent(DataTableComponent<DataTableFixtureRow>);
    const root = fixture.nativeElement as HTMLElement;
    vi.spyOn(root, 'clientWidth', 'get').mockReturnValue(width);
    fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    fixture.componentRef.setInput('rowId', (row: DataTableFixtureRow) => row.id);
    fixture.componentRef.setInput('caption', 'Evidence');
    await fixture.whenStable();
    return { fixture, root };
  }

  it('uses the existing no-wrap compact identifier with full-value and copy recovery in virtual rows', async () => {
    const { fixture, root } = await render();
    expect(fixture.componentInstance.virtual()).toBe(true);
    const debug = fixture.debugElement.query(By.directive(CopyableIdentifierComponent));
    const identifier = debug.componentInstance as CopyableIdentifierComponent;
    expect(identifier.compact()).toBe(true);
    expect(identifier.truncate()).toBe(true);
    expect(identifier.value()).toBe(dataTableFixtureRows(1)[0]!.hash);
    expect(identifier.label()).toBe('Evidence identity');
    const element = debug.nativeElement as HTMLElement;
    expect(element.querySelector('.identifier.compact')).not.toBeNull();
    expect(element.querySelector('button[aria-haspopup="dialog"]')).not.toBeNull();
    expect(element.querySelector('app-copy-value-button button')).not.toBeNull();
    expect(root.querySelector('.eg-table-pagination')).toBeNull();
  });

  it('restores ordinary identifier presentation at the 200-row boundary', async () => {
    const { fixture } = await render();
    fixture.componentRef.setInput('rows', dataTableFixtureRows(200));
    await fixture.whenStable();
    expect(fixture.componentInstance.virtual()).toBe(false);
    const identifier = fixture.debugElement.query(By.directive(CopyableIdentifierComponent))
      .componentInstance as CopyableIdentifierComponent;
    expect(identifier.compact()).toBe(false);
    expect(identifier.truncate()).toBe(true);
  });

  it('keeps complete non-compact values in paged narrow records', async () => {
    const { fixture, root } = await render(390);
    expect(fixture.componentInstance.narrow()).toBe(true);
    expect(fixture.componentInstance.virtual()).toBe(false);
    const identifier = fixture.debugElement.query(By.directive(CopyableIdentifierComponent))
      .componentInstance as CopyableIdentifierComponent;
    expect(identifier.compact()).toBe(false);
    expect(identifier.truncate()).toBe(false);
    expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(root.querySelectorAll('.eg-table-record')).toHaveLength(200);
    expect(root.querySelector('.eg-table-pagination')).not.toBeNull();
  });
});
