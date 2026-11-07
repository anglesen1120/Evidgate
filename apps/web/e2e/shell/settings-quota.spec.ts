import { expect, test, type Page } from '@playwright/test';
import {
  LOCALES,
  ORG_ACME,
  SETTINGS_ADMIN_USAGE_GRANTS,
  THEMES,
  expectNoA11yViolations,
  localePath,
  seedStoredTheme,
  withVariants,
  type ShellLocale,
} from './support';

const PANEL = 'app-quota-panel';
const BADGE = '[data-quota-badge]';

function path(locale: ShellLocale, variant: string, tab = true): string {
  const route = `/orgs/${ORG_ACME}${tab ? '/settings/quota-retention' : '/usage'}`;
  return withVariants(localePath(locale, route), [
    SETTINGS_ADMIN_USAGE_GRANTS,
    `?fixture.variant.getUsageSummary=${variant}`,
  ]);
}

async function ready(page: Page): Promise<void> {
  await expect(page.locator(PANEL)).toHaveAttribute('data-quota-state', 'ready');
}

for (const locale of LOCALES) {
  test.describe(`Quota and retention (${locale})`, () => {
    test('renders supplied counters and unavailable storage without fabricated zeroes', async ({
      page,
    }) => {
      await page.goto(path(locale, 'warn'));
      await ready(page);
      const results = page.locator('[data-quota-metric="results"]');
      await expect(results.locator('[data-quota-used]')).toHaveText('800');
      await expect(results.locator('[data-quota-limit]')).toHaveText(/1[,.\s\u00a0]?000/);
      const unavailable = locale === 'vi' ? 'Không khả dụng' : 'Unavailable';
      await page.goto(path(locale, 'unavailable'));
      await ready(page);
      const egress = page.locator('[data-quota-metric="egress_gb"]');
      await expect(egress.locator('[data-quota-used]')).toHaveText(unavailable);
      await expect(egress.locator('[data-quota-used]')).not.toContainText('0');
      await expect(page.locator('[data-quota-storage] dd')).toHaveText([unavailable, unavailable]);
    });

    for (const severity of ['warn', 'blocked'] as const) {
      test(`${severity} badge is keyboard-focusable and links to the quota tab`, async ({
        page,
      }) => {
        await page.goto(path(locale, severity, false));
        const badge = page.locator(BADGE);
        await expect(badge).toHaveAttribute('data-quota-severity', severity);
        await expect(badge).toContainText(locale === 'vi' ? 'Kết quả' : 'Results');
        await expect(badge).toHaveAttribute(
          'href',
          new RegExp(`/orgs/${ORG_ACME}/settings/quota-retention$`),
        );
        await badge.focus();
        await expect(badge).toBeFocused();
        await page.keyboard.press('Enter');
        await expect(page).toHaveURL(new RegExp('/settings/quota-retention$'));
        await ready(page);
      });
    }

    test('does not render a badge on an unavailable summary and exposes retry', async ({
      page,
    }) => {
      await page.goto(path(locale, 'error'));
      await expect(page.locator(PANEL)).toHaveAttribute('data-quota-state', 'unavailable');
      await expect(page.locator(BADGE)).toHaveCount(0);
      await expect(page.locator('[data-quota-unavailable] button')).toBeEnabled();
    });

    test('shows a distinct empty state without a badge', async ({ page }) => {
      await page.goto(path(locale, 'empty'));
      await ready(page);
      await expect(page.locator('[data-quota-empty]')).toBeVisible();
      await expect(page.locator(BADGE)).toHaveCount(0);
    });

    for (const theme of THEMES) {
      test(`wraps at 320px in ${theme} and retains accessible quota status`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width: 320, height: 800 });
        await page.goto(path(locale, 'blocked'));
        await ready(page);
        const badge = page.locator(BADGE);
        const bounds = await badge.boundingBox();
        expect(bounds?.height).toBeGreaterThanOrEqual(44);
        expect(bounds?.width).toBeGreaterThanOrEqual(44);
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
        ).toBe(true);
        await expectNoA11yViolations(page, `quota ${locale} ${theme} 320px`);
      });
    }
  });
}
