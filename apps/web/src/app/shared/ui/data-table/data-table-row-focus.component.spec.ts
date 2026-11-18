import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { DataTableComponent } from './data-table.component';
import { DATA_TABLE_FIXTURE_COLUMNS, dataTableFixtureRows } from './data-table.fixture';
import type { DataTableFixtureRow } from './data-table.fixture';

class RowFocusHarness extends ComponentHarness {
  static hostSelector = 'app-data-table';
  row(id: string) {
    return this.locatorFor(`[data-row-id="${id}"]`)();
  }
}

describe('Shared table row-level focus across layouts', () => {
  let notifyResize: () => void;
  let width: number;
  beforeEach(() => {
    width = 1280;
    notifyResize = () => undefined;
    vi.stubGlobal(
      'ResizeObserver',
      vi.fn(function (callback: ResizeObserverCallback) {
        const observer: ResizeObserver = {
          observe: () => undefined,
          unobserve: () => undefined,
          disconnect: () => undefined,
        };
        notifyResize = () => callback([], observer);
        return observer;
      }),
    );
    TestBed.configureTestingModule({
      imports: [DataTableComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  async function render() {
    const fixture = TestBed.createComponent(DataTableComponent<DataTableFixtureRow>);
    const root = fixture.nativeElement as HTMLElement;
    vi.spyOn(root, 'clientWidth', 'get').mockImplementation(() => width);
    fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(2));
    fixture.componentRef.setInput('rowId', (row: DataTableFixtureRow) => row.id);
    fixture.componentRef.setInput('caption', 'Loaded identities');
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, RowFocusHarness);
    return { fixture, root, harness };
  }

  it('preserves a focused appended row itself from table to records and back', async () => {
    const { fixture, root, harness } = await render();
    fixture.componentRef.setInput('rows', dataTableFixtureRows(3));
    await fixture.whenStable();
    const source = root.querySelector<HTMLElement>('[data-row-id="demo-2"]');
    if (!source) throw new Error('Appended row is missing');
    source.tabIndex = -1;
    await (await harness.row('demo-2')).focus();
    expect(await (await harness.row('demo-2')).isFocused()).toBe(true);
    width = 480;
    notifyResize();
    await fixture.whenStable();
    expect(fixture.componentInstance.narrow()).toBe(true);
    expect(await (await harness.row('demo-2')).isFocused()).toBe(true);
    expect(await (await harness.row('demo-2')).getAttribute('tabindex')).toBe('-1');
    width = 1280;
    notifyResize();
    await fixture.whenStable();
    expect(fixture.componentInstance.narrow()).toBe(false);
    expect(await (await harness.row('demo-2')).isFocused()).toBe(true);
  });

  it('does not take row focus back after the visitor moves outside before reflow renders', async () => {
    const { fixture, root } = await render();
    const source = root.querySelector<HTMLElement>('[data-row-id="demo-1"]');
    if (!source) throw new Error('Second row is missing');
    source.tabIndex = -1;
    source.focus();
    const outside = document.createElement('input');
    document.body.append(outside);
    try {
      width = 480;
      notifyResize();
      outside.focus();
      await fixture.whenStable();
      expect(document.activeElement).toBe(outside);
    } finally {
      outside.remove();
    }
  });
});
