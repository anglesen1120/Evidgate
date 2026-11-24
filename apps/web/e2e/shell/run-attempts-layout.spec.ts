import { expect, test, type Page } from '@playwright/test';
import attemptsFixture from '../../../../fixtures/web-api/run-attempts/input.json';
import { runDetailPath } from './run-detail.support';
import { seedStoredTheme, type ShellLocale } from './support';

// The shared data table stacks from its own container width, not the viewport: it switches to
// labelled records below 768 px of container width, or when its columns do not fit. The mode is
// therefore read from the rendered DOM (see renderedStacked). Both neighbouring widths are still
// covered because the strict max-width utility stopped at 766 px in the past.
const WIDTHS = [390, 766, 767, 768] as const;
const RECORD = '.eg-table-record[data-row-id]';
const DESKTOP_ROW = 'tr[data-row-id]';

/** A measured rectangle, in CSS pixels, relative to the viewport. */
interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Everything the browser answers about one attempts record at the current viewport width. */
interface AttemptLayout {
  head: Box | null;
  labelDisplays: string[];
  fieldBoxes: Box[];
  recordInnerWidth: number;
  fieldContentOverflow: number[];
  fieldMinimums: string[];
  headerWordsFit: boolean;
  /** Horizontal overflow of the scroll container, 0 when the layout fits its column. */
  containerOverflow: number;
  /** Distance from the top of the table or record list to its first data row. */
  firstRecordOffset: number;
}

async function attemptLayout(page: Page, stacked: boolean): Promise<AttemptLayout> {
  const host = page.locator('app-attempts-table');
  const first = host.locator(stacked ? RECORD : DESKTOP_ROW).first();
  await expect(first).toBeVisible();
  const identity = first.locator('[data-attempt-row]');
  const rowId = await first.getAttribute('data-row-id');
  expect(rowId).toMatch(/^attempt-/);
  await expect(identity).toHaveAttribute('id', rowId!);
  await expect(identity).toHaveAttribute('tabindex', '-1');
  return first.evaluate((record, stacked): AttemptLayout => {
    const container = record.closest('.scroll')!;
    const tableOrList = record.closest(stacked ? 'ol' : 'table')!;
    const head = container.querySelector('thead');
    const box = (target: Element): Box => {
      const rect = target.getBoundingClientRect();
      return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
    };
    const fields = [
      ...record.querySelectorAll<HTMLElement>(stacked ? ':scope > dl > div' : ':scope > td'),
    ];
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
      head: head && box(head),
      labelDisplays: [...record.querySelectorAll('dt')].map(
        (label) => getComputedStyle(label).display,
      ),
      fieldBoxes: fields.map(box),
      recordInnerWidth:
        record.clientWidth -
        Number.parseFloat(recordStyle.paddingLeft) -
        Number.parseFloat(recordStyle.paddingRight),
      fieldContentOverflow: fields.map((field) => field.scrollHeight - field.clientHeight),
      fieldMinimums: fields.map((field) => getComputedStyle(field).minInlineSize),
      headerWordsFit: [...container.querySelectorAll('th')].every(wordFits),
      containerOverflow: container.scrollWidth - container.clientWidth,
      firstRecordOffset: box(record).y - box(tableOrList).y,
    };
  }, stacked);
}

/**
 * Reads which layout the shared data table rendered at the current viewport. The table stacks from
 * its own container width, so the mode is read from the DOM after two frames. A desktop table needs
 * a container of at least 768 px; a narrower container must render labelled records.
 */
async function renderedStacked(page: Page): Promise<boolean> {
  await page.evaluate(
    () =>
      new Promise<void>((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
      ),
  );
  const table = page.locator('app-attempts-table');
  const stacked = (await table.locator(RECORD).count()) > 0;
  const containerWidth = await table.evaluate((host) => host.clientWidth);
  expect(stacked || containerWidth >= 768, `a ${containerWidth}px container must stack`).toBe(true);
  return stacked;
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
    const stacked = await renderedStacked(page);
    const layout = await attemptLayout(page, stacked);
    expect(layout.fieldBoxes).toHaveLength(5);
    expect(layout.containerOverflow, JSON.stringify(layout)).toBeLessThanOrEqual(1);
    const distinct = (values: number[]): number => new Set(values.map(Math.round)).size;
    if (stacked) {
      // The ordered list replaces the table entirely; each value now has a visible term.
      expect.soft(layout.head).toBeNull();
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
      expect.soft(layout.head?.height, JSON.stringify(layout)).toBeGreaterThan(20);
      expect.soft(layout.labelDisplays).toEqual([]);
      expect.soft(layout.firstRecordOffset, JSON.stringify(layout)).toBeGreaterThan(2);
      expect.soft(distinct(layout.fieldBoxes.map((field) => field.y))).toBe(1);
      expect.soft(distinct(layout.fieldBoxes.map((field) => field.x))).toBe(5);
      expect.soft(layout.headerWordsFit, JSON.stringify(layout)).toBe(true);
    }
  });
}

