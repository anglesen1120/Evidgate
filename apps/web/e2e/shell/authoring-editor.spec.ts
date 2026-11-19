import { expect, test, type Page } from '@playwright/test';
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

const CASE = 'a4430000-0000-4000-8000-000000000001';
const CONFLICT = 'a4430000-0000-4000-8000-000000000002';
const INVALID = 'a4430000-0000-4000-8000-000000000003';
const FIRST_VERSION = 'a4430000-0000-4000-8000-000000000101';
function path(locale: ShellLocale, id = CASE, writable = true, overrides: readonly string[] = []) {
  return withVariants(
    localePath(
      locale,
      '/orgs/' + ORG_ACME + '/projects/' + PROJECT_STOREFRONT + '/authoring/cases/' + id,
    ),
    [
      '?fixture.variant.listPermissions=authoring-editor-' + (writable ? 'write' : 'read'),
      ...overrides,
    ],
  );
}
async function writes(page: Page) {
  return page.evaluate(
    (project) =>
      (window.__evidgateFixtureRequests ?? []).filter(
        (request) =>
          (request.method === 'PATCH' || request.method === 'POST') &&
          new URL(request.url, 'http://127.0.0.1').pathname.startsWith(
            '/v1/projects/' + project + '/authoring/',
          ),
      ),
    PROJECT_STOREFRONT,
  );
}
async function timelineRows(page: Page) {
  return page.locator('[data-version-row]').evaluateAll((rows) =>
    rows.map((row) => ({
      id: row.getAttribute('data-version-row'),
      text: row.textContent,
    })),
  );
}
for (const locale of LOCALES) {
  test.describe('Manual case editor ' + locale, () => {
    test('keyboard edit saves once with the loaded ETag and exposes immutable history', async ({
      page,
    }) => {
      await page.goto(path(locale));
      await expect(page.locator('#case-title')).toBeVisible();
      await page.locator('#case-title').fill('Updated manual case title');
      await page.locator('[data-editor-save]').focus();
      await page.keyboard.press('Enter');
      await expect(page.locator('[data-editor-saved]')).toBeVisible();
      const mutations = await writes(page);
      expect(mutations).toHaveLength(1);
      expect(mutations[0]?.method).toBe('PATCH');
      expect(mutations[0]?.headers['if-match']).toEqual(['"3"']);
      expect(mutations[0]?.headers['idempotency-key']?.[0]).toBeTruthy();
      await expect(page.locator('#case-reason')).toBeDisabled();
      await page.locator('[data-editor-timeline-tab]').click();
      await page.locator('[data-inspect-version]').first().click();
      await expect(page.locator('[data-editor-version-detail]')).toContainText(
        'Updated manual case title',
      );
      await expect(page.locator('[data-editor-version-detail]')).toContainText('sha256:');
    });
    test('412 shows the fresh revision and every draft field survives Escape without a second write', async ({
      page,
    }) => {
      await page.goto(path(locale, CONFLICT));
      const draft = {
        'case-title': 'Unsaved title',
        'case-description': 'Unsaved description',
        'case-precondition': 'Unsaved preconditions',
        'case-step-0-action': 'Unsaved action',
        'case-step-0-expected': 'Unsaved expected',
        'case-step-0-data': 'Unsaved data',
      };
      for (const [id, value] of Object.entries(draft)) await page.locator('#' + id).fill(value);
      await page.locator('[data-editor-save]').click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(dialog).toContainText('Sign in with a password and MFA');
      await expect(dialog).toContainText('"3"');
      await page.keyboard.press('Escape');
      await expect(dialog).not.toBeVisible();
      for (const [id, value] of Object.entries(draft))
        await expect(page.locator('#' + id)).toHaveValue(value);
      expect(await writes(page)).toHaveLength(1);
    });
    test('422 keeps the draft and links errors to refused fields', async ({ page }) => {
      await page.goto(path(locale, INVALID));
      await page.locator('#case-title').fill('Refused draft title');
      await page.locator('#case-step-0-action').fill('Refused draft action');
      await page.locator('[data-editor-save]').click();
      await expect(page.locator('#case-errors')).toBeVisible();
      await expect(page.locator('#case-title')).toHaveAttribute('aria-invalid', 'true');
      await expect(page.locator('#case-step-0-action')).toHaveAttribute('aria-invalid', 'true');
      await expect(page.locator('#case-title')).toHaveValue('Refused draft title');
      expect(await writes(page)).toHaveLength(1);
    });
    test('503 retains every edited field and respects Retry-After without automatic writes', async ({
      page,
    }) => {
      await page.goto(path(locale, CASE, true, ['?fixture.variant.updateManualCase=unavailable']));
      const draft = {
        'case-title': 'Unavailable draft',
        'case-description': 'Retained description',
        'case-precondition': 'Retained precondition',
        'case-step-0-action': 'Retained action',
        'case-step-0-expected': 'Retained expected result',
        'case-step-0-data': 'Retained data',
      };
      for (const [id, value] of Object.entries(draft)) await page.locator('#' + id).fill(value);
      await page.locator('[data-editor-save]').click();
      await expect(page.locator('app-retry-after')).toBeVisible();
      for (const [id, value] of Object.entries(draft))
        await expect(page.locator('#' + id)).toHaveValue(value);
      expect(await writes(page)).toHaveLength(1);
    });
    test('write denial explains disabled Save while read denial exposes no content', async ({
      page,
    }) => {
      await page.goto(path(locale, CASE, false));
      await expect(page.locator('[data-editor-save]')).toBeDisabled();
      await expect(page.locator('#case-write-reason')).toContainText('authoring:write');
      await page.locator('[data-editor-timeline-tab]').click();
      await page.locator('[data-inspect-version]').first().click();
      await expect(page.locator('[data-restore-open]')).toBeDisabled();
      expect(await writes(page)).toHaveLength(0);
      await page.goto(path(locale, CASE, true, ['?fixture.variant.getManualCase=forbidden']));
      await expect(page.locator('app-forbidden-pattern')).toBeVisible();
      await expect(page.locator('#case-title')).toHaveCount(0);
    });
    test('steps add, reorder and remove through the keyboard and navigation warns before discarding', async ({
      page,
    }) => {
      await page.goto(path(locale));
      await page.locator('[data-step-add]').focus();
      await page.keyboard.press('Enter');
      const actions = page.locator('[data-step-field][id$="-action"]');
      const last = actions.last();
      await expect(last).toBeFocused();
      await last.fill('Keyboard-added step');
      const index = (await actions.count()) - 1;
      await page
        .locator('#case-step-' + index + '-expected')
        .fill('Keyboard-added expected result');
      await page.locator('[data-step-up="' + index + '"]').focus();
      await page.keyboard.press('Enter');
      await expect(page.locator('#case-step-' + (index - 1) + '-action')).toHaveValue(
        'Keyboard-added step',
      );
      await page.locator('[data-step-remove="' + (index - 1) + '"]').focus();
      await page.keyboard.press('Enter');
      await expect(page.locator('#case-step-' + (index - 1) + '-action')).toBeFocused();
      await page.locator('#case-title').fill('Draft protected from navigation');
      await page.locator('a[href$="/overview"]').first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await dialog
        .getByRole('button', {
          name: locale === 'vi' ? 'Tiếp tục chỉnh sửa' : 'Keep editing',
          exact: true,
        })
        .click();
      await expect(page.locator('#case-title')).toHaveValue('Draft protected from navigation');
      expect(await writes(page)).toHaveLength(0);
    });
    test('restore needs confirmation, appends one id and leaves earlier rows identical', async ({
      page,
    }) => {
      await page.goto(path(locale));
      await page.locator('[data-editor-timeline-tab]').click();
      const before = await timelineRows(page);
      expect(before.length).toBeGreaterThan(1);
      await page.locator('[data-inspect-version="' + FIRST_VERSION + '"]').click();
      await page.locator('[data-restore-open]').click();
      await waitForDialogAnimations(page, '[role="dialog"]');
      const dialog = page.getByRole('dialog');
      await expect(page.locator('[data-restore-cancel]')).toBeFocused();
      await expect(page.locator('#case-restore-reason')).toBeDisabled();
      expect(await writes(page)).toHaveLength(0);
      await page.keyboard.press('Escape');
      await expect(dialog).not.toBeVisible();
      expect(await writes(page)).toHaveLength(0);
      await page.locator('[data-restore-open]').click();
      await page.locator('[data-restore-confirm]').click();
      await expect(page.locator('[data-editor-restored]')).toBeVisible();
      const after = await timelineRows(page);
      expect(after).toHaveLength(before.length + 1);
      expect(after.slice(1)).toEqual(before);
      expect(before.some((row) => row.id === after[0]?.id)).toBe(false);
      const mutations = await writes(page);
      expect(mutations).toHaveLength(1);
      expect(mutations[0]?.method).toBe('POST');
      expect(mutations[0]?.headers['if-match']).toBeUndefined();
    });
    for (const theme of THEMES) {
      test(
        'editor and timeline reflow, accessible dialogs and retained draft in ' + theme,
        async ({ page }) => {
          await seedStoredTheme(page, theme);
          await page.setViewportSize({ width: 1280, height: 900 });
          await page.goto(path(locale));
          await expect(page.locator('#case-title')).toBeVisible();
          await expectNoA11yViolations(page, 'authoring-editor-' + locale + '-' + theme);
          const longDraft = 'Long retained draft '.repeat(15);
          await page.locator('#case-title').fill(longDraft);
          await page.locator('#case-title').focus();
          await page.setViewportSize({ width: 360, height: 800 });
          await expect(page.locator('#case-title')).toHaveCount(0);
          await expect(page.locator('app-authoring-editor')).toContainText(longDraft.trim());
          await expect(page.locator('[data-editor-save]')).toHaveCount(0);
          expect(
            await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
          ).toBe(true);
          await expectNoA11yViolations(page, 'authoring-editor-narrow-' + locale + '-' + theme);
          await page.locator('#case-panel-select').click();
          await page
            .getByRole('option', {
              name: locale === 'vi' ? 'Dòng thời gian' : 'Timeline',
              exact: true,
            })
            .click();
          await expect(page.locator('[data-version-row]').first()).toBeVisible();
          await page.locator('[data-inspect-version]').first().click();
          await expect(page.locator('[data-restore-open]')).toBeDisabled();
          await expectNoA11yViolations(page, 'authoring-timeline-narrow-' + locale + '-' + theme);
          await page.setViewportSize({ width: 1280, height: 900 });
          await page.locator('[data-restore-open]').click();
          await waitForDialogAnimations(page, '[role="dialog"]');
          await expectNoA11yViolations(page, 'authoring-restore-' + locale + '-' + theme);
          await page.setViewportSize({ width: 360, height: 800 });
          await expect(page.getByRole('dialog')).not.toBeVisible();
          expect(await writes(page)).toHaveLength(0);
          await page.setViewportSize({ width: 1280, height: 900 });
          await expect(page.locator('[data-restore-open]')).toBeVisible();
          await expect(page.getByRole('dialog')).not.toBeVisible();
        },
      );
    }
  });
}
