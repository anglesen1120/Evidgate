import { expect, test, type Page } from '@playwright/test';
import type {} from '../../src/app/core/dev-fixtures/fixture-request-log';
import {
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  localePath,
  withVariants,
  type ShellLocale,
} from './support';

const CASE = 'a4430000-0000-4000-8000-000000000001';

function editorPath(locale: ShellLocale) {
  return withVariants(
    localePath(
      locale,
      '/orgs/' + ORG_ACME + '/projects/' + PROJECT_STOREFRONT + '/authoring/cases/' + CASE,
    ),
    [
      '?fixture.variant.listPermissions=authoring-editor-write',
      '?fixture.variant.listAuthoringChanges=ok',
    ],
  );
}

async function pendingReads(page: Page) {
  return page.evaluate(
    (project) =>
      (window.__evidgateFixtureRequests ?? []).filter(
        (request) =>
          request.method === 'GET' &&
          new URL(request.url, 'http://127.0.0.1').pathname ===
            '/v1/projects/' + project + '/authoring/changes',
      ).length,
    PROJECT_STOREFRONT,
  );
}

test('the editor review tab mounts lazily through the keyboard and the narrow select in both locales', async ({
  page,
}) => {
  for (const locale of LOCALES) {
    const label = locale === 'vi' ? 'Xem xét danh sách kiểm tra' : 'Checklist review';
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(editorPath(locale));
    const editor = page.locator('app-authoring-editor');
    await expect(editor.locator('#case-title')).toBeVisible();
    const firstTab = editor.getByRole('tab').first();
    const reviewTab = editor.locator('[data-editor-review-tab]');
    await expect(reviewTab).toHaveAccessibleName(label);
    await expect(reviewTab).toHaveAttribute('aria-selected', 'false');
    await expect(editor.locator('app-checklist-review-tab')).toHaveCount(0);
    expect(await pendingReads(page)).toBe(0);

    await firstTab.focus();
    await page.keyboard.press('ArrowRight');
    await expect(reviewTab).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(reviewTab).toHaveAttribute('aria-selected', 'true');
    await expect(editor.getByRole('tabpanel')).toHaveAccessibleName(label);
    await expect(editor.locator('#review-comment')).toBeVisible();
    expect(await pendingReads(page)).toBe(1);
    // The existing pending fixtures target other checklist items, not this case's loaded parent.
    await expect(editor.locator('[data-review-change]')).toHaveCount(0);
    await expect(editor.locator('a[href$="/authoring/pending-changes"]')).toBeVisible();

    await firstTab.click();
    await expect(editor.locator('app-checklist-review-tab')).toHaveCount(0);
    await expect(editor.locator('#case-title')).toBeVisible();
    await page.setViewportSize({ width: 390, height: 900 });
    const select = editor.locator('#case-panel-select');
    await expect(select).toBeVisible();
    await select.click();
    const option = page.getByRole('option').nth(1);
    await expect(option).toHaveText(label);
    await option.click();
    await expect(select).toContainText(label);
    await expect(editor.locator('app-checklist-review-tab')).toBeVisible();
    await expect(editor.locator('#review-comment')).toHaveCount(0);
    await expect(editor.locator('[data-review-comment-snapshot]')).toBeVisible();
  }
});
