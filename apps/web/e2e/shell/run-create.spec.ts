import { expect, test } from '@playwright/test';
import createdFixture from '../../../../fixtures/web-api/run-create/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  THEMES,
  localePath,
  seedStoredTheme,
  withVariants,
  type ShellLocale,
} from './support';

const MANIFEST = '018f0000-0000-7000-8000-000000000041';
function path(locale: ShellLocale, create = 'ok', grant = 'run-actions') {
  return withVariants(
    localePath(locale, `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs`),
    [`?fixture.variant.listPermissions=${grant}`, `?fixture.variant.createRun=${create}`],
  );
}

for (const locale of LOCALES) {
  test(`creates from the list, closes the dialog and opens the returned run (${locale})`, async ({
    page,
  }) => {
    await page.goto(path(locale));
    const trigger = page.locator('[data-run-create-trigger]');
    await trigger.focus();
    await page.keyboard.press('Enter');
    const dialog = page.locator('[data-run-create-dialog]');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('#run-create-manifestId')).toBeFocused();
    await dialog.locator('#run-create-manifestId').fill(MANIFEST);
    await dialog.locator('[data-run-create-submit]').click();
    await expect(dialog).toHaveCount(0);
    await expect(page).toHaveURL(new RegExp(`/runs/${createdFixture.body.run_id}(?:\\?|$)`));
    await expect(page.locator('app-run-detail-page')).toHaveAttribute(
      'data-run-detail-status',
      'ready',
    );
    await expect(page.locator('[data-run-id] > bdi')).toHaveText(createdFixture.body.run_id);
  });

  test(`preserves focus and the draft through a typed key conflict (${locale})`, async ({
    page,
  }) => {
    await page.goto(path(locale, 'conflict'));
    const trigger = page.locator('[data-run-create-trigger]');
    await trigger.click();
    const dialog = page.locator('[data-run-create-dialog]');
    await dialog.locator('#run-create-manifestId').fill(MANIFEST);
    await dialog.locator('[data-run-create-submit]').click();
    await expect(dialog.locator('[data-run-create-error]')).toContainText('idempotency_conflict');
    await expect(dialog.locator('#run-create-manifestId')).toHaveValue(MANIFEST);
    await dialog.locator('[data-run-create-submit]').click();
    await expect(dialog.locator('[data-run-create-error]')).toContainText('idempotency_conflict');
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(trigger).toBeFocused();
  });

  test(`opens the refused field and focuses its validation summary (${locale})`, async ({
    page,
  }) => {
    await page.goto(path(locale, 'invalid'));
    await page.locator('[data-run-create-trigger]').click();
    const dialog = page.locator('[data-run-create-dialog]');
    await dialog.locator('#run-create-manifestId').fill(MANIFEST);
    await dialog.locator('[data-run-create-submit]').click();
    await expect(dialog.locator('#run-create-prNumber')).toBeVisible();
    await expect(dialog.locator('#run-create-prNumber')).toHaveAttribute('aria-invalid', 'true');
    await expect(dialog.locator('#run-create-summary')).toBeFocused();
    await dialog.locator('#run-create-summary a[href="#run-create-prNumber"]').click();
    await expect(dialog.locator('#run-create-prNumber')).toBeFocused();
    await expect(dialog.locator('[data-run-create-error]')).toContainText('/ref/pr_number');
  });

  test(`keeps a submitting intent busy until the server answers (${locale})`, async ({ page }) => {
    await page.goto(path(locale, 'slow'));
    await page.locator('[data-run-create-trigger]').click();
    const dialog = page.locator('[data-run-create-dialog]');
    await dialog.locator('#run-create-manifestId').fill(MANIFEST);
    await dialog.locator('[data-run-create-submit]').click();
    await expect(dialog.locator('form')).toHaveAttribute('aria-busy', 'true');
    await expect(dialog.locator('[data-run-create-submit]')).toBeDisabled();
    await expect(dialog.locator('[data-run-create-dismiss]')).toBeDisabled();
    await page.keyboard.press('Escape');
    await expect(dialog).toBeVisible();
    await expect(page).toHaveURL(new RegExp(`/runs/${createdFixture.body.run_id}(?:\\?|$)`));
  });

  for (const [variant, code] of [
    ['forbidden', 'forbidden'],
    ['not-found', 'not_found'],
    ['too-large', 'payload_too_large'],
  ] as const) {
    test(`retains a ${variant} refusal without navigating (${locale})`, async ({ page }) => {
      await page.goto(path(locale, variant));
      await page.locator('[data-run-create-trigger]').click();
      const dialog = page.locator('[data-run-create-dialog]');
      await dialog.locator('#run-create-manifestId').fill(MANIFEST);
      await dialog.locator('[data-run-create-submit]').click();
      await expect(dialog.locator('[data-run-create-error]')).toContainText(code);
      await expect(dialog.locator('#run-create-manifestId')).toHaveValue(MANIFEST);
      await expect(page).toHaveURL(/\/runs\?/);
    });
  }

  test(`disables the trigger with a visible permission reason (${locale})`, async ({ page }) => {
    await page.goto(path(locale, 'ok', 'settings-policy-readonly'));
    await expect(page.locator('[data-run-create-trigger]')).toBeDisabled();
    await expect(page.locator('#run-create-permission')).toContainText('run:create');
  });

  for (const theme of THEMES) {
    test(`uses the shared dialog at desktop and remains read-only at 360px (${locale}, ${theme})`, async ({
      page,
    }) => {
      await seedStoredTheme(page, theme);
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto(path(locale));
      await page.locator('[data-run-create-trigger]').click();
      const dialog = page.locator('[data-run-create-dialog]');
      await expect(dialog).toBeVisible();
      await expectNoA11yViolations(page, `create-run dialog (${locale}, ${theme})`);
      await page.keyboard.press('Escape');
      await expect(page.locator('[data-run-create-trigger]')).toBeFocused();
      await page.setViewportSize({ width: 360, height: 800 });
      await expect(page.locator('[data-run-create-trigger]')).toHaveCount(0);
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
    });
  }
}
