import { expect, test, type Locator } from '@playwright/test';

import { runDetailPath } from './run-detail.support';
import { LOCALES } from './support';

async function doubleHeaderText(header: Locator, spaced: boolean) {
  return header.evaluate((root, applySpacing) => {
    const elements = Array.from(
      root.querySelectorAll<HTMLElement>('h1, .header-description, a[appPageHeaderActions]'),
    );
    // Snapshot first: mutating an ancestor must not double an inherited font twice.
    const originals = elements.map((element) => {
      const style = getComputedStyle(element);
      return {
        element,
        fontSize: Number.parseFloat(style.fontSize),
        lineHeight: Number.parseFloat(style.lineHeight),
      };
    });
    for (const { element, fontSize, lineHeight } of originals) {
      // Read the final size, not the first frame of Helm's transition-all.
      element.style.transition = 'none';
      element.style.fontSize = `${fontSize * 2}px`;
      element.style.lineHeight = applySpacing ? '1.5' : `${lineHeight * 2}px`;
      if (applySpacing) {
        element.style.letterSpacing = '0.12em';
        element.style.wordSpacing = '0.16em';
        if (element.tagName === 'P') element.style.marginBlockEnd = '2em';
      }
    }
    return originals.map(({ element, fontSize }) => ({
      tag: element.tagName,
      before: fontSize,
      after: Number.parseFloat(getComputedStyle(element).fontSize),
      text: element.textContent,
    }));
  }, spaced);
}

async function headerGeometry(header: Locator) {
  return header.evaluate((root) => {
    const row = root.querySelector<HTMLElement>('.header-row')!;
    const actions = root.querySelector<HTMLElement>('.header-actions')!;
    const action = root.querySelector<HTMLElement>('a[appPageHeaderActions]')!;
    const actionBounds = action.getBoundingClientRect();
    return {
      viewportWidth: window.innerWidth,
      documentWidth: document.documentElement.scrollWidth,
      headerWidth: root.getBoundingClientRect().width,
      rowWidth: row.clientWidth,
      rowOverflow: row.scrollWidth - row.clientWidth,
      actionVerticalOverflow: action.scrollHeight - action.clientHeight,
      clipsContent: [root, row, actions].some((element) =>
        ['overflowX', 'overflowY'].some((axis) =>
          ['hidden', 'clip'].includes(getComputedStyle(element)[axis as 'overflowX' | 'overflowY']),
        ),
      ),
      actionsOverflow: actions.scrollWidth - actions.clientWidth,
      actionWidth: actionBounds.width,
      actionHeight: actionBounds.height,
      whiteSpace: getComputedStyle(action).whiteSpace,
      boxSizing: getComputedStyle(action).boxSizing,
    };
  });
}

test.use({ hasTouch: true });

for (const locale of LOCALES) {
  for (const theme of ['light', 'dark'] as const) {
    test(`page header reflows genuinely doubled text (${locale}, ${theme})`, async ({
      page,
    }, testInfo) => {
      for (const width of [320, 390]) {
        for (const spaced of [false, true]) {
          await page.setViewportSize({ width, height: 900 });
          await page.goto(runDetailPath({ locale }));
          await expect(page.locator('app-run-detail-page')).toHaveAttribute(
            'data-run-detail-status',
            'ready',
          );
          await page.evaluate((value) => {
            document.documentElement.dataset['theme'] = value;
          }, theme);
          await page.evaluate(() => document.fonts.ready);
          const header = page.locator('app-run-detail-page > app-page-header');
          const back = header.locator('a[appPageHeaderActions]');
          const label = await back.innerText();
          const fonts = await doubleHeaderText(header, spaced);
          expect(fonts.map((font) => font.tag)).toEqual(['H1', 'A']);
          for (const font of fonts) {
            expect(font.before).toBeGreaterThan(0);
            expect(font.after).toBeCloseTo(font.before * 2, 4);
          }
          const geometry = await headerGeometry(header);
          await testInfo.attach(`header-${width}-${spaced ? 'spaced' : 'doubled'}`, {
            body: JSON.stringify({ locale, theme, width, spaced, fonts, geometry }, null, 2),
            contentType: 'application/json',
          });
          expect(geometry.whiteSpace).toBe('normal');
          expect(geometry.boxSizing).toBe('border-box');
          expect(geometry.documentWidth - geometry.viewportWidth).toBeLessThanOrEqual(1);
          expect(geometry.rowOverflow).toBeLessThanOrEqual(1);
          expect(geometry.actionsOverflow).toBeLessThanOrEqual(1);
          expect(geometry.actionWidth).toBeLessThanOrEqual(geometry.rowWidth + 1);
          expect(geometry.actionHeight).toBeGreaterThanOrEqual(44);
          expect(geometry.actionVerticalOverflow).toBeLessThanOrEqual(1);
          expect(geometry.clipsContent).toBe(false);
          expect(geometry.actionWidth).toBeGreaterThanOrEqual(44);
          await expect(back).toHaveText(label);
          await expect(back).toHaveAttribute('href', /\/runs(?:\?|$)/);
          await back.focus();
          await expect(back).toBeFocused();
        }
      }
    });
  }
}
