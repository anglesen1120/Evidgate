// State sheet: docs/design/web/layout.md#open-points--hand-overs
// Dense table contract: docs/design/web/layout.md#dense-table-rules
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { DataTableComponent } from './data-table.component';
import { DATA_TABLE_FIXTURE_COLUMNS, dataTableFixtureRows } from './data-table.fixture';
import type { DataTableFixtureRow } from './data-table.fixture';
import type { Mock } from 'vitest';

/**
 * jsdom performs no layout, so a virtual window exists only through the modelled scroll offset
 * the fixed-size strategy measures. That strategy recomputes the range from the offset on every
 * later pass, so this polls the settled window into the DOM instead of racing a single render.
 */
async function waitForRenderedWindow<T>(
  fixture: ComponentFixture<T>,
  root: HTMLElement,
  expected: { readonly ariaRowIndex: string; readonly rowId: string },
): Promise<void> {
  await vi.waitFor(
    () => {
      fixture.detectChanges();
      const firstRow = root.querySelector('.body-table [data-row-id]');
      expect(firstRow?.getAttribute('aria-rowindex')).toBe(expected.ariaRowIndex);
      expect(firstRow?.getAttribute('data-row-id')).toBe(expected.rowId);
    },
    // Bounded well below vitest's 5 s test timeout so a real mismatch reports its own assertion.
    { timeout: 3000, interval: 10 },
  );
}

/** The `ResizeObserver` surface the lifecycle spec records, as plain spies (no `this` binding). */
interface ObserverSpies {
  observe: Mock;
  unobserve: Mock;
  disconnect: Mock;
}

