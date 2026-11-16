import { expect, test, type Locator } from '@playwright/test';
import { MEMBER_ROWS, openMemberMenu } from './member-controls';
import {
  LOCALES,
  ORG_ACME,
  SETTINGS_ADMIN_GRANTS,
  SETTINGS_AUDIT_ONLY_GRANTS,
  membersPath,
  seedStoredTheme,
  settingsTabPath,
  withVariants,
} from './support';

async function comparisonBudgets(matrix: Locator) {
  return matrix.evaluate((table) => {
    const headers = [
      ...table.querySelectorAll<HTMLTableCellElement>('thead th[data-column-role="status"]'),
    ];
    return headers.map((header) => {
      const cells = [...table.querySelectorAll<HTMLTableRowElement>('tbody tr')]
        .map((row) => row.cells[header.cellIndex])
        .filter((cell): cell is HTMLTableCellElement => !!cell);
      const contents = [
        header,
        ...cells.map((cell) => cell.querySelector('span:not(.record-label)')),
      ];
      const textWidth = Math.max(
        ...contents.map((content) => {
          if (!content) return 0;
          const range = document.createRange();
          range.selectNodeContents(content);
          return range.getBoundingClientRect().width;
        }),
      );
      const insets = Math.max(
        ...[header, ...cells].map((cell) => {
          const style = getComputedStyle(cell);
          return (
            parseFloat(style.paddingLeft) +
            parseFloat(style.paddingRight) +
            parseFloat(style.borderLeftWidth) +
            parseFloat(style.borderRightWidth)
          );
        }),
      );
      return {
        label: header.textContent,
        width: header.getBoundingClientRect().width,
        textWidth,
        insets,
      };
    });
  });
}

for (const locale of LOCALES) {
  test(`audit CSV stays intrinsic on both routes (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await seedStoredTheme(page, 'dark');
    for (const path of [`/${locale}/orgs/${ORG_ACME}/audit`, settingsTabPath(locale, 'audit')]) {
      await page.goto(withVariants(path, [SETTINGS_AUDIT_ONLY_GRANTS]));
      const action = page.locator('app-audit-export [data-audit-export]');
      await expect(action).toBeVisible();
      await page.evaluate(() => document.fonts.ready);
      const size = await action.evaluate((button) => {
        const bounds = button.getBoundingClientRect();
        if (!button.parentElement) throw new Error('Expected the export action grid');
        const parent = button.parentElement.getBoundingClientRect();
        const range = document.createRange();
        range.selectNodeContents(button);
        const text = range.getBoundingClientRect();
        const style = getComputedStyle(button);
        const insets =
          parseFloat(style.paddingLeft) +
          parseFloat(style.paddingRight) +
          parseFloat(style.borderLeftWidth) +
          parseFloat(style.borderRightWidth);
        return {
          width: bounds.width,
          left: bounds.left,
          parentLeft: parent.left,
          content: text.width + insets,
          textHeight: text.height,
          lineHeight: parseFloat(style.lineHeight),
        };
      });
      expect(size.width).toBeLessThanOrEqual(size.content + 2);
      expect(size.textHeight).toBeLessThanOrEqual(size.lineHeight + 1);
      expect(Math.abs(size.left - size.parentLeft)).toBeLessThanOrEqual(1);
    }
  });

  test(`member menu labels stay whole and start aligned (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await seedStoredTheme(page, 'dark');
    await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
    const row = page
      .locator(MEMBER_ROWS)
      .filter({ has: page.locator('[data-members-actions]') })
      .first();
    await openMemberMenu(page, row);
    const menu = page.getByRole('menu');
    await expect(menu).toBeVisible();
    await menu.evaluate(async (node) => {
      await Promise.all(
        node.getAnimations({ subtree: true }).map((animation) => animation.finished),
      );
    });
    for (const item of await menu.getByRole('menuitem').all()) {
      await expect(item).toHaveCSS('justify-content', 'flex-start');
      await expect(item).toHaveCSS('text-align', 'start');
      await expect(item).toHaveCSS('white-space', 'nowrap');
      const contentFits = await item.evaluate((node) => {
        const range = document.createRange();
        range.selectNodeContents(node);
        const style = getComputedStyle(node);
        return (
          range.getBoundingClientRect().width <=
          node.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight) + 1
        );
      });
      expect(contentFits).toBe(true);
    }
    await page.keyboard.press('Escape');
    await expect(menu).not.toBeVisible();
  });

  test(`role matrix budgets stay intrinsic and responsive (${locale})`, async ({ page }) => {
    await seedStoredTheme(page, 'dark');
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
    const matrix = page.locator('[data-members-matrix]');
    await expect(matrix).toBeVisible();
    await page.evaluate(() => document.fonts.ready);
    for (const width of [1440, 1024, 768]) {
      await page.setViewportSize({ width, height: 1000 });
      const budgets = await comparisonBudgets(matrix);
      expect(budgets.length).toBeGreaterThan(0);
      for (const budget of budgets)
        expect(budget.width, budget.label ?? 'Role').toBeLessThanOrEqual(
          budget.textWidth + budget.insets + 2,
        );
    }
    await page.setViewportSize({ width: 390, height: 1000 });
    const expand = page.locator('[aria-controls="members-full-matrix"]');
    await expect(page.locator('[data-members-granted-summary]')).toBeVisible();
    await expand.click();
    await expect(expand).toHaveAttribute('aria-expanded', 'true');
    for (const cell of await matrix.locator('tbody td').all()) {
      await expect(cell).toHaveCSS('display', 'grid');
      await expect(cell.locator('.record-label')).toBeVisible();
      expect(await cell.evaluate((node) => node.style.getPropertyValue('inline-size'))).toBe('');
    }
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= document.documentElement.clientWidth,
      ),
    ).toBe(true);
  });
}
