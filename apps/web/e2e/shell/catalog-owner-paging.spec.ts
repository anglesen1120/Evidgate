import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  expectFieldColumns,
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  waitForDialogAnimations,
  withVariants,
} from './support';

const ROWS = 'app-case-list-table [data-row-id]';

async function tabTo(page: Page, target: Locator): Promise<void> {
  for (let step = 0; step < 100; step++) {
    if (await target.evaluate((element) => element === document.activeElement)) return;
    await page.keyboard.press('Tab');
  }
  await expect(target).toBeFocused();
}

// Brain publishes the highlighted option through aria-activedescendant once the overlay is
// keyboard-ready; a key sent earlier is dropped, so wait for that state instead of a delay.
async function moveHome(page: Page, trigger: Locator): Promise<void> {
  await expect(trigger).toHaveAttribute('aria-activedescendant', /.+/);
  await page.keyboard.press('Home');
  const first = page.getByRole('option').first();
  await expect(trigger).toHaveAttribute(
    'aria-activedescendant',
    (await first.getAttribute('id')) ?? /.+/,
  );
}

async function chooseOwnerWithKeyboard(page: Page, edit: Locator): Promise<string> {
  await tabTo(page, edit);
  await page.keyboard.press('Enter');
  const picker = page.locator('#catalog-record-owner');
  await expect(picker).toBeFocused();
  await page.keyboard.press('Enter');
  const email = 'qa@acme.test';
  const options = page.getByRole('option', { disabled: false });
  await expect(options.first()).toBeVisible();
  const labels = await options.allTextContents();
  const index = labels.findIndex((label) => label.trim() === email);
  expect(index).toBeGreaterThanOrEqual(0);
  await moveHome(page, picker);
  for (let step = 0; step < index; step++) await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  // Brain animates the overlay out; scanning mid-fade reads the option's blended colour.
  await expect(page.getByRole('listbox')).toHaveCount(0);
  await tabTo(page, page.locator('[data-catalog-save]'));
  await page.keyboard.press('Enter');
  return email;
}

