// The gallery's 250-row virtual specimen in a real Chromium. Row stride, scroll range and the
// clipped scrollport box are layout facts jsdom cannot have, so they are asserted here; the gallery
// page itself (sideways overflow, value clipping, column budgets, headings) stays in
// `ui-gallery.spec.ts`.
import { expect, test, type Page } from '@playwright/test';

import { localePath, LOCALES, THEMES } from './support';

/** `/dev/ui-gallery` is review-only and has no shell, so a theme is just the attribute on `<html>`. */
async function openGallery(
  page: Page,
  locale: (typeof LOCALES)[number],
  theme: (typeof THEMES)[number],
): Promise<void> {
  await page.goto(localePath(locale, '/dev/ui-gallery'));
  await expect(page.locator('h1').first()).toBeVisible();
  // Below the 768px breakpoint the table renders as stacked records, so readiness is the first
  // data row of either anatomy inside the scroll box, not a `<tbody>` row.
  await expect(
    page.locator('[data-demo="table-small"] :is(tbody tr, li.eg-table-record)').first(),
  ).toBeVisible();
  await page.evaluate((choice) => {
    if (choice === 'light') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', choice);
  }, theme);
}

test.describe('the virtual window keeps its fixed stride', () => {
  test('the virtual window keeps its 44px stride at 1024 vi', async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 900 });
    await openGallery(page, 'vi', 'light');

    const stride = await page.evaluate(() => {
      const root = document.querySelector('[data-demo="table-virtual"]') as HTMLElement;
      const viewport = root.querySelector('cdk-virtual-scroll-viewport') as HTMLElement;
      const rows = Array.from(root.querySelectorAll('tbody tr[data-row-id]'));
      return {
        heights: rows.map((row) => Math.round(row.getBoundingClientRect().height)),
        scrollHeight: viewport.scrollHeight,
      };
    });
    expect(stride.heights.length).toBeGreaterThan(0);
    expect(stride.heights, 'every rendered row keeps the fixed stride').toEqual(
      stride.heights.map(() => 44),
    );
    // 250 rows × 44px: a wrapped row would enlarge the scroll range and the last row would move.
    expect(Math.abs(stride.scrollHeight - 250 * 44)).toBeLessThanOrEqual(44);

    await page.evaluate(() => {
      const viewport = document.querySelector(
        '[data-demo="table-virtual"] cdk-virtual-scroll-viewport',
      ) as HTMLElement;
      viewport.scrollTop = viewport.scrollHeight;
    });
    // `toBeVisible` alone would accept a row painted below the scrollport, and that is exactly what
    // a wrapping identity cell does (the last row then sits hundreds of pixels under the clip after
    // the first bottom scroll), so measure the row against the viewport's own box, after the scroll,
    // and re-check the stride and range.
    await expect(
      page.locator('[data-demo="table-virtual"] tbody tr[aria-rowindex="251"]'),
      'a single scroll to the end renders the last row',
    ).toHaveCount(1);

    const end = await page.evaluate(() => {
      const root = document.querySelector('[data-demo="table-virtual"]') as HTMLElement;
      const viewport = root.querySelector('cdk-virtual-scroll-viewport') as HTMLElement;
      const rows = Array.from(root.querySelectorAll('tbody tr[data-row-id]'));
      const clip = viewport.getBoundingClientRect();
      const last = root.querySelector('tbody tr[aria-rowindex="251"]')?.getBoundingClientRect();
      return {
        heights: rows.map((row) => Math.round(row.getBoundingClientRect().height)),
        scrollHeight: viewport.scrollHeight,
        scrollTop: Math.round(viewport.scrollTop),
        lastTop: last ? Math.round(last.top) : null,
        lastBottom: last ? Math.round(last.bottom) : null,
        clipTop: Math.round(clip.top),
        clipBottom: Math.round(clip.bottom),
      };
    });
    const { lastTop, lastBottom } = end;
    if (lastTop === null || lastBottom === null) throw new Error('the last row was not rendered');
    expect(end.scrollTop, 'the first bottom scroll actually moved the window').toBeGreaterThan(0);
    expect(
      lastBottom,
      `last row bottom ${lastBottom} vs scrollport bottom ${end.clipBottom}`,
    ).toBeLessThanOrEqual(end.clipBottom + 1);
    expect(
      lastTop,
      `last row top ${lastTop} vs scrollport top ${end.clipTop}`,
    ).toBeGreaterThanOrEqual(end.clipTop - 1);
    // The scrolled window keeps the stride and its range: a late-wrapping row would grow both.
    expect(end.heights, 'scrolled rows keep the fixed stride').toEqual(end.heights.map(() => 44));
    expect(end.scrollHeight, 'the range does not grow after scrolling').toBe(stride.scrollHeight);
  });
});
