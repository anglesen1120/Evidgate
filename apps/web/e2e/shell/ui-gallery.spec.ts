// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC6; 00 §5.6 W9; T7 hand-over): the shared-UI data table as
// it behaves in a real Chromium at the narrow end — 360px is the width the owner reported mid-word
// breaks and wrapped badges at in earlier design rounds, so it is the width that can regress.
//
// Everything here is a layout property jsdom cannot have: real scroll boxes, a classic scrollbar
// that takes width, a sticky header, the top layer of a native popover, and the accessibility tree
// Chromium builds. The gutter assertion runs under the classic scrollbar this headless Chrome
// draws by default (measured non-zero); if a future Chrome or launch flag switches to overlay
// scrollbars, that assertion fails loudly rather than letting the column-alignment check measure a
// width the user can never see.
//
// Defects this file finds in `shared/ui` are REPORTED, not fixed (the task owns `shell/**` only).
import { expect, test, type Page } from '@playwright/test';

import { localePath, LOCALES, THEMES } from './support';

const HASH_OF_FIRST_ROW = 'sha256:demo-evidence-0-0123456789abcdef';

/** `/dev/ui-gallery` is review-only and has no shell, so a theme is just the attribute on `<html>`. */
async function openGallery(
  page: Page,
  locale: (typeof LOCALES)[number],
  theme: (typeof THEMES)[number],
): Promise<void> {
  await page.goto(localePath(locale, '/dev/ui-gallery'));
  await expect(page.locator('h1').first()).toBeVisible();
  await expect(page.locator('[data-demo="table-small"] .body-table tr').first()).toBeVisible();
  await page.evaluate((choice) => {
    if (choice === 'light') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', choice);
  }, theme);
}

interface Box {
  readonly left: number;
  readonly top: number;
  readonly right: number;
  readonly bottom: number;
  readonly width: number;
  readonly height: number;
}

/** The table's own measurements, read in one round trip so they describe the same frame. */
async function tableMetrics(page: Page): Promise<{
  scroll: Box & {
    scrollWidth: number;
    clientWidth: number;
    scrollHeight: number;
    clientHeight: number;
  };
  header: Box;
  headerCells: readonly { left: number; width: number }[];
  bodyCells: readonly { left: number; width: number }[];
  scrollbarWidth: number;
  viewportWidth: number;
}> {
  return page.evaluate(() => {
    const root = document.querySelector('[data-demo="table-small"]') as HTMLElement;
    const scroll = root.querySelector('.scroll') as HTMLElement;
    const header = root.querySelector('.header-table') as HTMLElement;
    const bodyRow = root.querySelector('.body-table tr') as HTMLElement;
    const box = (element: Element) => {
      const rect = element.getBoundingClientRect();
      return {
        left: Math.round(rect.left * 100) / 100,
        top: Math.round(rect.top * 100) / 100,
        right: Math.round(rect.right * 100) / 100,
        bottom: Math.round(rect.bottom * 100) / 100,
        width: Math.round(rect.width * 100) / 100,
        height: Math.round(rect.height * 100) / 100,
      };
    };
    const cells = (table: Element) =>
      Array.from(table.querySelectorAll('tr:first-child > .cell')).map((cell) => {
        const rect = cell.getBoundingClientRect();
        return {
          left: Math.round(rect.left * 100) / 100,
          width: Math.round(rect.width * 100) / 100,
        };
      });
    return {
      scroll: {
        ...box(scroll),
        scrollWidth: scroll.scrollWidth,
        clientWidth: scroll.clientWidth,
        scrollHeight: scroll.scrollHeight,
        clientHeight: scroll.clientHeight,
      },
      header: box(header),
      headerCells: cells(header),
      bodyCells: cells(bodyRow),
      scrollbarWidth: scroll.offsetWidth - scroll.clientWidth,
      viewportWidth: window.innerWidth,
    };
  });
}

