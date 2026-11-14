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
