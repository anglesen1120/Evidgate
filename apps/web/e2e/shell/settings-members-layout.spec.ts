import { expect, test } from '@playwright/test';
import {
  LOCALES,
  membersPath,
  SETTINGS_ADMIN_GRANTS,
  seedStoredTheme,
  THEMES,
  withVariants,
} from './support';

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test(`member cells contain their roles, actions and status words (${locale}, ${theme})`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: 1440, height: 1000 });
      await seedStoredTheme(page, theme);
      await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
      await expect(page.locator('app-members-page')).toHaveAttribute('data-members-phase', 'ready');
      const rows = page.locator('app-member-list [data-members-row]');
      expect(await rows.count()).toBeGreaterThan(0);
      expect(await rows.locator('.role-name').count()).toBeGreaterThan(1);
      expect(await rows.locator('button').count()).toBeGreaterThan(0);
      for (const row of await rows.all()) {
        for (const item of await row.locator('.role-name, button').all()) {
          const bounds = await item.evaluate((element) => {
            const cell = element.closest('td');
            if (!cell) throw new Error('Member action or role has no table cell');
            const content = element.getBoundingClientRect();
            const parent = cell.getBoundingClientRect();
            return {
              left: content.left,
              right: content.right,
              start: parent.left,
              end: parent.right,
            };
          });
          expect(bounds.left).toBeGreaterThanOrEqual(bounds.start);
          expect(bounds.right).toBeLessThanOrEqual(bounds.end);
        }
      }
      const activeLabels = page.locator(
        'app-member-list [data-members-status="active"] .status-pill__label',
      );
      expect(await activeLabels.count()).toBeGreaterThan(0);
      for (const label of await activeLabels.all()) {
        const line = await label.evaluate((element) => {
          const style = getComputedStyle(element);
          return {
            height: element.getBoundingClientRect().height,
            lineHeight: Number.parseFloat(style.lineHeight),
          };
        });
        expect(line.height).toBeLessThanOrEqual(line.lineHeight + 1);
      }
    });

    test(`narrow member records retain table semantics and accessible labels (${locale}, ${theme})`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: 390, height: 844 });
      await seedStoredTheme(page, theme);
      await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
      await expect(page.locator('app-members-page')).toHaveAttribute('data-members-phase', 'ready');
      for (const host of ['app-member-list', 'app-role-matrix']) {
        const table = page.locator(host).getByRole('table');
        await expect(table).toBeVisible();
        expect(await table.getByRole('rowheader').count()).toBeGreaterThan(0);
        expect(await table.getByRole('cell').count()).toBeGreaterThan(0);
        for (const label of await table.locator('.record-label').all()) {
          await expect(label).toBeVisible();
          await expect(label).not.toHaveAttribute('aria-hidden');
        }
      }
    });
  }
}
