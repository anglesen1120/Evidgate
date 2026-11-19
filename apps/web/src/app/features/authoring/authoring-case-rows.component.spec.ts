import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import type { ManualCaseListItemDto } from '@evidgate/sdk';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { DataTableComponent } from '../../shared/ui/data-table/data-table.component';
import { AuthoringCaseRowsComponent } from './authoring-case-rows.component';

class RowsHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-case-rows';
  readonly selectAll = this.locatorFor('[data-select-visible] [role="checkbox"]');
  readonly rowBoxes = this.locatorForAll('[data-case-select] [role="checkbox"]');
  readonly summary = this.locatorFor('[data-selection-summary]');
  readonly exportAction = this.locatorFor('[data-export-selection]');
  readonly selectionNotice = this.locatorFor('[data-selection-notice]');
}
const row = (id: number): ManualCaseListItemDto => ({
  case_id: `018f0000-0000-7000-8000-${String(id).padStart(12, '0')}`,
  code: `SHOP-${id}`,
  title: 'Tuỳ chọn giao hàng và kiểm tra địa chỉ tại các thị trường bị hạn chế',
  node_id: '018f0000-0000-7000-8000-000000000101',
  checklist_item_id: '018f0000-0000-7000-8000-000000000201',
  state: 'draft',
  priority: 'high',
  version: 2,
  linked_live_count: 1,
  linked_case_ids: ['018f0000-0000-7000-8000-000000000777'],
});

describe('AuthoringCaseRowsComponent', () => {
  provideLayoutTestStubs();
  const desktop = signal(true);
  beforeEach(() => {
    desktop.set(true);
    TestBed.configureTestingModule({
      imports: [AuthoringCaseRowsComponent],
      providers: [
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: desktop,
            narrow: () => !desktop(),
            allowsMutation: () => desktop(),
          },
        },
      ],
    });
  });

  async function mount(count = 2) {
    const fixture = TestBed.createComponent(AuthoringCaseRowsComponent);
    fixture.componentRef.setInput(
      'rows',
      Array.from({ length: count }, (_, index) => row(index + 1)),
    );
    fixture.detectChanges();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, RowsHarness);
    return { fixture, harness };
  }

  it('selects one row, exposes mixed select-visible, and selects or clears visible rows only', async () => {
    const { harness } = await mount();
    await (await harness.rowBoxes())[0]?.click();
    expect(await (await harness.selectAll()).getAttribute('aria-checked')).toBe('mixed');
    expect(await (await harness.summary()).text()).toContain('1 of 2 loaded selected');
    await (await harness.selectAll()).click();
    expect(await (await harness.summary()).text()).toContain('2 of 2 loaded selected');
    await (await harness.selectAll()).click();
    expect(await (await harness.summary()).text()).toContain('0 of 2 loaded selected');
  });

  it('clears selection when a new filter result replaces rows', async () => {
    const { fixture, harness } = await mount();
    await (await harness.selectAll()).click();
    fixture.componentRef.setInput('rows', [row(3)]);
    fixture.detectChanges();
    expect(await (await harness.summary()).text()).toContain('0 of 1 loaded selected');
  });

  it('never enables export or hides its visible reason, and shows canonical linked UUIDs', async () => {
    const { harness } = await mount();
    expect(await (await harness.exportAction()).getAttribute('aria-disabled')).toBe('true');
    expect(await (await harness.host()).text()).toContain('cannot export explicit case IDs');
    expect(await (await harness.host()).text()).toContain('018f0000-0000-7000-8000-000000000777');
    expect(await (await harness.host()).text()).toContain('Pending change count unavailable');
  });

  it('removes export-selection controls below the viewport policy without hiding case facts', async () => {
    desktop.set(false);
    const { harness } = await mount();
    expect(await harness.rowBoxes()).toHaveLength(0);
    expect(await (await harness.host()).text()).toContain('SHOP-1');
    expect(await (await harness.host()).text()).toContain('at least 768 pixels');
  });

  it('hands focused selection back to the inspection notice when the viewport becomes narrow', async () => {
    const { fixture, harness } = await mount();
    await (await harness.selectAll()).focus();
    desktop.set(false);
    fixture.detectChanges();
    expect(await (await harness.selectionNotice()).isFocused()).toBe(true);
    expect(await harness.rowBoxes()).toHaveLength(0);
  });

  it('retains the selection when only the viewport narrows and widens again', async () => {
    const { fixture, harness } = await mount();
    await (await harness.selectAll()).click();
    expect(await (await harness.summary()).text()).toContain('2 of 2 loaded selected');
    desktop.set(false);
    fixture.detectChanges();
    expect(await harness.rowBoxes()).toHaveLength(0);
    desktop.set(true);
    fixture.detectChanges();
    expect(await (await harness.summary()).text()).toContain('2 of 2 loaded selected');
  });

  it('hands focus to the inspection notice when the export control disappears', async () => {
    const { fixture, harness } = await mount();
    await (await harness.exportAction()).focus();
    desktop.set(false);
    fixture.detectChanges();
    expect(await (await harness.selectionNotice()).isFocused()).toBe(true);
  });

  it('gives the case code its own column and keeps the UUID in a separate cell', async () => {
    const { fixture } = await mount(1);
    const table = fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent<ManualCaseListItemDto>);
    const columns = table.columns();
    const code = columns.find((column) => column.id === 'code');
    expect(code).toMatchObject({ role: 'identity', priority: 'primary' });
    expect(columns.find((column) => column.id === 'id')?.cell(row(1))).toBe(row(1).case_id);
    const codeCell = (fixture.nativeElement as HTMLElement).querySelector(
      'td [data-column-id="code"]',
    );
    const idCell = (fixture.nativeElement as HTMLElement).querySelector('td [data-column-id="id"]');
    expect((codeCell as HTMLElement).textContent).toContain('SHOP-1');
    expect((codeCell as HTMLElement).textContent).not.toContain(row(1).case_id);
    expect((idCell as HTMLElement).textContent).toContain(row(1).case_id);
  });

  it('drops a reveal request for a row missing from the result', async () => {
    const { fixture } = await mount();
    const host = fixture.debugElement.query(By.directive(AuthoringCaseRowsComponent));
    const rows = host.componentInstance as AuthoringCaseRowsComponent;
    const missing = '018f0000-0000-7000-8000-000000000999';
    rows.focusRow(missing);
    fixture.detectChanges();
    fixture.componentRef.setInput('rows', [row(1), { ...row(2), case_id: missing }]);
    fixture.detectChanges();
    await fixture.whenStable();
    const active = (fixture.nativeElement as HTMLElement).ownerDocument
      .activeElement as HTMLElement | null;
    expect(active?.closest('[data-row-id]')?.getAttribute('data-row-id')).not.toBe(missing);
  });

  it('uses the shared 200-row threshold and opts fixed-height cells into virtualization', async () => {
    const { fixture } = await mount(200);
    const table = fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent<ManualCaseListItemDto>);
    expect(table.virtualThreshold()).toBe(200);
    expect(table.fixedHeightCells()).toBe(true);
    fixture.componentRef.setInput(
      'rows',
      Array.from({ length: 201 }, (_, index) => row(index + 1)),
    );
    fixture.detectChanges();
    expect(table.rows()).toHaveLength(201);
  });
});
