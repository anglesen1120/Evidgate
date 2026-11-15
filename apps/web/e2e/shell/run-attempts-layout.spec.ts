import { expect, test, type Page } from '@playwright/test';
import attemptsFixture from '../../../../fixtures/web-api/run-attempts/input.json';
import { runDetailPath } from './run-detail.support';
import { seedStoredTheme, type ShellLocale } from './support';

// The attempts table stacks up to and including 767 px and keeps its columns from 768 px. Both
// neighbouring widths are covered because the strict max-width utility stops at 766 px, which
// used to leave a 767 px viewport with a visible header row above stacked records.
const WIDTHS = [390, 766, 767, 768] as const;
const STACKED_MAX = 767;

/** A measured rectangle, in CSS pixels, relative to the viewport. */
interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Everything the browser answers about one attempts record at the current viewport width. */
interface AttemptLayout {
  tableDisplay: string;
  headPosition: string;
  head: Box;
  bodyDisplay: string;
  rowDisplay: string;
  fieldDisplays: string[];
  labelDisplays: string[];
  fieldBoxes: Box[];
  recordInnerWidth: number;
  fieldContentOverflow: number[];
  fieldMinimums: string[];
  headerWordsFit: boolean;
  /** Horizontal overflow of the scroll container, 0 when the layout fits its column. */
  containerOverflow: number;
  /** Distance from the table's top to the first record: the header is out of flow when stacked. */
  firstRecordOffset: number;
}

async function attemptLayout(page: Page): Promise<AttemptLayout> {
  return page.locator('app-attempts-table').evaluate((host): AttemptLayout => {
    const table = host.querySelector('table');
    const head = host.querySelector('thead');
    const body = host.querySelector('tbody');
    const record = host.querySelector('[data-attempt-row]');
    const container = host.querySelector('.table-scroll');
    if (
      !(table instanceof HTMLElement) ||
      !(head instanceof HTMLElement) ||
      !(body instanceof HTMLElement) ||
      !(record instanceof HTMLElement) ||
      !(container instanceof HTMLElement)
    ) {
      throw new Error(
        'Attempts table, header, body, first record and scroll container are required',
      );
    }
    const box = (target: Element): Box => {
      const rect = target.getBoundingClientRect();
      return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
    };
    const fields = [...record.querySelectorAll<HTMLElement>('[role="rowheader"], [role="cell"]')];
    const label = (field: HTMLElement): HTMLElement => {
      const found = field.querySelector('[aria-hidden="true"]');
      if (!(found instanceof HTMLElement)) {
        throw new Error('Every stacked field carries its own label');
      }
      return found;
    };
    const recordStyle = getComputedStyle(record);
    const wordFits = (element: Element): boolean => {
      const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        for (const match of (node.textContent ?? '').matchAll(/\S+/gu)) {
          const range = document.createRange();
          range.setStart(node, match.index);
          range.setEnd(node, match.index + match[0].length);
          if (range.getClientRects().length > 1) return false;
        }
      }
      return true;
    };
    return {
      tableDisplay: getComputedStyle(table).display,
      headPosition: getComputedStyle(head).position,
      head: box(head),
      bodyDisplay: getComputedStyle(body).display,
      rowDisplay: getComputedStyle(record).display,
      fieldDisplays: fields.map((field) => getComputedStyle(field).display),
      labelDisplays: fields.map((field) => getComputedStyle(label(field)).display),
      fieldBoxes: fields.map(box),
      recordInnerWidth:
        record.clientWidth -
        Number.parseFloat(recordStyle.paddingLeft) -
        Number.parseFloat(recordStyle.paddingRight),
      fieldContentOverflow: fields.map((field) => field.scrollHeight - field.clientHeight),
      fieldMinimums: fields.map((field) => getComputedStyle(field).minInlineSize),
      headerWordsFit: [...head.querySelectorAll('th')].every(wordFits),
      containerOverflow: container.scrollWidth - container.clientWidth,
      firstRecordOffset: box(record).y - box(table).y,
    };
  });
}

async function openAttempts(page: Page, locale: ShellLocale = 'en'): Promise<void> {
  await page.goto(runDetailPath({ locale, attempts: 'ok', revisions: 'ok' }));
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
  await expect(page.locator('app-attempts-table [data-attempt-row]')).toHaveCount(
    attemptsFixture.body.items.length,
  );
}

