import { expect, test, type Locator } from '@playwright/test';
import { LOCALES, seedStoredTheme, THEMES } from './support';
import { HEAD_ID, PAGE, visit } from './settings-policy.support';

const LABELS = {
  en: [
    { visible: 'Values', accessible: 'Policy values' },
    { visible: 'Compare', accessible: 'Compare revisions' },
    { visible: 'Activation', accessible: 'Activation' },
  ],
  vi: [
    { visible: 'Giá trị', accessible: 'Giá trị chính sách' },
    { visible: 'So sánh', accessible: 'So sánh các phiên bản' },
    { visible: 'Kích hoạt', accessible: 'Kích hoạt' },
  ],
} as const;

async function labelGeometry(tab: Locator) {
  return tab.evaluate((element) => {
    const range = document.createRange();
    range.selectNodeContents(element);
    const rects = Array.from(range.getClientRects()).filter((rect) => rect.width > 0);
    const bounds = element.getBoundingClientRect();
    return {
      lines: new Set(rects.map((rect) => Math.round(rect.top))).size,
      fits: rects.every((rect) => rect.left >= bounds.left && rect.right <= bounds.right),
      height: bounds.height,
    };
  });
}

for (const locale of LOCALES) {
  for (const width of [1024, 1280]) {
    test(`UUID lookup is bounded at ${width}px (${locale})`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await visit(page, locale);
      const input = page.locator('#policy-revision-id');
      await expect(input).toHaveValue(HEAD_ID);
      const field = await input.boundingBox();
      const action = await page.locator('[data-policy-open]').boundingBox();
      if (!field || !action) throw new Error('Expected the policy lookup controls');
      expect(field.width).toBeLessThanOrEqual(420);
      expect(field.x + field.width).toBeLessThan(action.x);
      expect(field.y + field.height).toBeCloseTo(action.y + action.height, 0);
      expect(await input.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(
        true,
      );
    });
  }
  for (const theme of THEMES) {
    for (const width of [320, 390]) {
      test(`policy labels and lookup fit ${width}px (${locale}, ${theme})`, async ({
        page,
      }, info) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 800 });
        await visit(page, locale);
        await page.evaluate(() => document.fonts.ready);
        const tabs = page.locator(`${PAGE} hlm-tabs-list [role="tab"]`);
        await expect(tabs).toHaveCount(3);
        for (const [index, label] of LABELS[locale].entries()) {
          const tab = tabs.nth(index);
          await expect(tab).toHaveText(label.visible);
          await expect(tab).toHaveAccessibleName(label.accessible);
          await expect(tab).toHaveCSS('white-space', 'nowrap');
          const geometry = await labelGeometry(tab);
          expect(geometry.lines).toBe(1);
          expect(geometry.fits).toBe(true);
          expect(geometry.height).toBeGreaterThanOrEqual(44);
        }
        const input = page.locator('#policy-revision-id');
        await expect(input).toHaveValue(HEAD_ID);
        const field = await input.boundingBox();
        const form = await page.locator(`${PAGE} > form`).boundingBox();
        if (!field || !form) throw new Error('Expected the policy lookup form');
        expect(field.width).toBeCloseTo(form.width, 0);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
        await page
          .locator(`${PAGE} hlm-tabs-list`)
          .screenshot({ path: info.outputPath('tabs.png') });
        await page.locator(`${PAGE} > form`).screenshot({ path: info.outputPath('lookup.png') });
      });
    }
  }
}
