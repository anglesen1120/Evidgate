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

const CASE = caseFixture.body.case_id;
const ITEM = caseFixture.body.checklist_item_id;
const QUEUE_PATH = '/v1/projects/' + PROJECT_STOREFRONT + '/authoring/changes';
const COMMENT = 'Routed review comment\nKeep Unicode: tiếng Việt.';
const COMMENT_REASON = 'Explain this comment from the editor';
const DELETE_REASON = 'Remove the loaded checklist item after review';
const COMMENT_WRITE = {
  method: 'POST',
  path: QUEUE_PATH,
  body: {
    target_kind: 'checklist_item',
    target_id: ITEM,
    change_type: 'comment',
    payload: { body: COMMENT },
    reason: COMMENT_REASON,
  },
};
const DELETE_WRITE = {
  method: 'POST',
  path: QUEUE_PATH,
  body: {
    target_kind: 'checklist_item',
    target_id: ITEM,
    change_type: 'delete',
    payload: {},
    reason: DELETE_REASON,
  },
};

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

async function reviewWrites(page: Page) {
  return page.evaluate(() =>
    (window.__evidgateFixtureRequests ?? []).flatMap((request) => {
      const path = new URL(request.url, location.origin).pathname;
      const queue = request.method === 'POST' && path.endsWith('/authoring/changes');
      const directEdit =
        request.method === 'PATCH' && path.includes('/authoring/manual-cases/');
      if (!queue && !directEdit) return [];
      const body: unknown =
        typeof request.body === 'string' ? JSON.parse(request.body) : request.body;
      return [{ method: request.method, path, body }];
    }),
  );
}

for (const locale of LOCALES) {
  test(`the routed editor review tab queues comment and delete bodies for its loaded item (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(editorPath(locale));
    const editor = page.locator('app-authoring-editor');
    await expect(editor.locator('#case-title')).toHaveValue(caseFixture.body.title);
    await editor.locator('[data-editor-review-tab]').click();
    const review = editor.locator('app-checklist-review-tab');
    await expect(review.locator('#review-comment')).toBeVisible();
    expect(await reviewWrites(page)).toEqual([]);

    await review.locator('[data-review-comment]').click();
    await expect(review.locator('#review-comment')).toHaveAttribute('aria-invalid', 'true');
    expect(await reviewWrites(page)).toEqual([]);
    await review.locator('#review-comment').fill(COMMENT);
    await review.locator('#review-reason').fill(COMMENT_REASON);
    await review.locator('[data-review-comment]').click();
    await expect(review.locator('[data-review-success]')).toBeVisible();
    await expect(review.locator('#review-comment')).toHaveValue('');
    await expect(review.locator('#review-reason')).toHaveValue(COMMENT_REASON);
    expect(await reviewWrites(page)).toEqual([COMMENT_WRITE]);

    await review.locator('#review-reason').fill(DELETE_REASON);
    await review.locator('[data-review-delete]').click();
    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('[data-pending-cancel]')).toBeFocused();
    await expect(dialog).toContainText(ITEM);
    await expect(dialog).not.toContainText(CASE);
    await expect(dialog).toContainText(DELETE_REASON);
    expect(await reviewWrites(page)).toEqual([COMMENT_WRITE]);
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    expect(await reviewWrites(page)).toEqual([COMMENT_WRITE]);

    await review.locator('[data-review-delete]').click();
    await expect(dialog).toBeVisible();
    await dialog.locator('[data-pending-confirm]').click();
    await expect(dialog).toHaveCount(0);
    await expect(review.locator('[data-review-success]')).toBeVisible();
    expect(await reviewWrites(page)).toEqual([COMMENT_WRITE, DELETE_WRITE]);
    await expect(review.locator('#review-comment')).toHaveValue('');
    await expect(review.locator('#review-reason')).toHaveValue(DELETE_REASON);
  });
}
