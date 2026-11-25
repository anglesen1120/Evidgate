import { expect, test } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  authoringTreePath,
  IDS,
  LEAF,
  PAGE,
  readOnlyTreePath,
  ROOT,
  TREE,
} from './authoring-tree.helpers';
import { LOCALES, seedStoredTheme, THEMES } from './support';

for (const locale of LOCALES) {
  const base = readOnlyTreePath(locale);
  const labels =
    locale === 'vi'
      ? {
          title: 'Cây chức năng',
          moreRoots: 'Tải thêm nút gốc',
          moreChildren: 'Tải thêm nút con',
          pending: 'Chưa có số thay đổi đang chờ',
        }
      : {
          title: 'Feature tree',
          moreRoots: 'Load more root nodes',
          moreChildren: 'Load more child nodes',
          pending: 'Pending change count unavailable',
        };

  for (const theme of THEMES)
    for (const width of [360, 1280]) {
      test(`tree counts, deep wrapped titles, scoped paging and axe (${locale}, ${theme}, ${width})`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base);
        await expect(page.locator(`${PAGE} h1`)).toHaveText(labels.title);
        const root = page.locator(`[data-node-id="${ROOT}"]`);
        await expect(root.locator('dl')).toHaveCount(0);
        const details = root.locator('[data-node-details]');
        await root.locator('[data-scope]').focus();
        await page.keyboard.press('Tab');
        await expect(details).toBeFocused();
        await page.keyboard.press('Enter');
        const dialog = page.getByRole('dialog');
        await expect(dialog.getByRole('heading')).toBeFocused();
        await expect(dialog.locator('dd').nth(0)).toHaveText('2');
        await expect(dialog.locator('dd').nth(1)).toHaveText('2');
        await expect(dialog.locator('dd').nth(2)).toContainText(labels.pending);
        await expect(dialog.locator('app-copyable-identifier code')).toHaveText(ROOT);
        await page.keyboard.press('Escape');
        await expect(dialog).toHaveCount(0);
        await expect(details).toBeFocused();
        const disclosure = await root.locator('[data-disclosure]').boundingBox();
        if (!disclosure) throw new Error('The root disclosure is not visible.');
        expect(Math.min(disclosure.width, disclosure.height)).toBeGreaterThanOrEqual(24);
        await page.getByRole('button', { name: labels.moreRoots, exact: true }).click();
        await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(3);
        for (let level = 0; level < 6; level++) {
          await page.locator(`[data-disclosure="${IDS[level]}"]`).click();
          await expect(page.locator(`[data-node-id="${IDS[level + 1]}"]`)).toBeVisible();
        }
        await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(9);
        await page.getByRole('button', { name: labels.moreChildren, exact: true }).click();
        await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(10);
        const depth4 = await page.locator(`[data-node-id="${IDS[3]}"]`).boundingBox();
        const depth7 = await page.locator(`[data-node-id="${LEAF}"]`).boundingBox();
        if (!depth4 || !depth7) throw new Error('The deep hierarchy is not visible.');
        expect(Math.abs(depth4.x - depth7.x)).toBeLessThanOrEqual(1);
        const deepRow = page.locator(`[data-node-id="${IDS[3]}"]`);
        const scope = deepRow.locator('[data-scope]');
        const title = scope.locator('bdi');
        const fullTitle = await title.innerText();
        const titleBox = await title.boundingBox();
        const scopeBox = await scope.boundingBox();
        const info = deepRow.locator('[data-node-details]');
        const infoBox = await info.boundingBox();
        if (!scopeBox || !titleBox || !infoBox) throw new Error('Missing deep node controls.');
        expect(scopeBox.height).toBeGreaterThanOrEqual(24);
        expect(titleBox.width).toBeGreaterThanOrEqual(64);
        // Compact controls let a long title fit one line at 1280; it may still wrap, but never past two.
        const lineHeight = await title.evaluate((el) =>
          parseFloat(getComputedStyle(el).lineHeight),
        );
        expect(titleBox.height).toBeGreaterThanOrEqual(lineHeight - 1);
        expect(titleBox.height).toBeLessThanOrEqual(2 * lineHeight + 1);
        expect(Math.min(infoBox.width, infoBox.height)).toBeGreaterThanOrEqual(24);
        await info.click();
        await expect(dialog.getByText(fullTitle, { exact: true })).toBeVisible();
        await page.keyboard.press('Escape');
        await expect(info).toBeFocused();
        await expect(page.locator('[data-authoring-cases]')).toBeVisible();
        expect(await page.locator('[data-authoring-cases]').getAttribute('aria-busy')).toBeNull();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
        await expectNoA11yViolations(page, PAGE);
      });
    }
}

test.describe('authoring tree coarse pointer targets', () => {
  test.use({ hasTouch: true });
  test('keeps scope and disclosure targets at least 44px on touch devices', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 900 });
    await page.goto(authoringTreePath('vi'));
    for (const selector of [`[data-disclosure="${ROOT}"]`, `[data-scope="${ROOT}"]`]) {
      const box = await page.locator(selector).boundingBox();
      if (!box) throw new Error('The node control is not visible.');
      expect(box.width).toBeGreaterThanOrEqual(44);
      expect(box.height).toBeGreaterThanOrEqual(44);
    }
  });
});
