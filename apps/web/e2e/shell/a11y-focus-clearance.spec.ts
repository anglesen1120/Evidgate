// WCAG 2.4.11 (focus not obscured): the shell's topbar is sticky, so a control that scrolls into
// view must come to rest below it instead of under it. The offset is a measured custom property
// (`--eg-topbar-height`, published by the shell), so these cases cover the plain bar, a taller —
// wrapped — bar, and the mobile bar; the third case pins why the offset is `scroll-margin-top` on
// the focused control and not `scroll-padding-top` on the scroller.
//
// Every clearance case walks the real keyboard path (Tab into the page, Shift+Tab back up to a stop
// the document has scrolled past), because the browser's own focus scroll is what has to clear the
// bar; `focus()` alone would only prove what a scripted call does.
import { expect, test, type Page } from '@playwright/test';
import { runDetailPath } from './run-detail.support';

/** A run-detail page: long enough that its early controls scroll far above the viewport. */
const LONG_RUN = runDetailPath({
  decision: 'evaluated-fail-missing-shard',
  attempts: 'ok',
  revisions: 'ok',
  evidenceRevision: 3,
});

interface BarMetrics {
  /** The sticky bar's bottom edge, in viewport pixels. */
  readonly bottom: number;
  /** The bar's measured height, as the layout's custom property carries it. */
  readonly published: string;
  readonly scrollY: number;
  readonly maxScroll: number;
}

/** The bar and the document scroll, read in one round trip so they describe the same frame. */
async function barMetrics(page: Page): Promise<BarMetrics> {
  return page.evaluate(() => {
    const bar = document.querySelector<HTMLElement>('header.topbar');
    const scroller = document.scrollingElement;
    if (bar === null || scroller === null) throw new Error('the shell or its scroller is missing');
    return {
      bottom: bar.getBoundingClientRect().bottom,
      published: getComputedStyle(document.documentElement)
        .getPropertyValue('--eg-topbar-height')
        .trim(),
      scrollY: scroller.scrollTop,
      maxScroll: scroller.scrollHeight - scroller.clientHeight,
    };
  });
}

/** Opens the run detail page and proves the document really scrolls, so the cases are not vacuous. */
async function openLongPage(page: Page): Promise<void> {
  await page.goto(LONG_RUN);
  await expect(page.locator('#main-content h1')).toBeVisible();
  // A focus scroll is only observable on a document that can scroll past its own content.
  expect((await barMetrics(page)).maxScroll).toBeGreaterThan(600);
}

/**
 * The check itself. The run detail page puts the selected tab immediately before its own panel in
 * the tab order (`run-detail.page.html`), so the pair is a deterministic way to test the real
 * keyboard path: start on the panel, scroll the document until the tab is above the viewport, then
 * Shift+Tab back to it. The focus change under test is that keypress; the panel focus is setup, and
 * it does not scroll so the pre-state stays controlled.
 */
async function expectShiftTabBackClearsBar(page: Page): Promise<void> {
  const panel = page.locator('#main-content [role="tabpanel"][tabindex="0"]').first();
  await expect(panel).toBeAttached();
  await panel.evaluate((element) => (element as HTMLElement).focus({ preventScroll: true }));
  await page.evaluate(() => window.scrollTo(0, document.scrollingElement?.scrollHeight ?? 0));

  const tabAboveViewport = await page.evaluate(() => {
    const tab = document.querySelector('[role="tab"][aria-selected="true"]');
    return tab !== null && tab.getBoundingClientRect().bottom < 0;
  });
  expect(tabAboveViewport).toBe(true);

  await page.keyboard.press('Shift+Tab');
  const measured = await page.evaluate(() => {
    const tab = document.querySelector('[role="tab"][aria-selected="true"]');
    const bar = document.querySelector<HTMLElement>('header.topbar');
    if (tab === null || bar === null) throw new Error('the selected tab or the bar is missing');
    return {
      focused: document.activeElement === tab,
      top: tab.getBoundingClientRect().top,
      barBottom: bar.getBoundingClientRect().bottom,
      barHeight: bar.getBoundingClientRect().height,
      marginTop: Number.parseFloat(getComputedStyle(tab).scrollMarginTop),
    };
  });

  // The tab, not the panel or a skip link: focus moved back along the tab order as intended.
  expect(measured.focused).toBe(true);
  // The bar's bottom edge is the line the tab has to clear.
  expect(measured.top).toBeGreaterThanOrEqual(measured.barBottom - 0.5);
  // And the offset itself is the bar's measured height: Chromium centres a focus target it has to
  // reveal, so the geometry above holds even without the offset — this line is what pins the fix.
  expect(measured.marginTop).toBeGreaterThanOrEqual(measured.barHeight - 0.5);
  expect(measured.marginTop).toBeLessThanOrEqual(Math.ceil(measured.barHeight));
}

/**
 * The other pre-state: the tab is inside the viewport but wholly under the bar, so only the margin
 * box the offset adds can ask for a reveal. With the offset removed, the geometry assertion below is
 * the line that should fail.
 */
