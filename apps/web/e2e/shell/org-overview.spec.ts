import { expect, test } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { LOCALES, ORG_ACME, THEMES, localePath, seedStoredTheme } from './support';

const variants = [
  'listPermissions',
  'listProjects',
  'listRuns',
  'listMemberships',
  'getUsageSummary',
  'listAudit',
]
  .map((operation) => `fixture.variant.${operation}=dashboard`)
  .join('&');

test.use({ bypassCSP: true });

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const width of [1440, 900, 768, 390]) {
      test(`organization dashboard ${locale}/${theme}/${width}`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 1100 });
        await seedStoredTheme(page, theme);
        await page.goto(`${localePath(locale, `/orgs/${ORG_ACME}/overview`)}?${variants}`);
        const main = page.locator('#main-content');
        await expect(main.getByRole('heading', { level: 1 })).toHaveText(
          locale === 'vi' ? 'Tổng quan tổ chức' : 'Organization overview',
        );
        await expect(main.locator('app-dashboard-activity')).toBeVisible();
        // An idle table still hosts its loading pattern, so check the in-flight signal instead.
        await expect(main.locator('[aria-busy="true"]')).toHaveCount(0);
        await expect(main.locator('[data-table-progress]')).toHaveCount(0);
        await expect(main.locator('app-data-table')).toHaveCount(2);
        const metrics = await page.evaluate(() => ({
          width: document.documentElement.clientWidth,
          scroll: document.documentElement.scrollWidth,
        }));
        expect(metrics.scroll).toBeLessThanOrEqual(metrics.width);
        await expectNoA11yViolations(page, `org overview ${locale}/${theme}/${width}`);
        await page.screenshot({
          path: testInfo.outputPath(`org-after-${width}-${theme}-${locale}.png`),
          fullPage: true,
        });
        const projectAction = main.locator('.projects button[appRowAction]').first();
        await expect(projectAction).toBeVisible();
        await expect(projectAction).toHaveAccessibleName(
          locale === 'vi' ? /Xem dự án / : /View project /,
        );
        for (const action of [
          projectAction,
          main.locator('app-dashboard-activity button[aria-haspopup="dialog"]').first(),
        ]) {
          const box = await action.boundingBox();
          expect(box?.width).toBeGreaterThanOrEqual(24);
          expect(box?.height).toBeGreaterThanOrEqual(24);
          await expect(action.locator('hlm-icon')).toBeVisible();
          await action.focus();
          await expect(page.getByRole('tooltip')).toHaveText(
            (await action.getAttribute('aria-label')) ?? '',
          );
          await action.press('Escape');
        }
        await projectAction.click();
        const dialog = page.getByRole('dialog');
        await expect(dialog).toHaveAccessibleName(
          locale === 'vi' ? 'Chi tiết dự án' : 'Project details',
        );
        await expect(
          dialog.getByRole('link', { name: locale === 'vi' ? 'Mở dự án' : 'Open project' }),
        ).toHaveAttribute('href', /\/projects\/[^/]+\/overview$/);
        await dialog.press('Escape');
        await expect(dialog).toBeHidden();
        await expect(projectAction).toBeFocused();
      });
    }
  }
}
