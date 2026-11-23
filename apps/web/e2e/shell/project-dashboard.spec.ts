import { mkdir } from 'node:fs/promises';
import { test, expect } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { LOCALES, THEMES, overviewPath, seedStoredTheme, waitForDialogAnimations } from './support';

const FIXTURE = '?fixture.variant.listPermissions=dashboard';
const WIDGETS =
  '[data-dashboard-kpis], [data-dashboard-trend], [data-dashboard-recent], [data-dashboard-attention], [data-dashboard-authoring], [data-dashboard-ci], [data-dashboard-usage], [data-dashboard-activity]';

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const width of [1440, 900, 390]) {
      test(`project dashboard ${locale} ${theme} ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 });
        await seedStoredTheme(page, theme);
        await page.goto(overviewPath(locale) + FIXTURE);
        await expect(page.locator('app-project-overview')).toBeVisible();
        await expect(page.locator('app-project-overview [aria-busy="true"]')).toHaveCount(0);
        await expect(page.locator(WIDGETS)).toHaveCount(8);
        await expect(page.locator('[data-dashboard-run-view]')).toHaveCount(5);
        await expect(page.locator('app-project-overview h1')).toHaveCount(1);
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
        ).toBe(true);
        await expectNoA11yViolations(page, `project dashboard ${locale} ${theme} ${width}`);
        await mkdir('/tmp/s674-shots', { recursive: true });
        await page.screenshot({
          path: `/tmp/s674-shots/project-${width}-${theme}-${locale}.png`,
          fullPage: true,
        });
      });
    }
  }
}

test('one failed source leaves other dashboard facts visible and retries only locally', async ({
  page,
}) => {
  await page.goto(
    overviewPath('en') + FIXTURE + '&fixture.variant.listProjectWaivers=dashboard-error',
  );
  await expect(page.locator('[data-dashboard-attention] app-error-pattern')).toBeVisible();
  await expect(page.locator('[data-dashboard-run-view]')).toHaveCount(5);
  await page.locator('[data-dashboard-attention] app-error-pattern button').click();
  await expect(page.locator('[data-dashboard-attention] app-error-pattern')).toBeVisible();
  await expect(page.locator('[data-dashboard-run-view]')).toHaveCount(5);
  await expect(page.locator('[data-dashboard-usage] meter')).toHaveCount(1);
});

test('successful empty runs remain empty rather than a green decision', async ({ page }) => {
  await page.goto(overviewPath('en') + FIXTURE + '&fixture.variant.listRuns=dashboard-empty');
  await expect(page.locator('[data-dashboard-recent] app-empty-pattern')).toBeVisible();
  await expect(page.locator('[data-dashboard-trend] app-empty-pattern')).toBeVisible();
  await expect(page.locator('[data-dashboard-kpis]')).toContainText('No data');
  await expect(page.locator('[data-dashboard-run-view]')).toHaveCount(0);
});

test('restricted widgets are hidden instead of rendering forbidden cards', async ({ page }) => {
  await page.goto(overviewPath('en') + '?fixture.variant.listPermissions=dashboard-limited');
  await expect(page.locator('[data-dashboard-ci]')).toBeVisible();
  await expect(
    page.locator(
      '[data-dashboard-kpis], [data-dashboard-usage], [data-dashboard-authoring], [data-dashboard-activity], [data-dashboard-attention]',
    ),
  ).toHaveCount(0);
  await expect(page.locator('app-project-overview app-error-pattern')).toHaveCount(0);
});

test('run inspection is a named read-only dialog with Escape and focus return', async ({
  page,
}) => {
  await page.goto(overviewPath('en') + FIXTURE);
  const view = page.locator('[data-dashboard-run-view]').first();
  await view.click();
  const dialog = page.locator('[data-dashboard-run-dialog]');
  await expect(dialog).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveAccessibleName(/\S/);
  await waitForDialogAnimations(page, '[data-dashboard-run-dialog]');
  await expectNoA11yViolations(page, 'project run inspection');
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(view).toBeFocused();
});
