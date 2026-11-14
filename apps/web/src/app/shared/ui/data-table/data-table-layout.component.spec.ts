import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { CopyableIdentifierComponent } from '../values/copyable-identifier.component';
import type { DataTableCellContext, DataTableColumn } from './data-table-column';
import { resolveTableColumns } from './data-table-column';
import { DataTableComponent } from './data-table.component';
import { DATA_TABLE_FIXTURE_COLUMNS, dataTableFixtureRows } from './data-table.fixture';
import type { DataTableFixtureRow } from './data-table.fixture';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<ng-template #cell let-row>{{ row.note }}</ng-template>',
})
class CellTemplateHost {
  readonly template =
    viewChild.required<TemplateRef<DataTableCellContext<DataTableFixtureRow>>>('cell');
}

const columns = DATA_TABLE_FIXTURE_COLUMNS.map((column) => {
  const textColumn = { ...column };
  delete textColumn.kind;
  return textColumn;
});

describe('Shared table column layout and reflow', () => {
  let fixture: ComponentFixture<DataTableComponent<DataTableFixtureRow>>;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DataTableComponent, CellTemplateHost],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(DataTableComponent<DataTableFixtureRow>);
    root = fixture.nativeElement as HTMLElement;
    fixture.componentRef.setInput('columns', columns);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(2));
    fixture.componentRef.setInput('rowId', (row: DataTableFixtureRow) => row.id);
    fixture.componentRef.setInput('caption', 'Evidence / Bằng chứng');
    await fixture.whenStable();
  });

  it('keeps legacy column inputs while resolving explicit roles, priorities and overflow', () => {
    const legacy: readonly DataTableColumn<DataTableFixtureRow>[] = [
      { id: 'hash', header: 'Identity', cell: (row) => row.hash, truncate: true },
      { id: 'count', header: 'Attempts', cell: (row) => row.attempts, align: 'end' },
      { id: 'mutation', header: 'Mutation', cell: (row) => row.mutation, mutates: true },
    ];
    expect(
      resolveTableColumns(legacy).map(({ role, priority, overflow }) => ({
        role,
        priority,
        overflow,
      })),
    ).toEqual([
      { role: 'identity', priority: 'primary', overflow: 'truncate' },
      { role: 'number', priority: 'secondary', overflow: 'wrap' },
      { role: 'actions', priority: 'primary', overflow: 'wrap' },
    ]);
    const resolved = resolveTableColumns([
      { ...legacy[0]!, role: 'text', priority: 'secondary', overflow: 'wrap' },
    ]);
    expect(resolved[0]).toMatchObject({ role: 'text', priority: 'secondary', overflow: 'wrap' });
  });

  it('uses one auto-layout native table with subject and numeric header/cell metadata', () => {
    expect(root.querySelectorAll('.table')).toHaveLength(1);
    expect(root.querySelector('.table')?.classList.contains('table-fixed')).toBe(false);
    expect(root.querySelector('th')?.getAttribute('data-column-priority')).toBe('primary');
    for (const cell of root.querySelectorAll('th.numeric,td.numeric')) {
      expect(cell.getAttribute('data-column-role')).toBe('number');
      expect(cell.classList.contains('[&.numeric]:tabular-nums')).toBe(true);
      expect(cell.classList.contains('[&.numeric]:text-right')).toBe(true);
    }
    expect(root.querySelector('td.numeric .value')?.classList.contains('whitespace-nowrap')).toBe(
      true,
    );
  });

  it('retains numeric precision beyond a review budget in stable virtual tracks', () => {
    const precisionColumns: readonly DataTableColumn<DataTableFixtureRow>[] = [
      columns[0]!,
      { id: 'count', header: 'Count', cell: () => '12345678901234567890 ms', role: 'number' },
    ];
    const resolved = resolveTableColumns(precisionColumns, dataTableFixtureRows(1));
    expect(resolved[0]?.virtualWidth).toBeNull();
    expect(resolved[1]?.virtualWidth).toBe('calc(23ch + var(--eg-space-4) * 2)');
  });

  it.each(['light', 'dark'])(
    'preserves named locally sticky header structure in %s theme',
    (theme) => {
      root.setAttribute('data-theme', theme);
      const scroll = root.querySelector('.eg-table-long');
      const header = root.querySelector('.eg-table-sticky');
      expect(scroll?.contains(header)).toBe(true);
      expect(header?.querySelectorAll('th[scope="col"]')).toHaveLength(4);
      expect(header?.closest('cdk-virtual-scroll-viewport')).toBeNull();
      expect(root.classList.contains('bg-card')).toBe(true);
      // jsdom has no real sticky positioning or token cascade; browser QA measures those.
    },
  );

  it('replaces narrow tables with P1 identity/state then every labelled P2 fact', async () => {
    const orderedColumns: readonly DataTableColumn<DataTableFixtureRow>[] = [
      columns[1]!,
      { ...columns[2]!, role: 'status', header: 'State / Trạng thái' },
      { ...columns[0]!, priority: 'primary' },
      { ...columns[2]!, id: 'count' },
      columns[3]!,
    ];
    fixture.componentRef.setInput('columns', orderedColumns);
    fixture.componentRef.setInput('rows', [
      { ...dataTableFixtureRows(1)[0]!, note: 'Nguyễn kiểm thử quyền '.repeat(30) },
    ]);
    fixture.componentInstance.narrow.set(true);
    await fixture.whenStable();
    expect(root.querySelector('[role="table"],cdk-virtual-scroll-viewport')).toBeNull();
    const record = root.querySelector('.eg-table-record')!;
    expect(
      Array.from(record.querySelectorAll('dt')).map((label) => label.textContent?.trim()),
    ).toEqual(['State / Trạng thái', 'Evidence identity', 'Context', 'Attempts']);
    expect(record.textContent).toContain('Nguyễn kiểm thử quyền '.repeat(30));
    expect(record.querySelectorAll('dd')).toHaveLength(4);
    expect(record.textContent).not.toContain('Mutation');
    expect(record.querySelector('.truncate,.numeric')).toBeNull();
    expect(record.querySelector('ol')).toBeNull();
    expect(root.querySelector('ol')?.getAttribute('aria-label')).toBe('Evidence / Bằng chứng');
  });

  it('uses the available container width, not only the browser viewport, for stacked records', async () => {
    const width = vi.spyOn(root, 'clientWidth', 'get').mockReturnValue(376);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(3));
    await fixture.whenStable();
    expect(root.querySelectorAll('.eg-table-record')).toHaveLength(3);
    width.mockReturnValue(1000);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(2));
    await fixture.whenStable();
    expect(root.querySelector('.table-semantics')).not.toBeNull();
    expect(root.querySelector('.eg-table-record')).toBeNull();
  });

  it('bounds variable-height narrow records at 200 while keeping every loaded record reachable', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    fixture.componentInstance.narrow.set(true);
    await fixture.whenStable();
    expect(root.querySelectorAll('.eg-table-record')).toHaveLength(200);
    expect(root.querySelector('[role="status"]')?.textContent).toContain(
      'Loaded records 1–200 of 201',
    );
    const buttons = root.querySelectorAll<HTMLButtonElement>('.eg-table-pagination button');
    expect(buttons[0]?.disabled).toBe(true);
    buttons[1]!.click();
    await fixture.whenStable();
    expect(root.querySelectorAll('.eg-table-record')).toHaveLength(1);
    expect(root.querySelector('.eg-table-record')?.getAttribute('data-row-id')).toBe('demo-200');
    expect(root.querySelector('[role="status"]')?.textContent).toContain('201–201 of 201');
    expect(buttons[1]?.disabled).toBe(true);
    expect(root.querySelector('.scroll')?.contains(buttons[0]!)).toBe(false);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(1));
    await fixture.whenStable();
    expect(root.querySelector('.eg-table-record')?.getAttribute('data-row-id')).toBe('demo-0');
    expect(root.querySelector('.eg-table-pagination')).toBeNull();
  });

  it('uses bounded pages rather than fixed-height virtual rows for arbitrary cell templates', async () => {
    const host = TestBed.createComponent(CellTemplateHost);
    await host.whenStable();
    fixture.componentRef.setInput('columns', [
      { ...columns[1]!, cellTemplate: host.componentInstance.template() },
    ]);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    await fixture.whenStable();
    expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(root.querySelectorAll('.body-table [data-row-id]')).toHaveLength(200);
    expect(root.querySelector('.eg-table-pagination')).not.toBeNull();
    expect(root.querySelector('.body-table')?.textContent).toContain(
      dataTableFixtureRows(1)[0]!.note,
    );
    host.destroy();
  });

  it('grows virtual stride to the text probe at 200% and forces narrative previews into disclosure', async () => {
    const probe = root.querySelector<HTMLElement>('.token-probe')!;
    probe.style.height = '88px';
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    await fixture.whenStable();
    expect(fixture.componentInstance.itemSize()).toBe(88);
    expect(root.querySelector<HTMLElement>('.body-table [data-row-id]')?.style.height).toBe('88px');
    const narrative = root.querySelector('.body-table td[aria-colindex="2"] app-data-table-value');
    expect(narrative?.querySelector('.value')?.classList.contains('truncate')).toBe(true);
    expect(narrative?.classList.contains('overflow-hidden')).toBe(false);
    expect(narrative?.textContent).toContain(dataTableFixtureRows(1)[0]!.note);
    // An 88px model checks stride/disclosure wiring, not actual zoomed glyph geometry.
  });

  it('passes the original machine identifier and a contextual copy label to the shared value family', async () => {
    fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
    await fixture.whenStable();
    const identifier = fixture.debugElement.query(By.directive(CopyableIdentifierComponent))
      .componentInstance as CopyableIdentifierComponent;
    expect(identifier.value()).toBe(dataTableFixtureRows(1)[0]!.hash);
    expect(identifier.label()).toBe('Evidence identity');
    expect(identifier.truncate()).toBe(true);
  });
});