async function expectCoveredStopClearsBar(page: Page): Promise<void> {
  const panel = page.locator('#main-content [role="tabpanel"][tabindex="0"]').first();
  await expect(panel).toBeAttached();
  await panel.evaluate((element) => (element as HTMLElement).focus({ preventScroll: true }));

  const placed = await page.evaluate(() => {
    const tab = document.querySelector('[role="tab"][aria-selected="true"]');
    const bar = document.querySelector<HTMLElement>('header.topbar');
    const scroller = document.scrollingElement;
    if (tab === null || bar === null || scroller === null) {
      throw new Error('the selected tab, the bar or the scroller is missing');
    }
    const barBox = bar.getBoundingClientRect();
    // Rest the tab wholly behind the bar: its bottom edge a few pixels above the bar's bottom.
    const goal = barBox.bottom - tab.getBoundingClientRect().height - 4;
    scroller.scrollTop += tab.getBoundingClientRect().top - goal;
    const rect = tab.getBoundingClientRect();
    return {
      top: rect.top,
      bottom: rect.bottom,
      barBottom: barBox.bottom,
      barHeight: barBox.height,
      marginTop: Number.parseFloat(getComputedStyle(tab).scrollMarginTop),
      scrollY: scroller.scrollTop,
    };
  });
  // The case is only meaningful if the target really is on screen and really is behind the bar.
  expect(placed.top).toBeGreaterThanOrEqual(0);
  expect(placed.bottom, JSON.stringify(placed)).toBeLessThanOrEqual(placed.barBottom - 1);
  // And the stop must carry the offset this case is about, else the reveal could not come from it.
  expect(placed.marginTop).toBeGreaterThanOrEqual(placed.barHeight - 0.5);

  await page.keyboard.press('Shift+Tab');
  const measured = await page.evaluate(() => {
    const tab = document.querySelector('[role="tab"][aria-selected="true"]');
    const bar = document.querySelector<HTMLElement>('header.topbar');
    const scroller = document.scrollingElement;
    if (tab === null || bar === null || scroller === null) {
      throw new Error('the selected tab, the bar or the scroller is missing');
    }
    return {
      focused: document.activeElement === tab,
      top: tab.getBoundingClientRect().top,
      barBottom: bar.getBoundingClientRect().bottom,
      scrollY: scroller.scrollTop,
    };
  });

  expect(measured.focused).toBe(true);
  // The reveal is a scroll of the document, not only a geometry read.
  expect(measured.scrollY).not.toBe(placed.scrollY);
  // Geometric clearance: the revealed tab's top edge sits at or below the bar's bottom edge.
  expect(measured.top).toBeGreaterThanOrEqual(measured.barBottom - 0.5);
}

test.describe('focus scrolls clear of the sticky topbar (WCAG 2.4.11)', () => {
  test.use({ viewport: { width: 1280, height: 480 } });

  test('a keyboard stop scrolled back into view rests below the bar', async ({ page }) => {
    await openLongPage(page);
    const { bottom, published } = await barMetrics(page);
    // The property the stylesheet reads is the bar's own height, rounded up.
    expect(Number.parseFloat(published)).toBe(Math.ceil(bottom));

    await expectShiftTabBackClearsBar(page);
  });

  test('a keyboard stop the viewport shows under the bar is scrolled clear', async ({ page }) => {
    await openLongPage(page);
    await expectCoveredStopClearsBar(page);
  });

  test('the offset follows a topbar that wraps to a second row', async ({ page }) => {
    await openLongPage(page);
    // A second row is the layout's own variable: a long scope name pushes the flex row over, which
    // is why the offset is measured instead of a fixed token. The style stands in for that row.
    await page.addStyleTag({
      content: 'header.topbar { min-height: calc(2 * var(--eg-space-12)); }',
    });
    await expect
      .poll(async () => Number.parseFloat((await barMetrics(page)).published) || 0)
      .toBeGreaterThan(100);

    const { bottom, published } = await barMetrics(page);
    expect(Number.parseFloat(published)).toBe(Math.ceil(bottom));
    await expectShiftTabBackClearsBar(page);
  });

  test('focusing a control inside the bar never scrolls the page', async ({ page }) => {
    await openLongPage(page);
    // The bar sits at the top of the scroller. With `scroll-padding-top` on <html> a control inside
    // it would count as "under the padding", and every Tab through the header would scroll the page
    // up by the bar's height (Chromium 374034263).
    await page.evaluate(() => window.scrollTo(0, document.scrollingElement?.scrollHeight ?? 0));
    const before = (await barMetrics(page)).scrollY;
    expect(before).toBeGreaterThan(0);

    await page.locator('button.theme-toggle').focus();
    await expect(page.locator('button.theme-toggle')).toBeFocused();
    expect((await barMetrics(page)).scrollY).toBe(before);
  });
});

test.describe('focus clearance on the mobile bar', () => {
  test.use({ viewport: { width: 375, height: 640 } });

  test('a keyboard stop scrolled back into view rests below the single-row bar', async ({
    page,
  }) => {
    await openLongPage(page);
    const { bottom, published } = await barMetrics(page);
    expect(Number.parseFloat(published)).toBe(Math.ceil(bottom));
    await expectShiftTabBackClearsBar(page);
  });
});
