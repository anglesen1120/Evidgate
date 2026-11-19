import { expect, test } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import type {} from '../../src/app/core/dev-fixtures/fixture-request-log';
import {
  LOCALES,
  THEMES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  localePath,
  withVariants,
  seedStoredTheme,
  waitForDialogAnimations,
  type ShellLocale,
} from './support';

function path(locale: ShellLocale, variant = 'ok', writable = true, read = 'ok') {
  return withVariants(
    localePath(locale, '/orgs/' + ORG_ACME + '/projects/' + PROJECT_STOREFRONT + '/ai-settings'),
    [
      '?fixture.variant.listPermissions=ai-settings-' + (writable ? 'write' : 'read'),
      '?fixture.variant.saveProjectAiSettings=' + variant,
      '?fixture.variant.getProjectAiSettings=' + read,
    ],
  );
}
for (const locale of LOCALES) {
  test.describe('AI settings ' + locale, () => {
    test('loads, edits, saves with loaded ETag and inspects the immutable saved version', async ({
      page,
    }) => {
      await page.goto(path(locale));
      await expect(page.locator('#ai-writing_style')).toHaveValue(
        'Use concise observable steps for the fixture checkout.',
      );
      await page
        .locator('#ai-writing_style')
        .fill('Concise updated review-only checkout guidance.');
      await page.locator('[data-ai-save]').focus();
      await page.keyboard.press('Enter');
      await expect(page.locator('[data-ai-saved]')).toBeVisible();
      const puts = await page.evaluate((projectId) => {
        return (window.__evidgateFixtureRequests ?? []).filter(
          (request) =>
            request.method === 'PUT' &&
            new URL(request.url, 'http://127.0.0.1').pathname ===
              '/v1/projects/' + projectId + '/ai-settings',
        );
      }, PROJECT_STOREFRONT);
      expect(puts).toHaveLength(1);
      expect(puts[0]?.headers['if-match']).toEqual(['"2"']);
      expect(puts[0]?.headers['idempotency-key']?.[0]).toBeTruthy();
      await page.locator('[data-ai-history-tab]').click();
      await page.locator('[data-ai-inspect]').first().click();
      await expect(page.locator('[data-ai-version-detail]')).toContainText(
        'Concise updated review-only checkout guidance.',
      );
    });
    test('412 fetches server snapshot and keeps the unsaved draft after Escape', async ({
      page,
    }) => {
      await page.goto(path(locale, 'conflict'));
      await page.locator('#ai-writing_style').fill('Unsaved conflict fixture guidance.');
      await page.locator('[data-ai-save]').click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(dialog).toContainText('The other fixture editor saved this guidance.');
      await page.keyboard.press('Escape');
      await expect(dialog).not.toBeVisible();
      await expect(page.locator('#ai-writing_style')).toHaveValue(
        'Unsaved conflict fixture guidance.',
      );
    });
    test('422 errors stay associated with retained input', async ({ page }) => {
      await page.goto(path(locale, 'validation'));
      await page.locator('#ai-writing_style').fill('Retained refused fixture guidance.');
      await page.locator('[data-ai-save]').click();
      await expect(page.locator('#ai-errors')).toBeVisible();
      await expect(page.locator('#ai-writing_style')).toHaveAttribute('aria-invalid', 'true');
      await expect(page.locator('#ai-writing_style')).toHaveValue(
        'Retained refused fixture guidance.',
      );
    });
    test('history pages and two independently fetched versions can be compared', async ({
      page,
    }) => {
      await page.goto(path(locale));
      await page.locator('[data-ai-history-tab]').click();
      await page.locator('[data-ai-inspect]').first().click();
      await expect(page.locator('[data-ai-version-detail]')).toBeVisible();
      await page
        .getByRole('button', { name: locale === 'vi' ? 'Trang tiếp' : 'Next page', exact: true })
        .click();
      await page
        .getByRole('button', {
          name: locale === 'vi' ? 'So sánh với 1' : 'Compare with 1',
          exact: true,
        })
        .click();
      await expect(page.locator('[data-ai-diff]')).toContainText(
        'Write fixture checkout steps in the active voice.',
      );
    });
    test('readable without write has an explained disabled Save', async ({ page }) => {
      await page.goto(path(locale, 'ok', false));
      await expect(page.locator('[data-ai-save]')).toBeDisabled();
      await expect(page.locator('#ai-write-reason')).toContainText('project:write');
    });
    test('403 does not expose the readable settings form', async ({ page }) => {
      await page.goto(path(locale, 'ok', true, 'forbidden'));
      await expect(page.locator('app-forbidden-pattern')).toBeVisible();
      await expect(page.locator('#ai-writing_style')).toHaveCount(0);
    });
    for (const theme of THEMES) {
      test('theme, keyboard, template Cancel and narrow read-only: ' + theme, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width: 1280, height: 900 });
        await page.goto(path(locale));
        await expect(page.locator('#ai-description')).toBeVisible();
        await expectNoA11yViolations(page, 'ai-settings-' + locale + '-' + theme);
        await page.getByRole('button', { name: /General fixture guidance/ }).click();
        await waitForDialogAnimations(page, '[role="dialog"]');
        const dialog = page.getByRole('dialog');
        await expect(
          dialog.getByRole('button', { name: locale === 'vi' ? 'Hủy' : 'Cancel', exact: true }),
        ).toBeFocused();
        await expectNoA11yViolations(page, 'ai-settings-template-' + locale + '-' + theme);
        await page.keyboard.press('Escape');
        await expect(dialog).not.toBeVisible();
        await page.locator('#ai-writing_style').fill('Retained desktop draft at narrow width.');
        await page.locator('#ai-writing_style').focus();
        await page.setViewportSize({ width: 360, height: 800 });
        await expect(page.locator('#ai-writing_style')).toHaveCount(0);
        await expect(page.locator('app-ai-settings-page')).toContainText(
          'Retained desktop draft at narrow width.',
        );
        await expect(page.locator('[data-ai-save]')).toHaveCount(0);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
        await expectNoA11yViolations(page, 'ai-settings-narrow-' + locale + '-' + theme);
        await page.setViewportSize({ width: 1280, height: 900 });
        await expect(page.locator('#ai-writing_style')).toHaveValue(
          'Retained desktop draft at narrow width.',
        );
      });
    }
  });
}
