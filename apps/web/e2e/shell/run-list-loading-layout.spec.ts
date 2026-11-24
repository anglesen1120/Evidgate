import { expect, test, type Locator } from '@playwright/test';
import { latencyToggle } from './onboarding-support';
import { openRunFilters, RUN_RECORDS } from './run-list.support';
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
  test(`single-page footer keeps its text-line height during a filter refetch (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 390, height: 900 });
    await page.goto(
      withVariants(
        localePath(locale, `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
        [
          '?fixture.variant.listPermissions=settings-policy-readonly',
          '?fixture.variant.listRuns=runs',
          `?${latencyToggle(1000)}`,
        ],
      ),
    );
    const pager = page.locator('app-data-table-pager');
    const footer = pager.getByRole('presentation');
    const range = pager.locator('.page-range');
    const feedback = pager.locator('[data-page-feedback]');
    await expect(range).toBeVisible();
    await expect(pager.getByRole('navigation')).toHaveCount(0);
    await expect(feedback).toBeHidden();
    await page.evaluate(() => document.fonts.ready);
    const idle = await documentBox(footer);
    expect(idle.height).toBe((await documentBox(range)).height);

    await openRunFilters(page);
    await page.locator('#run-ref').fill('main');
    await page.locator('[data-run-apply]').click();
    await expect(pager.getByRole('progressbar')).toBeVisible();
    expect((await documentBox(footer)).height).toBe(idle.height);
    await expect(feedback).toBeHidden();
    await expect(pager.getByRole('navigation')).toHaveCount(0);
  });

  for (const theme of THEMES) {
    for (const width of [390, 768, 1440]) {
      test(`runs ${locale} ${theme} ${width}px: size refetch preserves metrics, first row and pager geometry`, async ({
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
        const metrics = host.locator('[data-run-metrics]');
        const values = metrics.locator('dd');
        await expect(values).toHaveCount(4);
        const pager = host.locator('app-data-table-pager');
        const size = pager.getByRole('combobox');
        await size.scrollIntoViewIfNeeded();
        await expect(size).toContainText('20');
        const targets = [metrics, rows.first(), pager];
        const before = await Promise.all(targets.map(documentBox));
        const previousValues = await values.allTextContents();
        const firstId = await rows.first().getAttribute('data-row-id');

        await size.click();
        await page.getByRole('option', { name: '50', exact: true }).click();
        await expect(host).toHaveAttribute('data-run-list-status', 'loading');
        await expect(pager.getByRole('progressbar')).toBeVisible();
        await expect(values).toHaveText(previousValues);
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
