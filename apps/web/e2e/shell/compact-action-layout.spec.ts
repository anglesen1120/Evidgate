import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectCurrentStep, onboardingUrl } from './onboarding-support';
import { runDetailPath } from './run-detail.support';
import {
  LOCALES,
  ORG_ACME,
  SETTINGS_ADMIN_GRANTS,
  THEMES,
  localePath,
  seedStoredTheme,
  withVariants,
} from './support';

async function expectSingleLineActions(page: Page, buttons: Locator): Promise<void> {
  await expect(buttons.first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  for (const width of [390, 768, 900, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(buttons.first()).toBeVisible();
    for (const button of await buttons.all()) {
      // A narrow table may scroll locally: its complete action must be reachable in the scroller.
      await button.scrollIntoViewIfNeeded();
      const layout = await button.evaluate((element) => {
        const range = document.createRange();
        range.selectNodeContents(element);
        const lines = Array.from(range.getClientRects()).filter((rect) => rect.width > 0);
        const bounds = element.getBoundingClientRect();
        const cell = element.closest('td, dd')?.getBoundingClientRect();
        const scroller = element.closest('.spartan-table-container')?.getBoundingClientRect();
        const left = Math.max(cell?.left ?? 0, scroller?.left ?? 0, 0);
        const right = Math.min(
          cell?.right ?? innerWidth,
          scroller?.right ?? innerWidth,
          innerWidth,
        );
        return {
          lines: lines.length,
          textFits: lines.every(
            (line) => line.left >= bounds.left - 1 && line.right <= bounds.right + 1,
          ),
          reachable: bounds.left >= left - 1 && bounds.right <= right + 1,
        };
      });
      expect(layout, `short action at ${width}px`).toEqual({
        lines: 1,
        textFits: true,
        reachable: true,
      });
    }
  }
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test.describe(`Short actions (${locale}, ${theme})`, () => {
      test.beforeEach(async ({ page }) => seedStoredTheme(page, theme));

      test('quota state View labels fit on one line', async ({ page }) => {
        await page.goto(
          withVariants(localePath(locale, `/orgs/${ORG_ACME}/settings/quota-retention`), [
            SETTINGS_ADMIN_GRANTS,
            '?fixture.variant.getUsageSummary=warn',
          ]),
        );
        await expectSingleLineActions(page, page.locator('[data-quota-view]'));
      });

      test('onboarding resource and credential View labels fit on one line', async ({ page }) => {
        await page.goto(onboardingUrl(locale, 'onboarding-token'));
        await expectCurrentStep(page, 3);
        await page.getByTestId('onboarding-back').click();
        await expectCurrentStep(page, 2);
        await expect(page.getByTestId('connect-token-row')).toBeVisible();
        await expect(page.getByTestId('onboarding-evidence-panel')).toBeVisible();
        await expectSingleLineActions(page, page.locator('button[data-onboarding-record]:visible'));
      });

      test('run ID yields space to its one-line Copy label', async ({ page }) => {
        await page.goto(runDetailPath({ locale }));
        const copy = page.locator('#run-summary-title + app-copy-value-button button');
        await expect(copy).toHaveText(locale === 'vi' ? 'Sao chép' : 'Copy');
        await expectSingleLineActions(page, copy);
        await page.setViewportSize({ width: 390, height: 900 });
        const idEnd = await page
          .locator('#run-summary-title bdi')
          .evaluate((id) => id.getBoundingClientRect().right);
        const copyStart = await copy.evaluate((button) => button.getBoundingClientRect().left);
        expect(idEnd).toBeLessThanOrEqual(copyStart);
      });
    });
  }
}
