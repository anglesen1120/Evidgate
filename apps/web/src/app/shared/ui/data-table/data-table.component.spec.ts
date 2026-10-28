// State sheet: docs/design/web/layout.md#open-points--hand-overs
// Dense table contract: docs/design/web/layout.md#dense-table-rules
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { DataTableComponent } from './data-table.component';
import { DATA_TABLE_FIXTURE_COLUMNS, dataTableFixtureRows } from './data-table.fixture';
import type { DataTableFixtureRow } from './data-table.fixture';
import type { Mock } from 'vitest';

function tableStyles(): string {
  // Read Angular's compiled, encapsulated CSS rather than duplicating the source.
  return (
    Array.from(document.querySelectorAll('style'))
      .map((style) => style.textContent ?? '')
      .find((css) => css.includes('border-spacing:') && css.includes('.header-table')) ?? ''
  );
}

function specificity(selector: string): number {
  // This regression compares class/attribute-only component selectors, not a
  // general-purpose specificity parser (no :is(), ids or pseudo-elements).
  return (selector.match(/\.[\w-]+|\[[^\]]+\]/g) ?? []).length;
}

/**
 * F56: `CdkVirtualScrollViewport.setRenderedRange` publishes the new range synchronously but
 * schedules the re-render from a microtask, so a single `whenStable()` can resolve before the
 * bounded window reaches the DOM under CPU load. Poll with a forced change detection, bounded:
 * the first pass renders the requested window, and that render is what this observes, before
 * the fixed-size strategy's post-render recompute re-expands the range from the jsdom scroll
 * offset. The caller's strict assertions then judge that rendered window.
 */