test.describe('the gallery never scrolls sideways (T7, 360px)', () => {
  test.use({ viewport: { width: 360, height: 800 } });

  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`no horizontal overflow: ${locale} / ${theme}`, async ({ page }) => {
        await openGallery(page, locale, theme);

        const layout = await page.evaluate(() => {
          const limit = window.innerWidth;
          // The deepest elements whose box really crosses the right edge: an element wider than the
          // viewport usually has a parent that already contains it, and naming the deepest one names
          // the culprit a fix has to touch.
          const offenders = Array.from(document.querySelectorAll('body *'))
            .filter((element) => {
              const rect = element.getBoundingClientRect();
              if (rect.width === 0 || rect.height === 0) return false;
              if (rect.right <= limit + 0.5) return false;
              return !Array.from(element.children).some((child) => {
                const childRect = child.getBoundingClientRect();
                return childRect.width > 0 && childRect.right > limit + 0.5;
              });
            })
            .slice(0, 8)
            .map((element) => {
              const rect = element.getBoundingClientRect();
              return `${element.tagName.toLowerCase()}.${element.className || '—'} right=${Math.round(rect.right)}`;
            });
          return {
            htmlScrollWidth: document.documentElement.scrollWidth,
            bodyScrollWidth: document.body.scrollWidth,
            viewport: limit,
            offenders,
          };
        });

        expect(
          layout.htmlScrollWidth,
          `documentElement.scrollWidth ${layout.htmlScrollWidth} vs viewport ${layout.viewport}; ` +
            `offenders: ${layout.offenders.join(' | ') || 'none'}`,
        ).toBeLessThanOrEqual(layout.viewport);
        expect(layout.bodyScrollWidth).toBeLessThanOrEqual(layout.viewport);
      });
    }
  }
});

for (const locale of LOCALES) {
  test.describe(`table mechanics, ${locale} bundle (T7)`, () => {
    test.use({ viewport: { width: 360, height: 800 } });

    test('the small table really scrolls and its header sticks above the body', async ({
      page,
    }) => {
      await openGallery(page, locale, 'light');
      const scroll = page.locator('[data-demo="table-small"] .scroll');
      const before = await tableMetrics(page);

      // A reserved viewport of eight rows over twelve rows: the box has somewhere to scroll to, and
      // a classic scrollbar that takes layout width (the alignment case below depends on it).
      expect(before.scroll.scrollHeight).toBeGreaterThan(before.scroll.clientHeight);
      expect(
        before.scrollbarWidth,
        'a classic scrollbar gutter is present, so the header and body share the width the user sees',
      ).toBeGreaterThan(0);
      expect(before.scroll.scrollWidth).toBeLessThanOrEqual(before.scroll.clientWidth);

      await scroll.hover();
      await page.mouse.wheel(0, 320);
      await expect.poll(() => scroll.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);

      const scrolled = await tableMetrics(page);
      // The header is still pinned to the top of the box it scrolls inside: no gap above it, and it
      // did not scroll away with the body.
      expect(Math.abs(scrolled.header.top - scrolled.scroll.top)).toBeLessThanOrEqual(1.5);
      // And the columns still line up: a sticky header that loses the scrollbar's width drifts by
      // exactly that width on the last column.
      expect(scrolled.headerCells).toEqual(scrolled.bodyCells);
      expect(scrolled.headerCells.length).toBe(4);
    });

    test('a clipped value opens a native popover in the top layer, unclipped', async ({ page }) => {
      await openGallery(page, locale, 'light');

      const values = page.locator('[data-demo="table-small"] .body-table .value.clipped');
      expect(
        await values.count(),
        'the hash column is truncated, so at least the visible rows are clipped',
      ).toBeGreaterThan(0);

      // Scroll the table to its end, then use a cell that is still visible there: a popover whose
      // anchor is scrolled out of the box would be positioned off-screen for the wrong reason.
      const scroll = page.locator('[data-demo="table-small"] .scroll');
      await scroll.evaluate((element) => {
        element.scrollTop = element.scrollHeight;
      });

      const value = values.last();
      const cell = value.locator('..');
      const popover = cell.locator('.full-value');
      await value.focus();
      await expect(popover).toHaveAttribute('popover', 'manual');
      await expect(popover).toBeVisible();
      // The cell shows an abbreviated (ellipsised) value; the panel carries the whole one.
      const shownValue = (await value.innerText()).trim();

      const popoverState = await popover.evaluate((element) => {
        const rect = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        return {
          open: element.matches(':popover-open'),
          text: element.textContent ?? '',
          fullyDrawn:
            element.scrollHeight <= element.clientHeight + 1 &&
            element.scrollWidth <= element.clientWidth + 1,
          withinViewport: rect.left >= -0.5 && rect.right <= window.innerWidth + 0.5,
          display: style.display,
        };
      });
      expect(popoverState.open, 'the popover is in the top layer').toBe(true);
      expect(popoverState.display).not.toBe('none');
      expect(popoverState.text.trim()).toBe(shownValue);
      expect(popoverState.fullyDrawn, 'the whole value is drawn, not clipped').toBe(true);
      expect(popoverState.withinViewport, 'the popover stays inside the viewport').toBe(true);

      // Top layer, tested from the compositor's point of view: at the centre of the popover, the hit
      // test returns the popover itself — not the sticky header, not the scroll box's edge.
      const onTop = await popover.evaluate((element) => {
        const rect = element.getBoundingClientRect();
        const hit = document.elementFromPoint(
          rect.left + rect.width / 2,
          rect.top + rect.height / 2,
        );
        return hit === element || element.contains(hit);
      });
      expect(onTop, 'nothing sticky or clipped paints over the popover').toBe(true);

      // Escape closes it; the element stays in the DOM (it is the same span, re-opened on focus).
      await page.keyboard.press('Escape');
      await expect(popover).not.toBeVisible();
    });
  });
}

