import { expect, test, type Page } from '@playwright/test';
import caseFixture from '../../../../fixtures/web-api/authoring-editor-case/input.json';
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
const CONFLICT_CASE = 'a4430000-0000-4000-8000-000000000002';
const ITEM = caseFixture.body.checklist_item_id;

function editorPath(locale: ShellLocale, caseId = CASE) {
  return withVariants(
    localePath(
      locale,
      '/orgs/' + ORG_ACME + '/projects/' + PROJECT_STOREFRONT + '/authoring/cases/' + caseId,
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

async function pendingTargets(page: Page) {
  return page.evaluate(
    (project) =>
      (window.__evidgateFixtureRequests ?? [])
        .filter(
          (request) =>
            request.method === 'GET' &&
            new URL(request.url, 'http://127.0.0.1').pathname ===
              '/v1/projects/' + project + '/authoring/changes',
        )
        .map(
          (request) => new URL(request.url, 'http://127.0.0.1').searchParams.get('target_id') ?? '',
        ),
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
    expect(await pendingTargets(page)).toEqual([ITEM]);
    // The existing pending fixtures target other checklist items, not this case's loaded parent.
    await expect(editor.locator('[data-review-change]')).toHaveCount(0);
    const queueLink = editor.locator('a[href$="/pending-changes"]');
    await expect(queueLink).toBeVisible();
    await expect(queueLink).not.toHaveAttribute('href', /\/authoring\/pending-changes$/);

    await editor.locator('#review-comment').fill('Unsent review comment');
    await editor.locator('#review-reason').fill('Unsent review reason');
    await firstTab.click();
    await expect(editor.locator('app-checklist-review-tab')).toHaveCount(1);
    await expect(editor.locator('app-checklist-review-tab')).toBeHidden();
    await expect(editor.locator('#case-title')).toBeVisible();
    await reviewTab.click();
    await expect(editor.locator('#review-comment')).toHaveValue('Unsent review comment');
    await expect(editor.locator('#review-reason')).toHaveValue('Unsent review reason');
    expect(await pendingReads(page)).toBe(1);
    await firstTab.click();
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

test('Escape restores the Save opener after a routed editor save conflicts', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(editorPath('en', CONFLICT_CASE));
  const editor = page.locator('app-authoring-editor');
  const title = editor.locator('#case-title');
  await expect(title).toBeVisible();
  await title.fill('Unsaved keyboard conflict draft');
  const save = editor.locator('[data-editor-save]');
  await save.focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(save).not.toHaveAttribute('disabled');
  await expect(save).toHaveAttribute('aria-disabled', 'true');
  for (const key of ['Tab', 'Shift+Tab']) {
    for (let step = 0; step < 8; step++) {
      await page.keyboard.press(key);
      expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(
        true,
      );
    }
  }
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(save).toBeFocused();
  await expect(save).toBeEnabled();
  await expect(title).toHaveValue('Unsaved keyboard conflict draft');
  const writes = await page.evaluate(
    (caseId) =>
      (window.__evidgateFixtureRequests ?? []).filter(
        (request) =>
          request.method === 'PATCH' &&
          new URL(request.url, location.origin).pathname.endsWith(
            '/authoring/manual-cases/' + caseId,
          ),
      ),
    CONFLICT_CASE,
  );
  expect(writes).toHaveLength(1);
});
