import { expect, test, type Locator } from '@playwright/test';
import { latencyToggle } from './onboarding-support';
import { RUN_RECORDS } from './run-list.support';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
} from './support';

async function documentBox(locator: Locator) {
  return locator.evaluate((element) => {
    const box = element.getBoundingClientRect();
    return {
      x: box.x + window.scrollX,
      y: box.y + window.scrollY,
      width: box.width,
      height: box.height,
    };
  });
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const width of [390, 768, 1440]) {
      test(`runs ${locale} ${theme} ${width}px: size refetch preserves subtitle, first row and pager geometry`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 900 });
        await page.goto(
          withVariants(
            localePath(locale, `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
            [
              '?fixture.variant.listPermissions=settings-policy-readonly',
              '?fixture.variant.listRuns=paging-33',
              `?${latencyToggle(1000)}`,
            ],
          ),
        );
        const host = page.locator('app-run-list-page');
        const rows = page.locator(RUN_RECORDS);
        await expect(host).toHaveAttribute('data-run-list-status', 'ready', { timeout: 20_000 });
        await expect(rows).toHaveCount(20);
        await page.evaluate(() => document.fonts.ready);
        const subtitle = host.locator('app-page-header .header-description');
        const pager = host.locator('app-data-table-pager');
        const size = pager.getByRole('combobox');
        await size.scrollIntoViewIfNeeded();
        await expect(size).toContainText('20');
        const targets = [subtitle, rows.first(), pager];
        const before = await Promise.all(targets.map(documentBox));
        const text = await subtitle.textContent();
        const firstId = await rows.first().getAttribute('data-row-id');

        await size.click();
        await page.getByRole('option', { name: '50', exact: true }).click();
        await expect(host).toHaveAttribute('data-run-list-status', 'loading');
        await expect(pager.getByRole('progressbar')).toBeVisible();
        await expect(subtitle).toHaveText(text ?? '');
        await expect(rows).toHaveCount(20);
        await expect(rows.first()).toHaveAttribute('data-row-id', firstId ?? '');
        const pending = await Promise.all(targets.map(documentBox));
        for (const [index, initial] of before.entries()) {
          const current = pending[index];
          if (!current) throw new Error('A retained layout target disappeared');
          for (const key of ['x', 'y', 'width', 'height'] as const) {
            expect(
              Math.abs(current[key] - initial[key]),
              `target ${index} ${key}`,
            ).toBeLessThanOrEqual(1);
          }
        }
        await expect(host).toHaveAttribute('data-run-list-status', 'loading');
        await expect(rows).toHaveCount(33);
        await expect(host).toHaveAttribute('data-run-list-status', 'ready');
        await expect(size).toContainText('50');
      });
    }
  }
}