function recordResizeObservers(): ObserverSpies[] {
  const observers: ObserverSpies[] = [];
  vi.stubGlobal(
    'ResizeObserver',
    vi.fn(function () {
      const observer = { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
      observers.push(observer);
      return observer;
    }),
  );
  return observers;
}

function activeObserverFor(observers: readonly ObserverSpies[], target: Element): ObserverSpies {
  const observer = observers.find(
    (candidate) =>
      candidate.disconnect.mock.calls.length === 0 &&
      candidate.observe.mock.calls.some(([element]) => element === target),
  );
  expect(observer).toBeDefined();
  return observer!;
}

function expectBoundedObservers(observers: readonly ObserverSpies[], root: HTMLElement): void {
  const active = observers.filter((observer) => observer.disconnect.mock.calls.length === 0);
  expect(active.length).toBeLessThanOrEqual(root.querySelectorAll('[role="cell"]').length + 1);
  const targets = active.flatMap((observer) =>
    observer.observe.mock.calls.map(([element]): unknown => element),
  );
  expect(new Set(targets).size).toBe(targets.length);
  expect(targets.every((element) => element instanceof HTMLElement && element.isConnected)).toBe(
    true,
  );
}

describe('DataTableComponent', () => {
  let fixture: ComponentFixture<DataTableComponent<DataTableFixtureRow>>;
  let root: HTMLElement;
  let table: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DataTableComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(DataTableComponent<DataTableFixtureRow>);
    root = fixture.nativeElement as HTMLElement;
    fixture.componentRef.setInput(
      'columns',
      DATA_TABLE_FIXTURE_COLUMNS.map((column) => {
        const textColumn = { ...column };
        delete textColumn.kind;
        return textColumn;
      }),
    );
    fixture.componentRef.setInput('rows', dataTableFixtureRows(2));
    fixture.componentRef.setInput('rowId', (row: DataTableFixtureRow) => row.id);
    fixture.componentRef.setInput('caption', 'Evidence collection / Bộ bằng chứng');
    await fixture.whenStable();
    table = root.querySelector('.table-semantics') as HTMLElement;
  });

  it('R1: supplies manual table, columnheader, cell and absolute row semantics', () => {
    expect(table.getAttribute('role')).toBe('table');
    expect(table.getAttribute('aria-rowcount')).toBe('3');
    expect(table.getAttribute('aria-colcount')).toBe('4');
    expect(root.querySelector('[role="grid"]')).toBeNull();
    expect(root.querySelectorAll('[role="columnheader"][scope="col"]')).toHaveLength(4);
    expect(root.querySelector('[role="row"]')?.getAttribute('aria-rowindex')).toBe('1');
    expect(
      Array.from(root.querySelectorAll('.body-table [data-row-id]')).map((row) =>
        row.getAttribute('aria-rowindex'),
      ),
    ).toEqual(['2', '3']);
    expect(root.querySelectorAll('[role="cell"][aria-colindex]')).toHaveLength(8);
    expect(
      root.querySelector(`#${table.getAttribute('aria-labelledby')}`)?.textContent?.trim(),
    ).toBe('Evidence collection / Bộ bằng chứng');
  });

  it('R1: makes the virtual viewport the body rowgroup, with no nested rowgroup inside it', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    await fixture.whenStable();
    const viewport = root.querySelector('cdk-virtual-scroll-viewport') as HTMLElement;
    expect(viewport.getAttribute('role')).toBe('rowgroup');
    // CDK's own body sections inside the viewport are demoted, so rowgroups never nest.
    expect(viewport.querySelectorAll('[role="rowgroup"]').length).toBe(0);
    expect(viewport.querySelectorAll('[role="row"]').length).toBeGreaterThan(0);
    // Every rowgroup (header thead + viewport) is owned directly by the table semantics.
    const rowgroups = root.querySelectorAll('[role="rowgroup"]');
    expect(rowgroups.length).toBeGreaterThan(1);
    for (const rowgroup of rowgroups) {
      const semanticParent = rowgroup.parentElement?.closest('[role]:not([role="presentation"])');
      expect(semanticParent).toBe(table);
    }
    // A body row's nearest semantic ancestor is the viewport's group.
    const row = viewport.querySelector('[role="row"]');
    expect(row?.parentElement?.closest('[role]:not([role="presentation"])')).toBe(viewport);
  });

  it('R1: only genuinely overflowing scroll containers become named tab stops', async () => {
    const scroll = root.querySelector('.scroll') as HTMLElement;
    expect(scroll.hasAttribute('tabindex')).toBe(false);
    const width = vi.spyOn(scroll, 'clientWidth', 'get').mockReturnValue(200);
    const overflow = vi.spyOn(scroll, 'scrollWidth', 'get').mockReturnValue(600);
    // Change a tracked input to run the same after-render geometry pass as ResizeObserver.
    fixture.componentRef.setInput('rows', dataTableFixtureRows(3));
    await fixture.whenStable();
    expect(scroll.getAttribute('tabindex')).toBe('0');
    expect(scroll.getAttribute('aria-labelledby')).toBe(table.getAttribute('aria-labelledby'));
    expect(scroll.getAttribute('role')).toBe('region');
    expect(scroll.contains(table)).toBe(true);
    expect(table.contains(scroll)).toBe(false);
    expect(table.parentElement).toBe(scroll);
    expect(table.getAttribute('aria-rowcount')).toBe('4');
    overflow.mockReturnValue(200);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(2));
    await fixture.whenStable();
    expect(scroll.hasAttribute('tabindex')).toBe(false);
    width.mockRestore();
    overflow.mockRestore();
  });

  it('R2: keeps the token-layered sticky header inside its own local scroll container', () => {
    const header = root.querySelector('.header-table') as HTMLElement;
    expect(header.parentElement).toBe(table);
    expect(table.parentElement).toBe(root.querySelector('.scroll'));
    expect(header.closest('cdk-virtual-scroll-viewport')).toBeNull();
    expect(header.querySelector('.eg-table-sticky th')).not.toBeNull();
    expect(root.querySelector('.scroll')?.classList.contains('eg-table-long')).toBe(true);
    expect(header.classList.contains('eg-table-layout')).toBe(true);
    expect(header.getAttribute('data-slot')).toBe('table');
  });

  it('R3: numeric cells override base alignment and use tabular numerals with Helm styling', () => {
    const numeric = root.querySelector('.body-table td.cell.numeric') as HTMLElement;
    expect(numeric.getAttribute('aria-colindex')).toBe('3');
    expect(numeric.getAttribute('data-slot')).toBe('table-cell');
    expect(numeric.classList.contains('[&.numeric]:text-right')).toBe(true);
    expect(numeric.classList.contains('[&.numeric]:tabular-nums')).toBe(true);
    expect(numeric.getAttribute('data-column-role')).toBe('number');
  });

  it('R4: delegates opt-in clipping only and keeps narrative values naturally wrapping', () => {
    const values = root.querySelectorAll('.body-table app-data-table-value');
    expect(values[0]?.querySelector('.value')?.classList.contains('truncate')).toBe(true);
    expect(values[1]?.querySelector('.value')?.classList.contains('truncate')).toBe(false);
    expect(values[1]?.querySelector('.value')?.classList.contains('[overflow-wrap:anywhere]')).toBe(
      true,
    );
    expect(root.querySelector('[title]')).toBeNull();
    expect(root.querySelector('.body-table')?.textContent).toContain(
      dataTableFixtureRows(1)[0]?.hash,
    );
    expect(root.querySelector('.body-table')?.textContent).toContain(
      dataTableFixtureRows(1)[0]?.note,
    );
  });

  it('R5: tags only mutating headers and cells for the narrow read-only fallback', () => {
    expect(root.querySelectorAll('th[data-eg-mutate]')).toHaveLength(1);
    expect(root.querySelectorAll('td[data-eg-mutate]')).toHaveLength(2);
    expect(root.querySelectorAll('td:not([data-eg-mutate])')).toHaveLength(6);
    expect(root.querySelector('th[data-eg-mutate]')?.getAttribute('data-column-role')).toBe(
      'actions',
    );
  });

  it('R6: renders all 150 records through the plain CDK table', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(150));
    await fixture.whenStable();
    expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(root.querySelectorAll('.body-table [data-row-id]')).toHaveLength(150);
  });

  it('R6: switches only above the 200-row threshold and respects an explicit threshold', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(200));
    await fixture.whenStable();
    expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    await fixture.whenStable();
    expect(root.querySelector('cdk-virtual-scroll-viewport')).not.toBeNull();
    fixture.componentRef.setInput('virtualThreshold', 201);
    await fixture.whenStable();
    expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(root.querySelectorAll('.body-table [data-row-id]')).toHaveLength(200);
    expect(root.querySelector('.eg-table-pagination')).not.toBeNull();
  });

  it('R6: bounds a 5000-row window and retains total count and absolute indices after windowing', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(5000));
    await fixture.whenStable();
    const viewport = fixture.componentInstance.viewport()!;
    const element = viewport.elementRef.nativeElement;
    vi.spyOn(element, 'clientHeight', 'get').mockReturnValue(352);
    viewport.checkViewportSize();
    await fixture.whenStable();
    expect(viewport.getViewportSize()).toBe(352);
    const rendered = root.querySelectorAll('.body-table [data-row-id]');
    expect(rendered.length).toBeGreaterThan(0);
    expect(rendered.length).toBeLessThan(100);
    expect(table.getAttribute('aria-rowcount')).toBe('5001');
    // jsdom performs no layout, so model the scroll offset the fixed-size strategy measures:
    // it keeps minBufferPx (itemSize * 4 in the template) above the first visible row, so
    // scrolling row 404 into view settles the rendered window on rows 400-419. Driving the
    // offset instead of the range keeps that window a fixed point of every later recompute.
    element.scrollTop = 404 * fixture.componentInstance.itemSize();
    element.dispatchEvent(new Event('scroll'));
    viewport.checkViewportSize();
    await waitForRenderedWindow(fixture, root, { ariaRowIndex: '402', rowId: 'demo-400' });
    expect(root.querySelector('.body-table [data-row-id]')?.getAttribute('aria-rowindex')).toBe(
      '402',
    );
    expect(root.querySelector('.body-table [data-row-id]')?.getAttribute('data-row-id')).toBe(
      'demo-400',
    );
  });

  it('R6: bounds observers and reuses them after the virtual viewport is sized', async () => {
    const observers = recordResizeObservers();
    try {
      const columns = DATA_TABLE_FIXTURE_COLUMNS.map((column) => ({ ...column, truncate: false }));
      fixture.componentRef.setInput('columns', columns);
      fixture.componentRef.setInput('rows', dataTableFixtureRows(5000));
      await fixture.whenStable();
      const scroll = root.querySelector('.scroll') as HTMLElement;
      const tableObserver = activeObserverFor(observers, scroll);
      expect(tableObserver.observe).toHaveBeenCalledWith(
        root.querySelector('cdk-virtual-scroll-viewport'),
      );
      vi.spyOn(scroll, 'clientWidth', 'get').mockReturnValue(200);
      const width = vi.spyOn(scroll, 'scrollWidth', 'get').mockReturnValue(600);
      const viewport = fixture.componentInstance.viewport()!;
      vi.spyOn(viewport.elementRef.nativeElement, 'clientHeight', 'get').mockReturnValue(352);
      const viewportMeasure = vi.spyOn(viewport, 'checkViewportSize');
      // Sizing can legitimately add visible cells and their clipping observers.
      // Pin the count only after that first measured window has settled.
      viewport.checkViewportSize();
      await fixture.whenStable();
      const creations = observers.length;
      expect(creations).toBeGreaterThan(0);
      expectBoundedObservers(observers, root);

      for (let refresh = 0; refresh < 3; refresh++) {
        fixture.componentRef.setInput('rows', dataTableFixtureRows(5000));
        await fixture.whenStable();
        expect(observers).toHaveLength(creations);
        expect(tableObserver.disconnect).not.toHaveBeenCalled();
        expectBoundedObservers(observers, root);
        expect(scroll.getAttribute('tabindex')).toBe('0');
        expect(viewport.getViewportSize()).toBe(352);
      }
      expect(viewportMeasure).toHaveBeenCalled();
      width.mockReturnValue(200);
      fixture.componentRef.setInput('columns', [...columns]);
      await fixture.whenStable();
      expect(observers).toHaveLength(creations);
      expect(scroll.hasAttribute('tabindex')).toBe(false);
      expect(root.querySelectorAll('.body-table [data-row-id]').length).toBeLessThan(100);

      fixture.componentRef.setInput('rows', dataTableFixtureRows(2));
      await fixture.whenStable();
      expect(tableObserver.disconnect).toHaveBeenCalledTimes(1);
      expect(observers.length).toBeGreaterThan(creations);
      const replacementObserver = activeObserverFor(observers, scroll);
      expect(replacementObserver).not.toBe(tableObserver);
      expectBoundedObservers(observers, root);
      fixture.destroy();
      expect(tableObserver.disconnect).toHaveBeenCalledTimes(1);
      expect(replacementObserver.disconnect).toHaveBeenCalledTimes(1);
      expect(observers.every((observer) => observer.disconnect.mock.calls.length === 1)).toBe(true);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('R6: resolves fixed-size density from the CSS token probe with documented jsdom fallbacks', async () => {
    fixture.componentRef.setInput('rowHeight', 'compact');
    await fixture.whenStable();
    expect(root.classList.contains('compact')).toBe(true);
    expect(fixture.componentInstance.itemSize()).toBe(40);
    fixture.componentRef.setInput('rowHeight', 'default');
    await fixture.whenStable();
    expect(fixture.componentInstance.itemSize()).toBe(44);
    // Density is a host-utility contract; jsdom resolves no cascade, so the declared token
    // utilities are the observable evidence that compact and coarse-pointer rows survive.
    for (const utility of [
      '[--eg-table-row:var(--eg-row-default)]',
      '[--eg-table-inset:var(--eg-space-3)]',
      '[&.compact]:[--eg-table-row:var(--eg-row-compact)]',
      '[&.compact]:[--eg-table-inset:var(--eg-space-2)]',
      'pointer-coarse:[&.compact]:[--eg-table-row:var(--eg-row-default)]',
    ]) {
      expect(root.classList.contains(utility)).toBe(true);
    }
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    await fixture.whenStable();
    const cell = root.querySelector('.body-table .cell');
    expect(cell?.classList.contains('py-0')).toBe(true);
    expect(cell?.classList.contains('border-b-0')).toBe(true);
    expect(
      root.querySelector('app-data-table-value')?.classList.contains('eg-table-fixed-value'),
    ).toBe(true);
    expect(root.querySelector('app-data-table-value')?.classList.contains('overflow-hidden')).toBe(
      false,
    );
  });

  it('R6: retains focus in overlapping windows and restores it before the focused row is recycled', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(5000));
    await fixture.whenStable();
    const viewport = fixture.componentInstance.viewport()!;
    const element = viewport.elementRef.nativeElement;
    // Model the focusability produced by real overflow/clipping measurements.
    // Real geometry and real scroll-driven window changes require browser QA.
    element.tabIndex = 0;
    const trigger = root.querySelector('.body-table .value') as HTMLElement;
    trigger.tabIndex = 0;
    trigger.focus();
    viewport.setRenderedRange({ start: 0, end: 20 });
    await fixture.whenStable();
    expect(document.activeElement).toBe(trigger);
    viewport.setRenderedRange({ start: 400, end: 420 });
    await fixture.whenStable();
    expect(document.activeElement).toBe(element);
  });

  it('R7: renders a localized empty spanning row, caption and header-only rowcount', async () => {
    fixture.componentRef.setInput('rows', []);
    await fixture.whenStable();
    expect(root.querySelector('.caption')?.textContent).toContain('Evidence collection');
    expect(root.querySelector('.empty-table td')?.textContent?.trim()).toBe('No rows');
    expect(root.querySelector('.empty-table td')?.getAttribute('colspan')).toBe('4');
    expect(table.getAttribute('aria-rowcount')).toBe('1');
    expect(root.querySelector('.body-table [data-row-id]')).toBeNull();
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b/);
  });

  it('R8: composes Helm table styling and token utilities without replacing CDK semantics', () => {
    expect(root.querySelector('[hlmTableContainer]')?.getAttribute('data-slot')).toBe(
      'table-container',
    );
    expect(root.querySelector('th')?.getAttribute('data-slot')).toBe('table-head');
    expect(root.querySelector('.body-table tr')?.getAttribute('data-slot')).toBe('table-row');
    expect(
      root.querySelector('.body-table')?.classList.contains('border-spacing-[var(--eg-space-0)]'),
    ).toBe(true);
    expect(root.querySelector('.body-table td')?.classList.contains('px-[var(--eg-space-4)]')).toBe(
      true,
    );
    expect(root.classList.contains('bg-card')).toBe(true);
  });

  it.each([
    'default',
    'hover',
    'focus-visible',
    'active',
    'disabled',
    'loading',
    'empty',
    'error',
    'success',
  ])(
    'state %s: remains a render-only table, not an action, request or derived verdict',
    async (state) => {
      if (state === 'empty') {
        fixture.componentRef.setInput('rows', []);
        await fixture.whenStable();
        expect(root.querySelector('.empty-table')).not.toBeNull();
      }
      const row = root.querySelector('.body-table tr');
      if (state === 'hover') row?.dispatchEvent(new MouseEvent('mouseenter'));
      if (state === 'active') row?.dispatchEvent(new MouseEvent('click'));
      expect(
        root.querySelector('button,[role="button"],[aria-disabled],input,a,[aria-busy]'),
      ).toBeNull();
      expect(row?.hasAttribute('tabindex') ?? false).toBe(false);
      expect(root.querySelector('[tabindex]')).toBeNull();
      expect(root.querySelector('[aria-selected]')).toBeNull();
      expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b/);
    },
  );
});
