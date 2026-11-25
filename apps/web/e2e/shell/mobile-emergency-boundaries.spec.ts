import { expect, test, type Locator, type Page } from '@playwright/test';
import type { FixtureCallRecord } from '../../src/app/core/dev-fixtures/fixture-request-log';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  withVariants,
  type ShellLocale,
} from './support';

const MOBILE = { width: 390, height: 844 };
const WIDE = { width: 1280, height: 900 };
const SWITCH_PATH = '/healing/switch';
const PROJECT_EDITOR = '[data-healing-editor="project"]';
const DISABLE_TRIGGER = `${PROJECT_EDITOR} [data-healing-disable]`;
const DIALOG_CANCEL = '[data-healing-cancel]';
const DIALOG_CONFIRM = '[data-healing-confirm]';

const ENABLED: Record<ShellLocale, string> = { en: 'Enabled', vi: 'Bật' };
const DISABLED: Record<ShellLocale, string> = { en: 'Disabled', vi: 'Tắt' };
/** Substring of the scope-named dialog title; the scope label keeps the title stable. */
const TITLE: Record<ShellLocale, string> = {
  en: 'Disable healing for Project scope',
  vi: 'Tắt tự sửa cho Phạm vi dự án',
};

async function openPage(page: Page, locale: ShellLocale, permissions: string): Promise<void> {
  await page.goto(
    withVariants(
      localePath(locale, `/dev/healing-switch/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
      [`?fixture.variant.listPermissions=${permissions}`],
    ),
  );
  await expect(page.locator('[data-healing-resolved-mode]')).toHaveText(ENABLED[locale]);
}

/** The `PUT /healing/switch` calls the fixture backend handled, read from the page-global log. */
async function switchWrites(page: Page): Promise<FixtureCallRecord[]> {
  return page.evaluate(
    (ending) =>
      (window.__evidgateFixtureRequests ?? []).filter(
        (record) =>
          record.method === 'PUT' && new URL(record.url, location.origin).pathname.endsWith(ending),
      ),
    SWITCH_PATH,
  );
}

async function expectTouchTarget(locator: Locator): Promise<void> {
  await expect(locator).toBeVisible();
  // Dialog content opens with a zoom-in-95 transition; wait for the final box.
  await expect
    .poll(async () => {
      const box = await locator.boundingBox();
      return Math.min(box?.width ?? 0, box?.height ?? 0);
    })
    .toBeGreaterThanOrEqual(44);
}

async function expectNoHorizontalScroll(page: Page): Promise<void> {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
}

for (const locale of LOCALES) {
  test(`390px: Escape and Cancel keep healing enabled and return focus to the trigger (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize(MOBILE);
    await openPage(page, locale, 'healing-configure');
    const trigger = page.locator(DISABLE_TRIGGER);
    await expectTouchTarget(trigger);
    const dialog = page.getByRole('alertdialog');

    await trigger.click();
    await expect(dialog.getByText(TITLE[locale])).toBeVisible();
    await expect(dialog.locator(DIALOG_CANCEL)).toBeFocused();
    await expectTouchTarget(dialog.locator(DIALOG_CANCEL));
    await expectTouchTarget(dialog.locator(DIALOG_CONFIRM));

    const focusables = dialog.locator('button:enabled:visible');
    await focusables.first().focus();
    await page.keyboard.press('Shift+Tab');
    await expect(focusables.last()).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(focusables.first()).toBeFocused();

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(trigger).toBeFocused();
    await expect(page.locator('[data-healing-resolved-mode]')).toHaveText(ENABLED[locale]);
    expect(await switchWrites(page)).toHaveLength(0);

    await trigger.click();
    await dialog.locator(DIALOG_CANCEL).click();
    await expect(dialog).toBeHidden();
    await expect(trigger).toBeFocused();
    expect(await switchWrites(page)).toHaveLength(0);
    await expectNoHorizontalScroll(page);
  });

  test(`390px: a double click on confirm writes exactly once and hides the trigger (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize(MOBILE);
    await openPage(page, locale, 'healing-configure');
    const trigger = page.locator(DISABLE_TRIGGER);
    const dialog = page.getByRole('alertdialog');

    await trigger.click();
    await expect(dialog).toBeVisible();
    const confirm = dialog.locator(DIALOG_CONFIRM);
    const box = await confirm.boundingBox();
    if (!box) throw new Error('Confirm button has no bounds');
    await page.mouse.dblclick(box.x + box.width / 2, box.y + box.height / 2, { delay: 40 });

    await expect(page.locator('[data-healing-resolved-mode]')).toHaveText(DISABLED[locale]);
    await expect(dialog).toBeHidden();
    await expect(page.locator(DISABLE_TRIGGER)).toHaveCount(0);
    const writes = await switchWrites(page);
    expect(writes).toHaveLength(1);
    expect(writes[0]?.headers['idempotency-key']?.[0]?.trim()).toBeTruthy();
    await expectNoHorizontalScroll(page);
  });

  test(`390px read-only role offers no containment trigger and sends nothing (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize(MOBILE);
    await openPage(page, locale, 'healing-triage');
    await expect(page.locator(DISABLE_TRIGGER)).toHaveCount(0);
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
    expect(await switchWrites(page)).toHaveLength(0);
    await expectNoHorizontalScroll(page);
  });
}

test('desktop width: the containment trigger is not offered and no confirm opens', async ({
  page,
}) => {
  await page.setViewportSize(WIDE);
  await openPage(page, 'en', 'healing-configure');
  await expect(page.locator(DISABLE_TRIGGER)).toHaveCount(0);
  await expect(page.getByRole('alertdialog')).toHaveCount(0);
  expect(await switchWrites(page)).toHaveLength(0);
});
