import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  signal,
  TemplateRef,
  viewChild,
  provideZonelessChangeDetection,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../testing/layout.testing';
import type { DataTableCellContext, DataTableColumn } from './data-table-column';
import { DataTableComponent } from './data-table.component';

type Row = { id: string; name: string };

@Component({
  selector: 'app-fixed-template-host',
  imports: [DataTableComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-data-table
      [columns]="columns()"
      [rows]="rows()"
      [rowId]="rowId"
      [caption]="caption"
      [fixedHeightCells]="fixed()"
    />
    <ng-template #name let-row
      ><span data-custom-cell>{{ row.name }}</span></ng-template
    >
  `,
})
class FixedTemplateHost {
  readonly caption = 'Template rows';
  readonly fixed = signal(false);
  readonly rows = signal(
    Array.from({ length: 201 }, (_, id) => ({ id: String(id), name: `Row ${id}` })),
  );
  readonly rowId = (row: Row) => row.id;
  readonly table = viewChild.required(DataTableComponent<Row>);
  private readonly name = viewChild.required<TemplateRef<DataTableCellContext<Row>>>('name');
  readonly columns = computed<readonly DataTableColumn<Row>[]>(() => [
    { id: 'name', header: 'Name', cell: (row) => row.name, cellTemplate: this.name() },
  ]);
}

class FixedTemplateHarness extends ComponentHarness {
  static hostSelector = 'app-fixed-template-host';
  readonly viewport = this.locatorForOptional('cdk-virtual-scroll-viewport');
  readonly customCells = this.locatorForAll('[data-custom-cell]');
  readonly pagination = this.locatorForOptional('.eg-table-pagination');
}

describe('DataTable fixed-height template opt-in', () => {
  provideLayoutTestStubs();
  beforeEach(() => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1024);
    TestBed.configureTestingModule({
      imports: [FixedTemplateHost],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => vi.restoreAllMocks());

  it('keeps existing template paging by default and virtualizes 201 fixed-height template rows when opted in', async () => {
    const fixture = TestBed.createComponent(FixedTemplateHost);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      FixedTemplateHarness,
    );
    expect(await harness.viewport()).toBeNull();
    expect(await harness.pagination()).not.toBeNull();
    fixture.componentInstance.fixed.set(true);
    await fixture.whenStable();
    expect(await harness.viewport()).not.toBeNull();
    expect(await harness.pagination()).toBeNull();
    const cells = await harness.customCells();
    expect(cells.length).toBeGreaterThan(0);
    expect(cells.length).toBeLessThan(201);
    expect(await cells[0]?.text()).toBe('Row 0');
    fixture.componentInstance.rows.update((rows) => rows.slice(0, 200));
    await fixture.whenStable();
    expect(await harness.viewport()).toBeNull();
    expect(await harness.customCells()).toHaveLength(200);
  });
});
