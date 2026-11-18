import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  provideZonelessChangeDetection,
  viewChild,
  type TemplateRef,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { DataTableComponent } from './data-table.component';
import type { DataTableCellContext, DataTableColumn } from './data-table-column';

type Row = { id: string };
@Component({
  imports: [DataTableComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-template #details
      ><details>
        <summary i18n="@@test.summary">Inspect event</summary>
        <p i18n="@@test.meta">Metadata</p>
      </details></ng-template
    >
    <app-data-table
      [rows]="rows"
      [columns]="columns()"
      [rowId]="rowId"
      [caption]="'Security activity'"
      [flow]="'page'"
    />
  `,
})
class SummaryHost {
  readonly rows: readonly Row[] = [{ id: 'event-1' }];
  readonly rowId = (row: Row): string => row.id;
  private readonly details = viewChild<TemplateRef<DataTableCellContext<Row>>>('details');
  readonly columns = computed<readonly DataTableColumn<Row>[]>(() => {
    const details = this.details();
    return details
      ? [{ id: 'details', header: 'Details', cell: (row) => row.id, cellTemplate: details }]
      : [];
  });
}
class SummaryTableHarness extends ComponentHarness {
  static hostSelector = 'app-data-table';
  readonly summary = this.locatorFor('summary');
  readonly table = this.locatorForOptional('[role="table"]');
}

describe('Shared table native disclosure focus', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });
  it('hands the focused summary across table-to-records and records-to-table reflow', async () => {
    let width = 1280;
    const observers: {
      callback: ResizeObserverCallback;
      observer: ResizeObserver;
      targets: Set<Element>;
    }[] = [];
    vi.stubGlobal(
      'ResizeObserver',
      vi.fn(function (callback: ResizeObserverCallback) {
        const targets = new Set<Element>();
        const observer: ResizeObserver = {
          observe: (target) => {
            targets.add(target);
          },
          unobserve: (target) => {
            targets.delete(target);
          },
          disconnect: () => targets.clear(),
        };
        observers.push({ callback, observer, targets });
        return observer;
      }),
    );
    TestBed.configureTestingModule({
      imports: [SummaryHost],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(SummaryHost);
    fixture.detectChanges();
    const root = fixture.debugElement.query(By.directive(DataTableComponent))
      .nativeElement as HTMLElement;
    vi.spyOn(root, 'clientWidth', 'get').mockImplementation(() => width);
    const harness = await TestbedHarnessEnvironment.loader(fixture).getHarness(SummaryTableHarness);
    expect(await harness.table()).not.toBeNull();
    await (await harness.summary()).focus();
    for (const nextWidth of [360, 1280]) {
      width = nextWidth;
      for (const { callback, observer, targets } of observers)
        if (targets.has(root)) callback([], observer);
      await fixture.whenStable();
      expect(await (await harness.summary()).isFocused()).toBe(true);
      expect((await harness.table()) !== null).toBe(nextWidth === 1280);
    }
  });
});
