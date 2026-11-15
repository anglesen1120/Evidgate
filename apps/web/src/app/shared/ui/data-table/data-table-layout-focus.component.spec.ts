import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { DataTableCellContext, DataTableColumn } from './data-table-column';
import { DataTableComponent } from './data-table.component';
import { DATA_TABLE_FIXTURE_COLUMNS, dataTableFixtureRows } from './data-table.fixture';
import type { DataTableFixtureRow } from './data-table.fixture';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-template #identity let-row let-stacked="stacked">
      <a
        [href]="'#' + row.id"
        [attr.aria-label]="stacked ? row.hash : 'Open identity'"
        [textContent]="stacked ? row.hash : 'Open'"
      ></a>
      <button type="button" [attr.aria-label]="'Copy identity'" [textContent]="'Copy'"></button>
    </ng-template>
    <ng-template #other>
      <button type="button" [attr.aria-label]="'Copy identity'" [textContent]="'Copy'"></button>
    </ng-template>
  `,
})
class FocusTemplateHost {
  readonly identity =
    viewChild.required<TemplateRef<DataTableCellContext<DataTableFixtureRow>>>('identity');
  readonly other =
    viewChild.required<TemplateRef<DataTableCellContext<DataTableFixtureRow>>>('other');
}

interface ObservedResize {
  readonly callback: ResizeObserverCallback;
  readonly observer: ResizeObserver;
  readonly targets: Set<Element>;
}

describe('Shared table focus across record reflow', () => {
  let fixture: ComponentFixture<DataTableComponent<DataTableFixtureRow>>;
  let templates: ComponentFixture<FocusTemplateHost>;
  let root: HTMLElement;
  let width: number;
  let observers: ObservedResize[];
  let columns: readonly DataTableColumn<DataTableFixtureRow>[];

  beforeEach(async () => {
    width = 1280;
    observers = [];
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
    await TestBed.configureTestingModule({
      imports: [DataTableComponent, FocusTemplateHost],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    templates = TestBed.createComponent(FocusTemplateHost);
    await templates.whenStable();
    columns = [
      {
        id: 'other',
        header: 'Other fact',
        role: 'text',
        priority: 'secondary',
        cell: (row) => row.note,
        cellTemplate: templates.componentInstance.other(),
      },
      {
        id: 'identity',
        header: 'Identity',
        role: 'identity',
        priority: 'primary',
        cell: (row) => row.hash,
        cellTemplate: templates.componentInstance.identity(),
      },
    ];
    fixture = TestBed.createComponent(DataTableComponent<DataTableFixtureRow>);
    root = fixture.nativeElement as HTMLElement;
    vi.spyOn(root, 'clientWidth', 'get').mockImplementation(() => width);
    fixture.componentRef.setInput('columns', columns);
    fixture.componentRef.setInput('rows', [
      dataTableFixtureRows(1)[0]!,
      { ...dataTableFixtureRows(2)[1]!, id: 'demo-1"]\\' },
    ]);
    fixture.componentRef.setInput('rowId', (row: DataTableFixtureRow) => row.id);
    fixture.componentRef.setInput('caption', 'Evidence');
    await fixture.whenStable();
  });

  afterEach(() => {
    fixture.destroy();
    templates.destroy();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  async function resize(nextWidth: number): Promise<void> {
    width = nextWidth;
    for (const { callback, observer, targets } of observers) {
      if (targets.has(root)) callback([], observer);
    }
    await fixture.whenStable();
  }

  function control(rowIndex: number, columnId: string, selector: string): HTMLElement {
    const id = fixture.componentInstance.rows()[rowIndex]!.id;
    const row = Array.from(root.querySelectorAll<HTMLElement>('[data-row-id]')).find(
      (candidate) => candidate.dataset['rowId'] === id,
    );
    const cell = Array.from(row?.querySelectorAll<HTMLElement>('app-data-table-cell') ?? []).find(
      (candidate) => candidate.dataset['columnId'] === columnId,
    );
    const target = cell?.querySelector<HTMLElement>(selector);
    expect(target).toBeTruthy();
    return target!;
  }

  function tableMinimum(pixels: () => number): void {
    const scroll = root.querySelector<HTMLElement>('.scroll')!;
    vi.spyOn(scroll, 'clientWidth', 'get').mockImplementation(() => width - 2);
    vi.spyOn(scroll, 'scrollWidth', 'get').mockImplementation(() =>
      fixture.componentInstance.narrow() ? width - 2 : Math.max(width - 2, pixels()),
    );
  }

  it.each(['a', 'button'])(
    'preserves the second row %s through measured-fit stacking and the exact fit boundary',
    async (selector) => {
      tableMinimum(() => 1152);
      fixture.componentRef.setInput('fitToContainer', true);
      control(1, 'identity', selector).focus();
      await resize(900);
      expect(fixture.componentInstance.narrow()).toBe(true);
      expect(document.activeElement).toBe(control(1, 'identity', selector));
      await resize(900);
      expect(fixture.componentInstance.narrow()).toBe(true);
      expect(document.activeElement).toBe(control(1, 'identity', selector));
      await resize(1153);
      expect(fixture.componentInstance.narrow()).toBe(true);
      await resize(1154);
      expect(fixture.componentInstance.narrow()).toBe(false);
      expect(document.activeElement).toBe(control(1, 'identity', selector));
    },
  );

  it('leaves legacy wide-container layout unchanged without the fit opt-in', async () => {
    tableMinimum(() => 1152);
    await resize(900);
    expect(fixture.componentInstance.narrow()).toBe(false);
    expect(root.querySelector('[role="table"]')).not.toBeNull();
  });

  it.each(['columns', 'font', 'font metrics'] as const)(
    'rechecks the measured minimum after its %s changes without losing focus',
    async (source) => {
      let minimum = 1152;
      tableMinimum(() => minimum);
      fixture.componentRef.setInput('fitToContainer', true);
      await resize(900);
      expect(fixture.componentInstance.narrow()).toBe(true);
      control(1, 'identity', 'button').focus();
      minimum = 800;
      if (source === 'columns') {
        fixture.componentRef.setInput(
          'columns',
          columns.map((column) => ({
            ...column,
            width: { min: '8ch' },
          })),
        );
      } else if (source === 'font metrics') {
        root.querySelector<HTMLElement>('.token-probe')!.style.width = '20px';
      } else {
        root.querySelector<HTMLElement>('.token-probe')!.style.font = '12px sans-serif';
      }
      await resize(900);
      expect(fixture.componentInstance.narrow()).toBe(false);
      expect(document.activeElement).toBe(control(1, 'identity', 'button'));
    },
  );

  it('keeps fitted records mounted when a cursor appends an immutable row prefix', async () => {
    tableMinimum(() => 1152);
    fixture.componentRef.setInput('fitToContainer', true);
    await resize(900);
    const first = control(0, 'identity', 'a');
    fixture.componentRef.setInput('rows', [
      ...fixture.componentInstance.rows(),
      dataTableFixtureRows(3)[2]!,
    ]);
    await fixture.whenStable();
    expect(fixture.componentInstance.narrow()).toBe(true);
    expect(control(0, 'identity', 'a')).toBe(first);
    const appended = control(2, 'identity', 'a');
    appended.focus();
    await resize(900);
    expect(document.activeElement).toBe(appended);
  });

  it('keeps fitted records mounted when an existing middle row is replaced during append', async () => {
    let minimum = 1152;
    tableMinimum(() => minimum);
    fixture.componentRef.setInput('fitToContainer', true);
    await resize(900);
    const rows = fixture.componentInstance.rows();
    const first = control(0, 'identity', 'a');
    const layout = vi.spyOn(fixture.componentInstance.narrow, 'set');
    minimum = 800;
    fixture.componentRef.setInput('rows', [
      rows[0]!,
      { ...rows[1]!, note: 'Short replacement' },
      dataTableFixtureRows(3)[2]!,
    ]);
    await resize(900);
    expect(fixture.componentInstance.narrow()).toBe(true);
    expect(layout).not.toHaveBeenCalledWith(false);
    expect(control(0, 'identity', 'a')).toBe(first);
  });

  it.each(['replacement', 'filter'] as const)(
    'does not mount a transient native table on a row-only %s',
    async (source) => {
      tableMinimum(() => 1152);
      fixture.componentRef.setInput('fitToContainer', true);
      await resize(900);
      const surviving = control(1, 'identity', 'button');
      surviving.focus();
      const layout = vi.spyOn(fixture.componentInstance.narrow, 'set');
      const rows = fixture.componentInstance.rows();
      fixture.componentRef.setInput(
        'rows',
        source === 'filter' ? rows.slice(1) : rows.map((row) => ({ ...row, note: 'Replacement' })),
      );
      await resize(900);
      expect(fixture.componentInstance.narrow()).toBe(true);
      expect(layout).not.toHaveBeenCalledWith(false);
      expect(control(source === 'filter' ? 0 : 1, 'identity', 'button')).toBe(surviving);
      expect(document.activeElement).toBe(surviving);
    },
  );

  it.each(['a', 'button'])(
    'preserves the second row %s in both directions, including changed link labels',
    async (selector) => {
      const original = control(1, 'identity', selector);
      original.focus();
      expect(document.activeElement).toBe(original);
      await resize(767);
      expect(fixture.componentInstance.narrow()).toBe(true);
      expect(original.isConnected).toBe(false);
      expect(document.activeElement).toBe(control(1, 'identity', selector));
      await resize(768);
      expect(fixture.componentInstance.narrow()).toBe(false);
      expect(document.activeElement).toBe(control(1, 'identity', selector));
    },
  );

  it('uses the column identity when records reorder primary facts and duplicate action names', async () => {
    control(1, 'other', 'button').focus();
    await resize(480);
    expect(document.activeElement).toBe(control(1, 'other', 'button'));
    expect(document.activeElement).not.toBe(control(1, 'identity', 'button'));
    await resize(1280);
    expect(document.activeElement).toBe(control(1, 'other', 'button'));
  });

  it('keeps a focused record on its bounded second page', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    await fixture.whenStable();
    (root.querySelector('nav button:last-child') as HTMLButtonElement).click();
    await fixture.whenStable();
    control(200, 'identity', 'button').focus();
    await resize(480);
    expect(root.querySelector('nav [role="status"]')?.textContent).toContain('201–201');
    expect(document.activeElement).toBe(control(200, 'identity', 'button'));
    await resize(1280);
    expect(document.activeElement).toBe(control(200, 'identity', 'button'));
  });

  it('does not acquire focus when it was outside the table before resizing', async () => {
    const outside = document.createElement('button');
    document.body.append(outside);
    try {
      outside.focus();
      await resize(480);
      expect(document.activeElement).toBe(outside);
    } finally {
      outside.remove();
    }
  });

  it('does not steal focus moved elsewhere between capture and the new render', async () => {
    const outside = document.createElement('button');
    document.body.append(outside);
    try {
      control(1, 'identity', 'a').focus();
      const pending = resize(480);
      outside.focus();
      await pending;
      expect(document.activeElement).toBe(outside);
    } finally {
      outside.remove();
    }
  });

  it('does not focus a different row when the captured row is removed during reflow', async () => {
    const source = control(1, 'identity', 'a');
    source.focus();
    const pending = resize(480);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(1));
    await pending;
    expect(source.isConnected).toBe(false);
    expect(document.activeElement).toBe(document.body);
  });

  it('returns a later record to its virtual window before restoring its copy control', async () => {
    fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(250));
    await fixture.whenStable();
    await resize(480);
    (root.querySelector('nav button:last-child') as HTMLButtonElement).click();
    await fixture.whenStable();
    const source = control(200, 'hash', 'app-copy-value-button button');
    source.focus();
    // Model the real offset and height so later CDK measurements retain this window in jsdom.
    const scroll = vi
      .spyOn(CdkVirtualScrollViewport.prototype, 'scrollToIndex')
      .mockImplementation(function (this: CdkVirtualScrollViewport, index: number) {
        expect(index).toBe(200);
        const element = this.elementRef.nativeElement;
        vi.spyOn(element, 'clientHeight', 'get').mockReturnValue(352);
        element.scrollTop = index * fixture.componentInstance.itemSize();
        element.dispatchEvent(new Event('scroll'));
        this.checkViewportSize();
      });
    await resize(1280);
    expect(fixture.componentInstance.virtual()).toBe(true);
    expect(scroll).toHaveBeenCalledWith(200);
    expect(source.isConnected).toBe(false);
    await vi.waitFor(
      () => {
        fixture.detectChanges();
        expect(document.activeElement).toBe(control(200, 'hash', 'app-copy-value-button button'));
      },
      { timeout: 3000, interval: 10 },
    );
  });
});
