// State sheet: docs/design/web/state-sheet.md#how-the-specs-link-here
import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import axe from 'axe-core';

import exceptionsDocument from '../../../../../e2e/a11y-exceptions.json';
import {
  AXE_JSDOM_RUN_OPTIONS,
  formatViolations,
  unexceptedViolations,
  type A11yException,
} from '../../../../../e2e/a11y/axe-config';
import { DataTableComponent } from '../data-table';
import {
  DATA_TABLE_FIXTURE_COLUMNS,
  dataTableFixtureRows,
  type DataTableFixtureRow,
} from '../data-table/data-table.fixture';
import { UiGalleryComponent } from './ui-gallery.component';

registerLocaleData(localeVi);

const axeOptions: axe.RunOptions = AXE_JSDOM_RUN_OPTIONS;

/** The accepted violations the whole a11y gate shares; this spec honours them too. */
const A11Y_EXCEPTIONS: readonly A11yException[] = exceptionsDocument.exceptions;

/** What a scan of the gallery must leave empty: one line per violation no entry covers. */
function unexceptedLines(result: axe.AxeResults): string[] {
  return formatViolations(unexceptedViolations(result.violations, A11Y_EXCEPTIONS));
}

const cases = [
  { locale: 'en', theme: 'light' },
  { locale: 'en', theme: 'dark' },
  { locale: 'vi', theme: 'light' },
  { locale: 'vi', theme: 'dark' },
] as const;

describe('UiGalleryComponent axe acceptance', () => {
  let previousTheme: string | null;
  let previousLang: string | null;

  beforeEach(() => {
    previousTheme = document.documentElement.getAttribute('data-theme');
    previousLang = document.documentElement.getAttribute('lang');
  });

  afterEach(() => {
    if (previousTheme === null) document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', previousTheme);
    if (previousLang === null) document.documentElement.removeAttribute('lang');
    else document.documentElement.setAttribute('lang', previousLang);
  });

  it.each(cases)(
    'has zero WCAG violations: $locale / $theme',
    async ({ locale, theme }) => {
      if (theme === 'dark') document.documentElement.setAttribute('data-theme', 'dark');
      else document.documentElement.removeAttribute('data-theme');
      document.documentElement.setAttribute('lang', locale);
      await TestBed.configureTestingModule({
        imports: [UiGalleryComponent],
        providers: [
          provideZonelessChangeDetection(),
          ...(locale === 'vi' ? [{ provide: LOCALE_ID, useValue: 'vi' }] : []),
        ],
      }).compileComponents();
      expect(TestBed.inject(LOCALE_ID)).toBe(locale);
      const fixture = TestBed.createComponent(UiGalleryComponent);
      const root = fixture.nativeElement as HTMLElement;
      document.body.append(root);
      try {
        await fixture.whenStable();
        expect(document.body.contains(root)).toBe(true);
        // Three fixture specimens plus the column-role specimen in the tables group.
        expect(root.querySelectorAll('app-data-table')).toHaveLength(4);
        expect(root.querySelector('app-waiver-banner time')).not.toBeNull();
        for (const selector of [
          'app-loading-pattern',
          'app-empty-pattern',
          'app-error-pattern',
          'app-forbidden-pattern',
          'app-not-found-pattern',
          'app-retry-after[data-demo="retry-after-waiting"]',
          'app-retry-after[data-demo="retry-after-eligible"]',
          'app-request-state',
          'app-conflict-dialog',
        ]) {
          expect(root.querySelector(selector), selector).not.toBeNull();
        }
        const result = await axe.run(root, axeOptions);
        expect(unexceptedLines(result)).toEqual([]);
        root.querySelector<HTMLButtonElement>('[data-demo="reveal-opener"]')?.click();
        await fixture.whenStable();
        const revealResult = await axe.run(root, axeOptions);
        expect(unexceptedLines(revealResult)).toEqual([]);
        root.querySelector<HTMLButtonElement>('[data-demo="conflict-opener"]')?.click();
        await fixture.whenStable();
        const dialog = document.querySelector<HTMLElement>('[role="dialog"]');
        if (!dialog) throw new Error('Expected Brain dialog overlay');
        expect(dialog.getAttribute('aria-modal')).toBe('true');
        expect(dialog.textContent).toContain('Not compared');
        const dialogResult = await axe.run(dialog, axeOptions);
        expect(unexceptedLines(dialogResult)).toEqual([]);
      } finally {
        fixture.destroy();
        root.remove();
      }
    },
    30_000,
  );

  it.each([12, 250])(
    'keeps %i-row table semantics valid inside a named horizontal overflow region',
    async (count) => {
      await TestBed.configureTestingModule({
        imports: [DataTableComponent],
        providers: [provideZonelessChangeDetection()],
      }).compileComponents();
      const fixture = TestBed.createComponent(DataTableComponent<DataTableFixtureRow>);
      fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
      fixture.componentRef.setInput('rows', dataTableFixtureRows(count));
      fixture.componentRef.setInput('rowId', (row: DataTableFixtureRow) => row.id);
      fixture.componentRef.setInput('caption', 'Horizontal overflow boundary');
      const root = fixture.nativeElement as HTMLElement;
      document.body.append(root);
      try {
        await fixture.whenStable();
        const scroll = root.querySelector('.scroll') as HTMLElement;
        vi.spyOn(scroll, 'clientWidth', 'get').mockReturnValue(200);
        vi.spyOn(scroll, 'scrollWidth', 'get').mockReturnValue(600);
        // Refresh the real input so afterRenderEffect measures the overflow branch.
        fixture.componentRef.setInput('rows', dataTableFixtureRows(count));
        await fixture.whenStable();
        expect(scroll.getAttribute('role')).toBe('region');
        expect(scroll.getAttribute('tabindex')).toBe('0');
        expect(scroll.getAttribute('aria-labelledby')).toBe(
          root.querySelector('[role="table"]')?.getAttribute('aria-labelledby'),
        );
        const result = await axe.run(root, axeOptions);
        expect(
          result.violations.map(({ id, nodes }) => ({
            id,
            targets: nodes.map((node) => node.target),
            summaries: nodes.map((node) => node.failureSummary),
          })),
        ).toEqual([]);
      } finally {
        vi.restoreAllMocks();
        fixture.destroy();
        root.remove();
      }
    },
    30_000,
  );

  it('negative control detects a button without an accessible name', async () => {
    const control = document.createElement('div');
    control.append(document.createElement('button'));
    document.body.append(control);
    try {
      const result = await axe.run(control, axeOptions);
      expect(result.violations.some((violation) => violation.id === 'button-name')).toBe(true);
      expect(
        result.violations.find((violation) => violation.id === 'button-name')?.nodes,
      ).toHaveLength(1);
    } finally {
      control.remove();
    }
  });
});
