import { expect, test } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
} from './support';

const ROWS = 'app-case-list-table [data-row-id]';
for (const locale of LOCALES) {
  const path = localePath(
    locale,
    `/dev/catalog-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`,
  );
  const base = withVariants(path, ['?fixture.variant.listPermissions=settings-policy-readonly']);
  const labels =
    locale === 'vi'
      ? { active: 'Đang hoạt động', all: 'Mọi trạng thái' }
      : { active: 'Active', all: 'All statuses' };

  test(`catalog filters survive reload and browser back/forward (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(withVariants(base, ['?cursor=opaque-catalog&limit=25']));
    await expect(page.locator(ROWS)).toHaveCount(3);
    await page.locator('#catalog-search').fill('checkout');
    await page.locator('[data-catalog-apply]').click();
    await expect(page).toHaveURL(/q=checkout/);
    expect(new URL(page.url()).searchParams.get('cursor')).toBe('opaque-catalog');
    expect(new URL(page.url()).searchParams.get('limit')).toBe('25');
    await expect(page.locator(ROWS)).toHaveCount(1);
    await page.locator('#catalog-status').click();
    await page.getByRole('option', { name: labels.active, exact: true }).click();
    await page.locator('#catalog-framework').fill('playwright');
    await page.locator('#catalog-search').fill('checkout');
    await page.locator('[data-catalog-apply]').click();
    await expect(page).toHaveURL(/status=active/);
    expect(new URL(page.url()).searchParams.get('framework')).toBe('playwright');
    expect(new URL(page.url()).searchParams.get('q')).toBe('checkout');
    expect(new URL(page.url()).searchParams.has('cursor')).toBe(false);
    expect(new URL(page.url()).searchParams.get('limit')).toBe('25');
    await expect(page.locator(ROWS)).toHaveCount(1);
    await page.reload();
    await expect(page.locator('#catalog-status')).toContainText(labels.active);
    await expect(page.locator('#catalog-framework')).toHaveValue('playwright');
    await expect(page.locator('#catalog-search')).toHaveValue('checkout');
    await expect(page.locator(ROWS)).toHaveCount(1);
    await page.locator('[data-catalog-clear]').click();
    await expect(page.locator(ROWS)).toHaveCount(3);
    expect(new URL(page.url()).searchParams.has('q')).toBe(false);
    await page.goBack();
    await expect(page.locator('#catalog-search')).toHaveValue('checkout');
    await expect(page.locator('#catalog-status')).toContainText(labels.active);
    await expect(page.locator(ROWS)).toHaveCount(1);
    await page.goForward();
    await expect(page.locator('#catalog-search')).toHaveValue('');
    await expect(page.locator('#catalog-status')).toContainText(labels.all);
    await expect(page.locator(ROWS)).toHaveCount(3);
  });

  for (const theme of THEMES)
    for (const width of [360, 1280]) {
      test(`catalog reflows, keeps keyboard controls and passes axe (${locale}, ${theme}, ${width})`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base);
        await expect(page.locator('app-case-list-page')).toHaveAttribute(
          'data-catalog-status',
          'ready',
        );
        await expect(page.locator(ROWS)).toHaveCount(3);
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
        ).toBe(true);
        await page.locator('#catalog-search').focus();
        await page.keyboard.press('Tab');
        await expect(page.locator('[data-catalog-apply]')).toBeFocused();
        await expectNoA11yViolations(page, 'app-case-list-page');
      });
    }

  test(`catalog distinguishes empty, no-match, service error and forbidden (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariants(base, ['?fixture.variant.listCases=empty']));
    await expect(page.locator('app-empty-pattern')).toBeVisible();
    await expect(page.locator('[data-catalog-more]')).toHaveCount(0);
    await page.goto(withVariants(base, ['?q=no-such-identity']));
    await expect(page.locator('app-empty-pattern')).toBeVisible();
    await expect(page.locator('[data-catalog-more]')).toBeVisible();
    await page.goto(withVariants(base, ['?fixture.variant.listCases=unavailable']));
    await expect(page.locator('app-retry-after')).toBeVisible();
    await expect(page.locator('app-case-list-table')).toHaveCount(0);
    await page.goto(withVariants(base, ['?fixture.variant.listCases=forbidden']));
    await expect(page.locator('app-forbidden-pattern')).toBeVisible();
    await expect(page.locator('app-case-list-table')).toHaveCount(0);
    await expect(page.locator('app-case-list-page')).not.toContainText('case-checkout-17');
  });
}