for (const locale of LOCALES) {
  const path = localePath(
    locale,
    `/dev/catalog-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`,
  );
  const base = withVariants(path, [
    '?fixture.variant.listCases=ok',
    '?fixture.variant.listPermissions=catalog-owner',
    '?fixture.variant.listMemberships=ok',
    '?fixture.variant.updateCase=ok',
  ]);
  const labels =
    locale === 'vi'
      ? {
          more: 'Tải thêm',
          count: 'định danh đã tải',
          narrow: 'rộng từ 768 pixel',
          retry: 'Tải lại để thử lại',
        }
      : {
          more: 'Load more',
          count: 'loaded',
          narrow: 'at least 768 pixels wide',
          retry: 'Reload to retry',
        };

  for (const theme of THEMES) {
    for (const width of [360, 1280]) {
      test(`catalog owner and append controls stay accessible (${locale}, ${theme}, ${width})`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base);
        await expect(page.locator(ROWS)).toHaveCount(2);
        const row = page.locator(ROWS).first();
        const edit = row.locator('[data-catalog-edit]');
        if (width === 360) {
          await expect(edit).toHaveCount(0);
          await expect(page.locator('#catalog-owner-help')).toContainText(labels.narrow);
        } else {
          await expect(edit).toBeEnabled();
          const email = await chooseOwnerWithKeyboard(page, edit);
          await expect(page.getByRole('dialog')).toHaveCount(0);
          await expect(row.locator('[data-catalog-owner]')).toContainText(email);
          await expect(page.locator('[data-catalog-owner-pending]')).toHaveCount(0);
          await expect(page.locator('[data-catalog-owner-error]')).toHaveCount(0);
        }
        const more = page.getByRole('button', { name: labels.more, exact: true });
        await tabTo(page, more);
        await page.keyboard.press('Enter');
        await expect(page.locator(ROWS)).toHaveCount(3);
        await expect(page.locator(ROWS).nth(2)).toBeFocused();
        await expect(page.locator('[data-catalog-loaded]')).toContainText(`3 ${labels.count}`);
        await expect(page.locator('[data-catalog-summary]')).toContainText(`3 ${labels.count}`);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
        await expectNoA11yViolations(page, 'app-case-list-page');
      });
    }
  }

  for (const refusal of ['forbidden', 'conflict', 'precondition-failed']) {
    test(`catalog restores the owner after ${refusal} (${locale})`, async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto(withVariants(base, [`?fixture.variant.updateCase=${refusal}`]));
      const row = page.locator(ROWS).first();
      const edit = row.locator('[data-catalog-edit]');
      const value = row.locator('[data-catalog-owner] app-data-table-value').first();
      await expect(edit).toBeEnabled();
      const previous = (await value.innerText()).trim();
      await chooseOwnerWithKeyboard(page, edit);
      await expect(page.locator('[data-catalog-owner-error]')).toBeVisible();
      await expect(value).toContainText(previous);
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('button', { name: labels.retry, exact: true })).toBeVisible();
      await expectNoA11yViolations(page, '[role="dialog"]');
      await dialog.locator('[data-catalog-cancel]').click();
      await dialog.locator('[data-catalog-discard]').click();
      await expectNoA11yViolations(page, 'app-case-list-page');
    });
  }

  test(
    'record View preserves list query, Back, Forward and deep-link close (' + locale + ')',
    async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(withVariants(base, ['?limit=25']));
      const row = page.locator(ROWS).first();
      const opener = row.locator('[data-catalog-view]');
      await opener.click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.locator('#catalog-record-title')).toBeFocused();
      await waitForDialogAnimations(page, '[role="dialog"]');
      await expectFieldColumns(dialog.locator('app-case-record-facts dl').last(), 3);
      await expect(page).toHaveURL(/mode=view/);
      const recordUrl = page.url();
      expect(new URL(recordUrl).searchParams.get('limit')).toBe('25');
      await page.goBack();
      await expect(dialog).toHaveCount(0);
      await expect(opener).toBeFocused();
      await page.goForward();
      await expect(dialog).toBeVisible();
      await page.goto(recordUrl);
      await expect(dialog).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(page).not.toHaveURL(/record=/);
      expect(new URL(page.url()).searchParams.get('limit')).toBe('25');
    },
  );

  test(
    'Back cannot discard a changed owner without confirmation (' + locale + ')',
    async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(base);
      const row = page.locator(ROWS).first();
      await row.locator('[data-catalog-edit]').click();
      const dialog = page.getByRole('dialog');
      await dialog.locator('#catalog-record-owner').click();
      await page.getByRole('option', { name: 'qa@acme.test', exact: true }).click();
      await page.goBack();
      await expect(dialog.locator('[data-catalog-keep]')).toBeFocused();
      await dialog.locator('[data-catalog-keep]').click();
      await expect(dialog.locator('#catalog-record-owner')).toContainText('qa@acme.test');
      await expect(page).toHaveURL(/mode=edit/);
      await page.goBack();
      await dialog.locator('[data-catalog-discard]').click();
      await expect(dialog).toHaveCount(0);
      await expect(page).not.toHaveURL(/record=/);
      await expect(row.locator('[data-catalog-edit]')).toBeFocused();
    },
  );

  test(`catalog page size stays in the URL and resets the cursor (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(base);
    await tabTo(page, page.locator('#catalog-page-size'));
    await page.keyboard.press('Enter');
    await expect(page.getByRole('option', { name: '25', exact: true })).toBeVisible();
    await moveHome(page, page.locator('#catalog-page-size'));
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/limit=25/);
    expect(new URL(page.url()).searchParams.has('cursor')).toBe(false);
    await page.reload();
    await expect(page.locator('#catalog-page-size')).toContainText('25');
    await expect(page.locator('[data-catalog-loaded]')).toContainText(`2 ${labels.count}`);
  });
}
