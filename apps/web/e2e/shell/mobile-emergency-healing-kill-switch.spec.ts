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

const NARROW = { width: 390, height: 844 };
const WIDE = { width: 1280, height: 900 };
const SWITCH_PATH = '/healing/switch';
const PROJECT_EDITOR = '[data-healing-editor="project"]';

interface Copy {
  readonly enabled: string;
  readonly disabled: string;
  readonly notRecorded: string;
  readonly title: string;
  readonly consequence: string;
}

const COPY: Record<ShellLocale, Copy> = {
  en: {
    enabled: 'Enabled',
    disabled: 'Disabled',
    notRecorded: 'Not recorded',
    title: 'Disable healing for Project scope',
    consequence: 'All shadow healing proposals for this scope will stop right away.',
  },
  vi: {
    enabled: 'Bật',
    disabled: 'Tắt',
    notRecorded: 'Chưa được ghi lại',
    title: 'Tắt tự sửa cho Phạm vi dự án',
    consequence: 'Mọi đề xuất tự sửa đối chứng của phạm vi này sẽ dừng ngay.',
  },
};

async function openPage(page: Page, locale: ShellLocale): Promise<void> {
  await page.goto(
    withVariants(
      localePath(locale, `/dev/healing-switch/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
      ['?fixture.variant.listPermissions=healing-configure'],
    ),
  );
  await expect(page.locator('[data-healing-resolved-mode]')).toHaveText(COPY[locale].enabled);
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
  const box = await locator.boundingBox();
  expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
}

async function expectNoHorizontalScroll(page: Page): Promise<void> {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
}

/** At 390px the mode controls are withheld and the desktop-required notice explains why. */
async function expectNarrowControls(page: Page): Promise<void> {
  await expect(page.locator('app-desktop-required-notice').getByRole('note')).toBeVisible();
  await expect(
    page.locator(
      '[data-healing-editor] [data-healing-mode], [data-healing-editor] [data-healing-save]',
    ),
  ).toHaveCount(0);
  await expectNoHorizontalScroll(page);
}

for (const locale of LOCALES) {
  test(`at 390px only the confirmed disable changes the project healing switch (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize(NARROW);
    await openPage(page, locale);
    await expectNarrowControls(page);

    const storedMode = page.locator(PROJECT_EDITOR).locator('[data-healing-stored-mode]');
    await expect(storedMode).toHaveText(COPY[locale].notRecorded);

    const trigger = page.locator(PROJECT_EDITOR).locator('[data-healing-disable]');
    await expectTouchTarget(trigger);
    await trigger.click();
    const dialog = page.getByRole('alertdialog');
    await expect(dialog.getByText(COPY[locale].title)).toBeVisible();
    await expect(dialog.getByText(COPY[locale].consequence)).toBeVisible();
    await expect(dialog.locator('[data-healing-cancel]')).toBeFocused();
    await expectNoHorizontalScroll(page);

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(storedMode).toHaveText(COPY[locale].notRecorded);
    expect(await switchWrites(page)).toHaveLength(0);

    await trigger.click();
    await dialog.locator('[data-healing-confirm]').click();
    await expect(dialog).toBeHidden();
    await expect(page.locator('[data-healing-resolved-mode]')).toHaveText(COPY[locale].disabled);
    await expect(storedMode).toHaveText(COPY[locale].disabled);
    await expectNarrowControls(page);

    const writes = await switchWrites(page);
    expect(writes).toHaveLength(1);
    const [write] = writes;
    expect(write?.headers['idempotency-key']?.[0]?.trim()).toBeTruthy();
  });

  test(`resizing an open non-OFF mode picker from 1280px to 390px cannot submit (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize(WIDE);
    await openPage(page, locale);
    const picker = page.locator(PROJECT_EDITOR).locator('[data-healing-mode]');
    await picker.click();
    await page.locator('[data-mode="shadow"]').click();
    await picker.click();
    await expect(page.locator('[data-mode="shadow"]')).toBeVisible();

    await page.setViewportSize(NARROW);
    await expectNarrowControls(page);
    await expect(page.locator('[data-mode="shadow"]')).toBeHidden();
    await expect(page.locator('[data-healing-resolved-mode]')).toHaveText(COPY[locale].enabled);
    expect(await switchWrites(page)).toHaveLength(0);
  });
}
