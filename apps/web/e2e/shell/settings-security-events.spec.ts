import { expect, test, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  THEMES,
  localePath,
  seedStoredTheme,
  withVariants,
} from './support';

const grants = '?fixture.variant.listPermissions=settings-admin';
const host = 'app-security-events-page';
const rows = `${host} [data-row-id]`;
const path = (locale: 'en' | 'vi', variants: string[] = []) =>
  withVariants(localePath(locale, `/dev/settings-security-events/orgs/${ORG_ACME}`), [
    grants,
    ...variants,
  ]);
async function ready(page: Page): Promise<void> {
  await expect(page.locator(host)).toHaveAttribute('data-security-phase', 'ready');
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const width of [360, 1280]) {
      test(`security metadata ${locale} ${theme} ${width}px is readable, keyboard reachable and axe clean`, async ({
        page,
      }) => {
        await page.setViewportSize({ width, height: 900 });
        await seedStoredTheme(page, theme);
        await page.goto(path(locale));
        await ready(page);
        await expect(page.locator(rows)).toHaveCount(5);
        await expect(page.locator(`${host} h1`)).toHaveCount(1);
        await page.locator('#security-project').focus();
        await page.keyboard.press('Tab');
        await expect(page.locator('#security-type')).toBeFocused();
        await page.locator(`${rows} summary`).last().focus();
        await page.keyboard.press('Enter');
        await expect(page.locator(`${rows} details[open]`)).toHaveCount(1);
        await expect(page.locator(`${rows} details[open]`)).toContainText(
          '018f4350-0000-7000-8000-000000000500',
        );
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
        ).toBe(true);
        await expectNoA11yViolations(page);
        await page.screenshot({
          path: test.info().outputPath(`security-${locale}-${theme}-${width}.png`),
          fullPage: true,
        });
      });
    }
  }
  test(`security cursor and server filters ${locale}`, async ({ page }) => {
    await page.goto(path(locale));
    await ready(page);
    const navigation = page.locator(`${host} nav`);
    await navigation.locator('button').last().click();
    await ready(page);
    await expect(page.locator(rows)).toHaveCount(1);
    await navigation.locator('button').nth(1).click();
    await ready(page);
    await expect(page.locator(rows)).toHaveCount(5);
    await page.locator('#security-project').fill(PROJECT_STOREFRONT);
    await page.locator('#security-type').click();
    await page.getByRole('option').nth(5).click();
    await page.locator('[data-security-apply]').click();
    await ready(page);
    await expect(page.locator(rows)).toHaveCount(1);
    await page.locator('#security-project').fill('018f0000-0000-7000-8000-000000000098');
    await page.locator('[data-security-apply]').click();
    await ready(page);
    await expect(page.locator(`${host} app-empty-pattern`)).toBeVisible();
    await expect(page.locator(rows)).toHaveCount(0);
    await page.locator('[data-security-clear]').click();
    await ready(page);
    await expect(page.locator(rows)).toHaveCount(5);
  });
  for (const [variant, pattern] of [
    ['empty', 'app-empty-pattern'],
    ['forbidden', 'app-forbidden-pattern'],
    ['unavailable', 'app-retry-after'],
  ] as const) {
    test(`security ${variant} state ${locale} is accessible without success rows`, async ({
      page,
    }) => {
      await page.goto(path(locale, [`?fixture.variant.listSecurityEvents=${variant}`]));
      await expect(page.locator(`${host} ${pattern}`)).toBeVisible();
      await expect(page.locator(rows)).toHaveCount(0);
      await expectNoA11yViolations(page);
    });
  }
  test(`security loading state ${locale}`, async ({ page }) => {
    await page.goto(path(locale, ['?fixture.variant.listSecurityEvents=slow']));
    await expect(page.locator(`${host} app-loading-pattern`)).toBeVisible();
    await ready(page);
    await expect(page.locator(rows)).toHaveCount(5);
  });
  test(`security view refuses missing permission ${locale}`, async ({ page }) => {
    await page.goto(
      withVariants(localePath(locale, `/dev/settings-security-events/orgs/${ORG_ACME}`), [
        '?fixture.variant.listPermissions=settings-audit-only',
      ]),
    );
    await expect(page.locator('app-forbidden-pattern')).toBeVisible();
    await expect(page.locator(host)).toHaveCount(0);
  });
}