for (const { width, locale } of WIDTHS.flatMap((width) =>
  (['en', 'vi'] as const).map((locale) => ({ width, locale })),
)) {
  test(`attempts table layout at ${width}px (${locale})`, async ({ page }) => {
    await seedStoredTheme(page, 'light');
    await page.setViewportSize({ width, height: 900 });
    await openAttempts(page, locale);
    const layout = await attemptLayout(page);
    expect(layout.fieldDisplays).toHaveLength(5);
    expect(layout.labelDisplays).toHaveLength(5);
    expect(layout.containerOverflow, JSON.stringify(layout)).toBeLessThanOrEqual(1);
    const distinct = (values: number[]): number => new Set(values.map(Math.round)).size;
    if (width <= STACKED_MAX) {
      // One layout per width: the record header leaves the flow, every field stacks and the
      // header row's own labels are on screen, so a visible header above the stack is a failure.
      expect.soft(layout.tableDisplay).toBe('block');
      expect.soft(layout.headPosition).toBe('absolute');
      expect.soft(layout.head.width, JSON.stringify(layout)).toBeLessThanOrEqual(1);
      expect.soft(layout.head.height, JSON.stringify(layout)).toBeLessThanOrEqual(1);
      expect.soft(layout.bodyDisplay).toBe('block');
      expect.soft(layout.rowDisplay).toBe('block');
      expect.soft(layout.fieldDisplays).toEqual(Array(5).fill('block'));
      expect.soft(layout.labelDisplays).toEqual(Array(5).fill('block'));
      expect.soft(layout.firstRecordOffset, JSON.stringify(layout)).toBeLessThanOrEqual(2);
      expect.soft(distinct(layout.fieldBoxes.map((field) => field.x))).toBe(1);
      expect.soft(distinct(layout.fieldBoxes.map((field) => field.y))).toBe(5);
      expect.soft(layout.fieldMinimums).toEqual(Array(5).fill('0px'));
      for (const field of layout.fieldBoxes) {
        expect
          .soft(Math.abs(field.width - layout.recordInnerWidth), JSON.stringify(layout))
          .toBeLessThanOrEqual(1);
      }
      for (const overflow of layout.fieldContentOverflow) {
        expect.soft(overflow, JSON.stringify(layout)).toBeLessThanOrEqual(1);
      }
      for (let index = 1; index < layout.fieldBoxes.length; index++) {
        const previous = layout.fieldBoxes[index - 1]!;
        expect
          .soft(previous.y + previous.height)
          .toBeLessThanOrEqual(layout.fieldBoxes[index]!.y + 1);
      }
    } else {
      expect.soft(layout.tableDisplay).toBe('table');
      expect.soft(layout.headPosition).toBe('static');
      expect.soft(layout.head.height, JSON.stringify(layout)).toBeGreaterThan(20);
      expect.soft(layout.bodyDisplay).toBe('table-row-group');
      expect.soft(layout.rowDisplay).toBe('table-row');
      expect.soft(layout.fieldDisplays).toEqual(Array(5).fill('table-cell'));
      expect.soft(layout.labelDisplays).toEqual(Array(5).fill('none'));
      expect.soft(layout.firstRecordOffset, JSON.stringify(layout)).toBeGreaterThan(2);
      expect.soft(distinct(layout.fieldBoxes.map((field) => field.y))).toBe(1);
      expect.soft(distinct(layout.fieldBoxes.map((field) => field.x))).toBe(5);
      expect.soft(layout.headerWordsFit, JSON.stringify(layout)).toBe(true);
    }
  });
}

test('attempts layout flips between 767px and 768px on resize', async ({ page }) => {
  await seedStoredTheme(page, 'light');
  await page.setViewportSize({ width: STACKED_MAX, height: 900 });
  await openAttempts(page);
  await expect.poll(async () => (await attemptLayout(page)).rowDisplay).toBe('block');
  expect((await attemptLayout(page)).labelDisplays).toEqual(Array(5).fill('block'));
  await page.setViewportSize({ width: STACKED_MAX + 1, height: 900 });
  await expect.poll(async () => (await attemptLayout(page)).rowDisplay).toBe('table-row');
  await expect
    .poll(async () => (await attemptLayout(page)).labelDisplays)
    .toEqual(Array(5).fill('none'));
  await page.setViewportSize({ width: STACKED_MAX, height: 900 });
  await expect.poll(async () => (await attemptLayout(page)).rowDisplay).toBe('block');
});