for (const locale of ['en', 'vi'] as const) {
  test(`attempts stack facts in a narrow container and align desktop cells (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 767, height: 900 });
    await openAttempts(page, locale);
    const table = page.locator('app-attempts-table');
    for (const width of [767, 1440, 768]) {
      await page.setViewportSize({ width, height: 900 });
      const stacked = await renderedStacked(page);
      const row = table.locator(stacked ? RECORD : DESKTOP_ROW).first();
      const fields = row.locator(stacked ? ':scope > dl > div' : ':scope > td');
      const headers = table.getByRole('columnheader');
      await expect(fields).toHaveCount(5);
      await expect(headers).toHaveCount(stacked ? 0 : 5);
      for (const field of await fields.all()) {
        await expect(field).toHaveCSS('display', stacked ? 'block' : 'table-cell');
        if (!stacked) await expect(field).toHaveCSS('vertical-align', 'middle');
      }
      if (!stacked) {
        for (const header of await headers.all()) {
          await expect(header).toHaveCSS('vertical-align', 'middle');
        }
        const numbers = page.locator('app-attempts-table :is(th,td)[data-column-role="number"]');
        await expect(numbers.first()).toBeVisible();
        for (const cell of await numbers.all()) {
          await expect(cell).toHaveCSS('text-align', 'end');
          await expect(cell).toHaveCSS('font-variant-numeric', 'tabular-nums');
        }
      }
    }
  });
}

test('attempts switch between labelled records and table rows on resize', async ({ page }) => {
  await seedStoredTheme(page, 'light');
  await page.setViewportSize({ width: 767, height: 900 });
  await openAttempts(page);
  const table = page.locator('app-attempts-table');
  const steps = [
    { width: 767, stacked: true },
    { width: 1440, stacked: false },
    { width: 768, stacked: true },
    { width: 767, stacked: true },
  ] as const;
  for (const { width, stacked } of steps) {
    await page.setViewportSize({ width, height: 900 });
    await expect(table.locator(stacked ? RECORD : DESKTOP_ROW)).toHaveCount(
      attemptsFixture.body.items.length,
    );
    await expect(table.locator(stacked ? DESKTOP_ROW : RECORD)).toHaveCount(0);
  }
});

test('attempts stack when their columns no longer fit the container', async ({ page }) => {
  await seedStoredTheme(page, 'light');
  await page.setViewportSize({ width: 1440, height: 900 });
  await openAttempts(page);
  const table = page.locator('app-attempts-table');
  const grid = table.locator('app-data-table');
  const rows = attemptsFixture.body.items.length;
  await page.evaluate(() => (document.documentElement.style.fontSize = '150%'));
  await expect(table.locator(DESKTOP_ROW)).toHaveCount(rows);
  // Enlarged text raises the table's intrinsic minimum. Keep the panel above the
  // record breakpoint so only measured overflow can switch it to records.
  const panelWidth = 832;
  expect(await grid.evaluate((el) => el.clientWidth)).toBeGreaterThan(panelWidth);
  await table.evaluate((el, width) => (el.style.inlineSize = `${width}px`), panelWidth);
  expect(await grid.evaluate((el) => el.clientWidth)).toBeGreaterThanOrEqual(768);
  await expect(table.locator(RECORD)).toHaveCount(rows);
  await expect(table.locator(DESKTOP_ROW)).toHaveCount(0);
  const overflow = await page
    .locator('app-attempts-table .scroll')
    .evaluate((el) => el.scrollWidth - el.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await table.evaluate((el) => el.style.removeProperty('inline-size'));
  await expect(table.locator(DESKTOP_ROW)).toHaveCount(rows);
  await expect(table.locator(RECORD)).toHaveCount(0);
});

for (const locale of ['en', 'vi'] as const) {
  test(`attempts page-size read retains its table and pager until feedback settles (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(
      `${runDetailPath({ locale })}&fixture.variant.listRunAttempts=page-1&fixture.latency=1500`,
    );
    const table = page.locator('app-attempts-table');
    const rows = table.locator(DESKTOP_ROW);
    await expect(rows.first()).toBeVisible({ timeout: 15000 });
    const retained = await rows.allTextContents();
    const pager = table.locator('app-data-table-pager');
    await pager.getByRole('combobox').click();
    await page.getByRole('option', { name: '50', exact: true }).click();
    const feedback = pager.getByRole('progressbar');
    await expect(feedback).toBeInViewport();
    await expect(feedback).toHaveAccessibleName(locale === 'en' ? 'Loading…' : 'Đang tải…');
    await expect(table.locator('table')).toBeVisible();
    expect(await rows.allTextContents()).toEqual(retained);
    await expect(pager.locator('nav')).toHaveAttribute('aria-busy', 'true');
    await expect(feedback).toHaveCount(0);
    await expect(pager.locator('nav')).toHaveAttribute('aria-busy', 'false');
    await expect(pager.getByRole('combobox')).toContainText('50');
    await expect(rows).toHaveCount(retained.length);
  });
}
