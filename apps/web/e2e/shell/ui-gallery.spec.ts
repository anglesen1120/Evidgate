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
//
// The table has two anatomies: below a 768px container it renders stacked records, at or above it a
// single real table (header row + body rows in one grid). So the 360px reflow budget covers the
// narrow end, table mechanics are asserted at the narrowest width where the table renders, and
// value clipping is asserted on the fixed-layout (virtual) table, whose equal column shares clip
// every value regardless of the container width.
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
}> {
  return page.evaluate(() => {
    const root = document.querySelector('[data-demo="table-small"]') as HTMLElement;
    const scroll = root.querySelector('.scroll') as HTMLElement;
    // One table, one grid: the header row's first cell is the sticky element (`thead th` carries
    // the sticky rule) and the body's first row is the first `tbody` row.
    const header = root.querySelector('thead tr > *') as HTMLElement;
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
    const cells = (selector: string) =>
      Array.from(root.querySelectorAll(selector)).map((cell) => {
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
      headerCells: cells('thead tr > *'),
      bodyCells: cells('tbody tr:first-child > *'),
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
    // Below its 768px container breakpoint the table renders as stacked records (approved
    // redesign), so table mechanics are asserted at the narrowest width where the table itself
    // renders; the 360px reflow budget above still covers the narrow end.
    test.use({ viewport: { width: 900, height: 800 } });

    test('the ordinary specimen flows with the page, the virtual one keeps its window', async ({
      page,
    }) => {
      await openGallery(page, locale, 'light');
      const metrics = await tableMetrics(page);

      // Page flow (layout sweep LYT-03): the twelve-row example is not an inner row viewport any
      // more, so every row is laid out in the document and the page owns the vertical scrollbar.
      expect(metrics.scroll.scrollHeight).toBeLessThanOrEqual(metrics.scroll.clientHeight + 1);
      expect(await page.locator('[data-demo="table-small"] tbody tr[data-row-id]').count()).toBe(
        12,
      );
      expect(metrics.scroll.scrollWidth).toBeLessThanOrEqual(metrics.scroll.clientWidth);
      expect(metrics.headerCells.length).toBe(4);

      // The virtual example keeps its bounded window: 250 rows in a viewport of eight is the
      // contract that window was built for, not a defect.
      const virtual = await page.evaluate(() => {
        const root = document.querySelector('[data-demo="table-virtual"]') as HTMLElement;
        const viewport = root.querySelector('cdk-virtual-scroll-viewport') as HTMLElement;
        return { scrollHeight: viewport.scrollHeight, clientHeight: viewport.clientHeight };
      });
      expect(virtual.scrollHeight).toBeGreaterThan(virtual.clientHeight);
    });

    test('a clipped value opens a native popover in the top layer, unclipped', async ({ page }) => {
      await openGallery(page, locale, 'light');

      // The virtual table is the fixed-layout one: its columns get an equal share of the box, so the
      // whole hash does not fit and every rendered value is clipped. The plain table gives the
      // identity column its content width, so nothing there is clipped.
      const values = page.locator('[data-demo="table-virtual"] .body-table .value.clipped');
      expect(
        await values.count(),
        'the fixed-layout hash column is narrower than the whole value',
      ).toBeGreaterThan(0);

      const value = values.first();
      // Bring the anchor into the viewport: the popover is placed next to it, and the hit test below
      // has to land on painted pixels.
      await value.scrollIntoViewIfNeeded();
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
  // Table mode: the cell/`td` exposure below is the table anatomy the report measured.
  test.use({ viewport: { width: 900, height: 800 } });

  test('counts the copies Chromium exposes, closed and open', async ({ page }) => {
    await openGallery(page, 'en', 'light');
    // The fixed-layout table truncates its text column, so this cell holds the clipped-value
    // anatomy: `.value` (customary clipping, full text) plus the `.full-value` popover. The identity
    // column renders the copyable-identifier component instead, with its own disclosure dialog.
    const cell = page.locator('[data-demo="table-virtual"] tbody tr:first-child > td').nth(1);
    const fullValue = ((await cell.locator('.value').textContent()) ?? '').trim();
    expect(fullValue, 'the value cell carries text').not.toBe('');

    // The DOM contract first: one visible value (the accessible copy) and one popover that is
    // hidden from assistive technology and not rendered until it opens.
    const domCopies = await cell.evaluate(
      (element, value) =>
        Array.from(element.querySelectorAll('span')).filter(
          (span) => (span.textContent ?? '').trim() === value,
        ).length,
      fullValue,
    );
    expect(domCopies, 'visible span + aria-hidden popover').toBe(2);

    // Chromium's accessibility tree, split by the way each copy is exposed: the cell's accessible
    // NAME (what a reader announces when the cell is reached, built from every text node inside it)
    // and tooltip nodes.
    const exposures = (snapshot: string) => {
      const lines = snapshot.split('\n');
      // A name with punctuation is quoted by the snapshot (`- 'cell "…"'`), so the role token may sit
      // behind a quote character.
      const name = lines.filter((line) => /^\s*-\s+'?cell\b/.test(line)).join(' ');
      return {
        nameCopies: name.split(fullValue).length - 1,
        tooltipCopies: lines.filter((line) => /^\s*-\s+tooltip/.test(line)).length,
        rawCopies: snapshot.split(fullValue).length - 1,
      };
    };

    const closed = await cell.ariaSnapshot();
    const closedExposure = exposures(closed);
    // ONE copy in a closed cell: the value span itself (no `sr-only` description repeating it).
    expect(closedExposure.nameCopies, `aria snapshot while closed:\n${closed}`).toBe(1);
    expect(closedExposure.tooltipCopies).toBe(0);

    await cell.locator('.value').focus();
    await expect(cell.locator('.full-value')).toBeVisible();
    const open = await cell.ariaSnapshot();
    const openExposure = exposures(open);
    // The open popover is `aria-hidden`: still one copy, and no tooltip node.
    expect(openExposure.nameCopies, `aria snapshot while open:\n${open}`).toBe(1);
    expect(openExposure.tooltipCopies).toBe(0);
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

test.describe('layout sweep pins (LYT-03, LYT-06, LYT-07)', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('column budgets bound the identity column and flex the context (LYT-03)', async ({
    page,
  }) => {
    await openGallery(page, 'en', 'light');
    const widths = await page.evaluate(() => {
      const table = document.querySelector('[data-demo="table-small"] table') as HTMLElement;
      const cells = Array.from(table.querySelectorAll('thead th'));
      const tableWidth = table.getBoundingClientRect().width;
      const [identity = 0, context = 0, attempts = 0, mutation = 0] = cells.map(
        (cell) => cell.getBoundingClientRect().width,
      );
      return { tableWidth, identity, context, attempts, mutation };
    });

    // The identity column is bounded, the narrative column takes the spare room, and the utility
    // columns stay near their content width instead of stretching.
    expect(widths.identity).toBeLessThan(widths.context);
    expect(widths.identity).toBeLessThan(widths.tableWidth * 0.4);
    expect(widths.attempts).toBeLessThan(widths.context);
    expect(widths.mutation).toBeLessThan(widths.context);
  });

  test('the short-key form and its actions keep an expected content width (LYT-06)', async ({
    page,
  }) => {
    await openGallery(page, 'en', 'light');
    const boxes = await page.evaluate(() => {
      // The section is labelled by `#gallery-fields` (an `h2`), so the form is its sibling.
      const form = document.querySelector('section[aria-labelledby="gallery-fields"] form');
      if (!(form instanceof HTMLElement)) throw new Error('Expected the gallery field specimen');
      const input = form.querySelector('input');
      const submit = form.querySelector('button[type="submit"]');
      if (!(input instanceof HTMLElement) || !(submit instanceof HTMLElement)) {
        throw new Error('Expected the specimen input and submit action');
      }
      const section = form.parentElement;
      if (!(section instanceof HTMLElement)) throw new Error('Expected the specimen section');
      return {
        input: input.getBoundingClientRect().width,
        submit: submit.getBoundingClientRect().width,
        section: section.getBoundingClientRect().width,
      };
    });

    // A short key is entered in a bounded field, and the save action is content-sized, not a band.
    expect(boxes.input).toBeLessThan(boxes.section);
    expect(boxes.input).toBeLessThanOrEqual(460);
    expect(boxes.submit).toBeLessThan(300);
  });

  test('the role specimen reflows to labelled records instead of hiding columns (LYT-07)', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 768, height: 900 });
    await openGallery(page, 'en', 'light');
    const specimen = page.locator('[data-demo="gallery-column-roles"]');

    await expect(specimen.locator('table'), 'no table squeezes columns out of view').toHaveCount(0);
    await expect(specimen.locator('li.eg-table-record')).toHaveCount(2);
    await expect(specimen.locator('dt')).toHaveCount(8);
    await expect(specimen.locator('app-hash-chip')).toHaveCount(2);
    await expect(specimen.locator('app-status-pill')).toHaveCount(2);

    // The copy utility keeps a one-line label, so its action never wraps into a tall sliver.
    const copyLines = await specimen
      .locator('app-copy-value-button button')
      .first()
      .evaluate((button) => {
        const range = document.createRange();
        range.selectNodeContents(button);
        return range.getClientRects().length;
      });
    expect(copyLines).toBe(1);

    await page.setViewportSize({ width: 390, height: 900 });
    await expect(specimen.locator('dt')).toHaveCount(8);
  });
});