async function waitForRenderedWindow<T>(
  fixture: ComponentFixture<T>,
  root: HTMLElement,
  expected: { readonly ariaRowIndex: string; readonly rowId: string },
): Promise<void> {
  await vi.waitFor(
    () => {
      fixture.detectChanges();
      const firstRow = root.querySelector('.body-table [role="row"]');
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
    fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
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
      Array.from(root.querySelectorAll('.body-table [role="row"]')).map((row) =>
        row.getAttribute('aria-rowindex'),
      ),
    ).toEqual(['2', '3']);
    expect(root.querySelectorAll('[role="cell"][aria-colindex]')).toHaveLength(8);
    expect(root.querySelector(`#${table.getAttribute('aria-labelledby')}`)?.textContent).toBe(
      'Evidence collection / Bộ bằng chứng',
    );
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
    expect(getComputedStyle(header).position).toBe('sticky');
    expect(getComputedStyle(header).top).toBe('0px');
    expect(tableStyles()).toContain('z-index: var(--eg-z-sticky)');
  });

  it('R3: numeric cells beat the base alignment specificity and use tabular numerals', () => {
    const numeric = root.querySelector('.body-table .cell.numeric') as HTMLElement;
    expect(numeric.getAttribute('aria-colindex')).toBe('3');
    expect(getComputedStyle(numeric).textAlign).toBe('right');
    expect(getComputedStyle(numeric).fontVariantNumeric).toBe('tabular-nums');
    const rules = Array.from(tableStyles().matchAll(/([^{}]+)\{([^{}]+)\}/g));
    const base = rules.find(
      (rule) =>
        rule[1]?.includes('.table') &&
        /\.cell(?:\[|\s|$)/.test(rule[1]) &&
        rule[2]?.includes('text-align: start'),
    );
    const end = rules.find(
      (rule) => rule[1]?.includes('.cell.numeric') && rule[2]?.includes('text-align: right'),
    );
    expect(base).toBeDefined();
    expect(end).toBeDefined();
    expect(specificity(end?.[1] ?? '')).toBeGreaterThanOrEqual(specificity(base?.[1] ?? ''));
  });

  it('R4: delegates opt-in clipping only and keeps narrative values naturally wrapping', () => {
    const values = root.querySelectorAll('.body-table app-data-table-value');
    expect(values[0]?.querySelector('.value')?.classList.contains('truncate')).toBe(true);
    expect(values[1]?.querySelector('.value')?.classList.contains('truncate')).toBe(false);
    expect(getComputedStyle(values[1]?.querySelector('.value') as HTMLElement).overflowWrap).toBe(
      'anywhere',
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
    expect(tableStyles()).toMatch(/@media\s*\(max-width:\s*767px\)/);
    expect(tableStyles()).toMatch(/\[data-eg-mutate\][^{]*\{\s*display:\s*none/);
  });

  it('R6: renders all 150 records through the plain CDK table', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(150));
    await fixture.whenStable();
    expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(root.querySelectorAll('.body-table [role="row"]')).toHaveLength(150);
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
    expect(root.querySelectorAll('.body-table [role="row"]')).toHaveLength(201);
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
    const rendered = root.querySelectorAll('.body-table [role="row"]');
    expect(rendered.length).toBeGreaterThan(0);
    expect(rendered.length).toBeLessThan(100);
    expect(table.getAttribute('aria-rowcount')).toBe('5001');
    // Set a public CDK rendered range; jsdom cannot prove physical scrolling.
    viewport.setRenderedRange({ start: 400, end: 420 });
    await waitForRenderedWindow(fixture, root, { ariaRowIndex: '402', rowId: 'demo-400' });
    expect(root.querySelector('.body-table [role="row"]')?.getAttribute('aria-rowindex')).toBe(
      '402',
    );
    expect(root.querySelector('.body-table [role="row"]')?.getAttribute('data-row-id')).toBe(
      'demo-400',
    );
  });

  it('R6: remeasures refreshed rows and columns without recreating the resize observer', async () => {
    const observers: ObserverSpies[] = [];
    const ResizeObserverMock = vi.fn(function () {
      const observer: ObserverSpies = { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
      observers.push(observer);
      return observer;
    });
    vi.stubGlobal('ResizeObserver', ResizeObserverMock);
    try {
      const columns = DATA_TABLE_FIXTURE_COLUMNS.map((column) => ({ ...column, truncate: false }));
      fixture.componentRef.setInput('columns', columns);
      fixture.componentRef.setInput('rows', dataTableFixtureRows(5000));
      await fixture.whenStable();
      const creations = ResizeObserverMock.mock.calls.length;
      expect(creations).toBeGreaterThan(0);
      const scroll = root.querySelector('.scroll') as HTMLElement;
      const tableObserver = observers.find((observer) =>
        observer.observe.mock.calls.some(([element]) => element === scroll),
      )!;
      expect(tableObserver).toBeDefined();
      expect(tableObserver.observe).toHaveBeenCalledWith(
        root.querySelector('cdk-virtual-scroll-viewport'),
      );
      vi.spyOn(scroll, 'clientWidth', 'get').mockReturnValue(200);
      const width = vi.spyOn(scroll, 'scrollWidth', 'get').mockReturnValue(600);
      const viewport = fixture.componentInstance.viewport()!;
      vi.spyOn(viewport.elementRef.nativeElement, 'clientHeight', 'get').mockReturnValue(352);
      const viewportMeasure = vi.spyOn(viewport, 'checkViewportSize');

      for (let refresh = 0; refresh < 3; refresh++) {
        fixture.componentRef.setInput('rows', dataTableFixtureRows(5000));
        await fixture.whenStable();
        expect(ResizeObserverMock).toHaveBeenCalledTimes(creations);
        expect(tableObserver.disconnect).not.toHaveBeenCalled();
        expect(scroll.getAttribute('tabindex')).toBe('0');
        expect(viewport.getViewportSize()).toBe(352);
      }
      expect(viewportMeasure).toHaveBeenCalled();
      width.mockReturnValue(200);
      fixture.componentRef.setInput('columns', [...columns]);
      await fixture.whenStable();
      expect(ResizeObserverMock).toHaveBeenCalledTimes(creations);
      expect(scroll.hasAttribute('tabindex')).toBe(false);
      expect(root.querySelectorAll('.body-table [role="row"]').length).toBeLessThan(100);

      fixture.componentRef.setInput('rows', dataTableFixtureRows(2));
      await fixture.whenStable();
      expect(tableObserver.disconnect).toHaveBeenCalledTimes(1);
      expect(ResizeObserverMock.mock.calls.length).toBeGreaterThan(creations);
      const replacementObserver = observers.at(-1)!;
      expect(replacementObserver.observe).toHaveBeenCalledWith(scroll);
      fixture.destroy();
      expect(tableObserver.disconnect).toHaveBeenCalledTimes(1);
      expect(replacementObserver.disconnect).toHaveBeenCalledTimes(1);
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
    expect(tableStyles()).toContain('height: var(--eg-row-compact)');
    expect(tableStyles()).toContain('height: var(--eg-row-default)');
    expect(tableStyles()).toContain('max-height: var(--eg-row-compact)');
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
    expect(root.querySelector('.body-table [role="row"]')).toBeNull();
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b/);
  });

  it('R8: styles the typed demo with existing tokens including the deliberate zero cell gap', () => {
    const css = tableStyles();
    expect(css).toContain('border-spacing: var(--eg-space-0)');
    expect(css).toContain('padding: var(--eg-space-3) var(--eg-space-4)');
    expect(css).toContain('var(--eg-surface)');
    // The component's own template carries no style attributes; `CdkTable` writes inline
    // sticky positioning on its own sticky row groups, which are the only allowed ones.
    expect(root.querySelector('[style]:not(thead, tbody, tfoot)')).toBeNull();
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
