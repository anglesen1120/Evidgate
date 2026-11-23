import { mkdir } from 'node:fs/promises';
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
    for (const width of [1440, 900, 390]) {
      test(`organization dashboard ${locale}/${theme}/${width}`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1100 });
        await seedStoredTheme(page, theme);
        await page.goto(`${localePath(locale, `/orgs/${ORG_ACME}/overview`)}?${variants}`);
        const main = page.locator('#main-content');
        await expect(main.getByRole('heading', { level: 1 })).toHaveText(
          locale === 'vi' ? 'Tổng quan tổ chức' : 'Organization overview',
        );
        await expect(main.locator('app-dashboard-activity')).toBeVisible();
        await expect(main.locator('app-loading-pattern')).toHaveCount(0);
        await expect(main.locator('app-data-table')).toHaveCount(2);
        const metrics = await page.evaluate(() => ({
          width: document.documentElement.clientWidth,
          scroll: document.documentElement.scrollWidth,
        }));
        expect(metrics.scroll).toBeLessThanOrEqual(metrics.width);
        await expectNoA11yViolations(page, `org overview ${locale}/${theme}/${width}`);
        await mkdir('/tmp/s674-shots', { recursive: true });
        await page.screenshot({
          path: `/tmp/s674-shots/org-${width}-${theme}-${locale}.png`,
          fullPage: true,
        });
        const projectLink = main.locator('a[href*="/projects/"][href$="/overview"]').first();
        await expect(projectLink).toBeVisible();
        await expect(projectLink).toHaveAccessibleName(
          locale === 'vi' ? /Xem dự án / : /View project /,
        );
        for (const action of [
          projectLink,
          main.locator('app-dashboard-activity button[aria-haspopup="dialog"]').first(),
        ]) {
          expect(
            await action.evaluate((element) => {
              const text = document.createRange();
              text.selectNodeContents(element);
              return text.getClientRects().length;
            }),
          ).toBe(1);
        }
      });
    }
  }
}