test.describe('a clipped value in the accessibility tree (T7 report item)', () => {
  test.use({ viewport: { width: 360, height: 800 } });

  test('counts the copies Chromium exposes, closed and open', async ({ page }) => {
    await openGallery(page, 'en', 'light');
    const cell = page.locator('[data-demo="table-small"] .body-table tr:first-child td').first();

    // The DOM contract first: one visible value, one visually-hidden description the value points
    // at, and one popover that is not rendered until it opens.
    const domCopies = await cell.evaluate(
      (element, value) =>
        Array.from(element.querySelectorAll('span')).filter(
          (span) => (span.textContent ?? '').trim() === value,
        ).length,
      HASH_OF_FIRST_ROW,
    );
    expect(domCopies, 'visible span + sr-only description + popover').toBe(3);

    // Chromium's accessibility tree, split by the way each copy is exposed: the cell's accessible
    // NAME (what a reader announces when the cell is reached, built from every text node inside it)
    // and tooltip nodes.
    const exposures = (snapshot: string) => {
      const lines = snapshot.split('\n');
      const name = lines.filter((line) => /^\s*-\s+cell\b/.test(line)).join(' ');
      return {
        nameCopies: name.split(HASH_OF_FIRST_ROW).length - 1,
        tooltipCopies: lines.filter((line) => /^\s*-\s+tooltip/.test(line)).length,
        rawCopies: snapshot.split(HASH_OF_FIRST_ROW).length - 1,
      };
    };

    const closed = await cell.ariaSnapshot();
    const closedExposure = exposures(closed);
    // TWO copies in one closed cell: the visible span, plus the `sr-only` span it points at with
    // `aria-describedby` — so the value is announced and then announced again as the description.
    // This is the count the report quotes (`shared/ui`, not fixable from the shell).
    expect(closedExposure.nameCopies, `aria snapshot while closed:\n${closed}`).toBe(2);
    expect(closedExposure.tooltipCopies).toBe(0);

    await cell.locator('.value').focus();
    await expect(cell.locator('.full-value')).toBeVisible();
    const open = await cell.ariaSnapshot();
    const openExposure = exposures(open);
    // The open popover adds a third copy to the name and exactly one tooltip node.
    expect(openExposure.nameCopies, `aria snapshot while open:\n${open}`).toBe(3);
    expect(openExposure.tooltipCopies).toBe(1);
    test.info().annotations.push({
      type: 'clipped-value-exposure',
      description:
        `closed name=${closedExposure.nameCopies} tooltip=${closedExposure.tooltipCopies} ` +
        `raw=${closedExposure.rawCopies}; open name=${openExposure.nameCopies} ` +
        `tooltip=${openExposure.tooltipCopies} raw=${openExposure.rawCopies}`,
    });
  });
});

test.describe('column alignment at a desktop width (T7)', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('header and body columns share one grid', async ({ page }) => {
    await openGallery(page, 'en', 'light');
    const metrics = await tableMetrics(page);
    expect(metrics.headerCells).toEqual(metrics.bodyCells);
    expect(Math.abs(metrics.header.top - metrics.scroll.top)).toBeLessThanOrEqual(1.5);
  });
});
