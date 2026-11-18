import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
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

async function chooseOwnerWithKeyboard(page: Page, picker: Locator): Promise<string> {
  await tabTo(page, picker);
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
        const picker = page.locator(ROWS).first().locator('hlm-select-trigger button');
        if (width === 360) {
          await expect(picker).toBeDisabled();
          await expect(page.locator('#catalog-owner-help')).toContainText(labels.narrow);
        } else {
          await expect(picker).toBeEnabled();
          const email = await chooseOwnerWithKeyboard(page, picker);
          await expect(picker).toContainText(email);
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
      const picker = page.locator(ROWS).first().locator('hlm-select-trigger button');
      await expect(picker).toBeEnabled();
      const previous = (await picker.innerText()).trim();
      await chooseOwnerWithKeyboard(page, picker);
      await expect(page.locator('[data-catalog-owner-error]')).toBeVisible();
      await expect(picker).toContainText(previous);
      await expect(page.getByRole('button', { name: labels.retry, exact: true })).toBeVisible();
      await expectNoA11yViolations(page, 'app-case-list-page');
    });
  }

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
