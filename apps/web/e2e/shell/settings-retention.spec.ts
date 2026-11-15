import { expect, test, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { FIXTURE_REQUEST_LOG } from '../../src/app/core/dev-fixtures/fixture-request-log';
import {
  LOCALES,
  ORG_ACME,
  SETTINGS_ADMIN_GRANTS,
  THEMES,
  localePath,
  seedStoredTheme,
  waitForDialogAnimations,
  withVariants,
  type ShellLocale,
} from './support';

const FORM = 'app-retention-form';
const RETENTION_GRANTS = SETTINGS_ADMIN_GRANTS.replace(
  'settings-admin',
  'settings-retention-write',
);

function path(locale: ShellLocale, variant = 'ok', writable = true): string {
  return withVariants(localePath(locale, `/orgs/${ORG_ACME}/settings/quota-retention`), [
    writable ? RETENTION_GRANTS : SETTINGS_ADMIN_GRANTS,
    `?fixture.variant.setRetentionPolicy=${variant}`,
  ]);
}

async function putCount(page: Page): Promise<number> {
  return page.evaluate(
    (key) =>
      (window[key] ?? []).filter((call) => call.method === 'PUT' && /\/retention$/.test(call.url))
        .length,
    FIXTURE_REQUEST_LOG,
  );
}

async function tabTo(page: Page, selector: string): Promise<void> {
  const target = page.locator(selector);
  for (let step = 0; step < 80; step += 1) {
    if (await target.evaluate((element) => element === document.activeElement)) return;
    await page.keyboard.press('Tab');
  }
  await expect(target).toBeFocused();
}

async function enterDays(page: Page, field: string, value: string): Promise<void> {
  await tabTo(page, field);
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type(value);
}

for (const locale of LOCALES) {
  test.describe(`Project retention (${locale})`, () => {
    test('keyboard lowering requires confirmation; cancel and Escape retain the unsent draft', async ({
      page,
    }) => {
      await page.goto(path(locale));
      await expect(page.locator('#retention-artifact')).toHaveValue('14');
      await enterDays(page, '#retention-artifact', '7');
      await tabTo(page, '[data-retention-save]');
      await page.keyboard.press('Enter');
      const dialog = page.getByRole('alertdialog');
      await expect(dialog).toBeVisible();
      await expect(dialog.locator('[data-retention-cancel]')).toBeFocused();
      await expect(dialog.locator('[data-retention-impact="artifact_days"]')).toContainText('14');
      await expect(dialog.locator('[data-retention-impact="artifact_days"]')).toContainText('7');
      expect(await putCount(page)).toBe(0);
      // The dialog fades in; axe would otherwise read colours blended by the entrance opacity.
      await waitForDialogAnimations(page, '[role="alertdialog"]');
      await expectNoA11yViolations(page, `retention warning ${locale}`);
      await tabTo(page, '[data-retention-cancel]');
      await page.keyboard.press('Enter');
      await expect(dialog).not.toBeVisible();
      await expect(page.locator('[data-retention-save]')).toBeFocused();
      await expect(page.locator('#retention-artifact')).toHaveValue('7');
      expect(await putCount(page)).toBe(0);
      await page.keyboard.press('Enter');
      await expect(dialog).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(dialog).not.toBeVisible();
      await expect(page.locator('[data-retention-save]')).toBeFocused();
      expect(await putCount(page)).toBe(0);
      await page.keyboard.press('Enter');
      await expect(dialog).toBeVisible();
      await tabTo(page, '[data-retention-confirm]');
      await page.keyboard.press('Enter');
      await expect(page.locator('[data-retention-saved]')).toBeVisible();
      expect(await putCount(page)).toBe(1);
      await expect(page.locator('#retention-artifact')).toHaveValue('7');
    });

    test('raising saves directly without an impact dialog', async ({ page }) => {
      await page.goto(path(locale));
      await expect(page.locator('#retention-artifact')).toHaveValue('14');
      await enterDays(page, '#retention-artifact', '30');
      await tabTo(page, '[data-retention-save]');
      await page.keyboard.press('Enter');
      await expect(page.locator('[data-retention-saved]')).toBeVisible();
      await expect(page.getByRole('dialog')).not.toBeVisible();
      await expect(page.getByRole('alertdialog')).not.toBeVisible();
      expect(await putCount(page)).toBe(1);
    });

    test('422 renders server limits inline while retaining the draft', async ({ page }) => {
      await page.goto(path(locale, 'out_of_range'));
      await expect(page.locator('#retention-artifact')).toHaveValue('14');
      await enterDays(page, '#retention-artifact', '100');
      await enterDays(page, '#retention-metadata', '120');
      await tabTo(page, '[data-retention-save]');
      await page.keyboard.press('Enter');
      await expect(page.locator('[data-retention-error]')).toHaveText(
        'the retention window is outside the configured range',
      );
      await expect(page.locator('#retention-artifact-error')).toContainText(
        'retention_limits.min_artifact_days: 1',
      );
      await expect(page.locator('#retention-artifact')).toHaveAttribute('aria-invalid', 'true');
      await expect(page.locator('#retention-artifact')).toHaveValue('100');
      await expectNoA11yViolations(page, `retention refusal ${locale}`);
    });

    test('read permission alone shows a visible read-only explanation and no save action', async ({
      page,
    }) => {
      await page.goto(path(locale, 'ok', false));
      const facts = page.locator(`${FORM} [data-retention-current]`);
      await expect(facts.locator('dd').nth(0)).toHaveText('14');
      await expect(facts.locator('dd').nth(1)).toHaveText('90');
      await expect(page.locator('#retention-read-only')).toBeVisible();
      await expect(page.locator(`${FORM} input`)).toHaveCount(0);
      expect(await putCount(page)).toBe(0);
      await expect(page.locator('[data-retention-save]')).toHaveCount(0);
    });

    for (const theme of THEMES) {
      test(`retention inspection reflows at 320px and resize closes the warning in ${theme}`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.goto(path(locale));
        await expect(page.locator('#retention-artifact')).toHaveValue('14');
        await expect(page.locator(FORM)).toContainText(
          locale === 'vi' ? 'Lưu giữ dự án' : 'Project retention',
        );
        const size = await page.locator('[data-retention-save]').boundingBox();
        expect(size?.height).toBeGreaterThanOrEqual(44);
        await enterDays(page, '#retention-artifact', '7');
        await tabTo(page, '[data-retention-save]');
        await page.keyboard.press('Enter');
        const dialog = page.getByRole('alertdialog');
        await expect(dialog).toBeVisible();
        await waitForDialogAnimations(page, '[role="alertdialog"]');
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
        ).toBe(true);
        await expectNoA11yViolations(page, `retention dialog ${locale} ${theme} desktop`);

        await page.setViewportSize({ width: 320, height: 800 });
        await expect(dialog).not.toBeVisible();
        await expect(page.locator(`${FORM} input, ${FORM} [data-retention-save]`)).toHaveCount(0);
        await expect(page.locator(`${FORM} app-desktop-required-notice`)).toBeVisible();
        const facts = page.locator(`${FORM} [data-retention-current]`);
        await expect(facts.locator('dd').nth(0)).toHaveText('14');
        await expect(facts.locator('dd').nth(1)).toHaveText('90');
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
        ).toBe(true);
        await expectNoA11yViolations(page, `retention ${locale} ${theme} 320px`);
        expect(await putCount(page)).toBe(0);

        await page.setViewportSize({ width: 1440, height: 900 });
        await expect(page.locator('#retention-artifact')).toHaveValue('7');
        expect(await putCount(page)).toBe(0);
      });
    }
  });
}
