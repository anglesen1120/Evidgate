import { expect, test, type Page } from '@playwright/test';
import listing from '../../../../fixtures/web-api/api-tokens-list/input.json';
import creation from '../../../../fixtures/web-api/api-token-create/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  LOCALES,
  ORG_ACME,
  THEMES,
  localePath,
  seedStoredTheme,
  waitForDialogAnimations,
  type ShellLocale,
} from './support';

const PAGE = 'app-api-tokens-page';
const DIALOG = '[data-api-create-dialog]';
const VALUE = '[data-testid="reveal-value"]';

async function visit(page: Page, locale: ShellLocale, writable = true): Promise<void> {
  const variant = writable ? 'settings-api-tokens-write' : 'settings-api-tokens-read';
  await page.clock.setFixedTime(new Date('2026-11-06T12:00:00Z'));
  await page.goto(
    `${localePath(locale, `/dev/settings-api-tokens/orgs/${ORG_ACME}`)}?fixture.variant.listPermissions=${variant}`,
  );
  await expect(page.locator(`${PAGE} [data-api-token-row]`)).toHaveCount(listing.body.items.length);
}

async function expectNoCredential(page: Page): Promise<void> {
  const sinks = await page.evaluate(() => ({
    html: document.documentElement.innerHTML,
    values: Array.from(document.querySelectorAll<HTMLInputElement>('input')).map(
      (input) => input.value,
    ),
    location: location.href,
    local: JSON.stringify(Object.entries(localStorage)),
    session: JSON.stringify(Object.entries(sessionStorage)),
  }));
  expect(JSON.stringify(sinks)).not.toContain(creation.body.token);
}

async function reveal(page: Page): Promise<void> {
  await page.locator(`${PAGE} [data-api-create]`).click();
  await expect(page.locator(DIALOG)).toBeVisible();
  await page.locator('#api-create-name').fill('Dashboard reader');
  await expect(page.locator('[data-api-create-submit]')).toBeDisabled();
  await page.locator('#api-create-expiry').fill('2027-01-01T00:00');
  await page.locator('button[id="api-permission-run:read"]').click();
  await page.locator('[data-api-create-submit]').click();
  await expect(page.locator(VALUE)).toHaveValue(creation.body.token);
  await waitForDialogAnimations(page, DIALOG);
}

for (const locale of LOCALES) {
  test.describe(`API tokens ${locale}`, () => {
    test('renders served metadata; a read-only member cannot create or revoke', async ({
      page,
    }) => {
      await visit(page, locale, false);
      for (const row of listing.body.items) {
        const item = page.locator(`${PAGE} [data-api-token-row][data-state="${row.status}"]`);
        await expect(item).toContainText(row.name);
        await expect(item).toContainText(row.prefix);
        await expect(item).toContainText(row.permissions[0]!);
      }
      await expect(page.locator(`${PAGE} [data-api-create]`)).toHaveCount(0);
      await expect(page.locator(`${PAGE} [data-api-revoke]`)).toHaveCount(0);
      await expectNoCredential(page);
    });

    test('reveals once, protects Escape/outside clicks, and explicitly clears without persistence', async ({
      page,
    }) => {
      await visit(page, locale);
      await expectNoCredential(page);
      await reveal(page);
      await expect(page.locator('button[id="api-permission-api_token:write"]')).toHaveCount(0);
      await page.keyboard.press('Escape');
      await expect(page.locator(VALUE)).toHaveValue(creation.body.token);
      await page.locator('body').click({ position: { x: 2, y: 2 }, force: true });
      await expect(page.locator(VALUE)).toHaveValue(creation.body.token);
      await page.getByTestId('reveal-copy').click();
      await expect(page.getByTestId('reveal-copy-status')).not.toBeEmpty();
      await page.getByTestId('reveal-dismiss').click();
      await expect(page.locator(DIALOG)).toHaveCount(0);
      await expect(page.locator(`${PAGE} [data-api-create]`)).toBeFocused();
      await expectNoCredential(page);
      await page.reload();
      await expect(page.locator(`${PAGE} [data-api-token-row]`)).toHaveCount(
        listing.body.items.length,
      );
      await expectNoCredential(page);
    });

    test('opens and cancels revoke from the keyboard without sending a DELETE', async ({
      page,
    }) => {
      const deletes: string[] = [];
      page.on('request', (request) => {
        if (request.method() === 'DELETE') deletes.push(request.url());
      });
      await visit(page, locale);
      const action = page.locator(`${PAGE} [data-api-revoke]`).first();
      await action.focus();
      await page.keyboard.press('Enter');
      await expect(page.locator('[data-api-revoke-dialog]')).toBeVisible();
      const dialog = page.getByRole('alertdialog');
      await expect(dialog).toHaveAccessibleName(/\S/u);
      await expect(dialog).toHaveAttribute('aria-modal', 'true');
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await waitForDialogAnimations(page, '[data-api-revoke-dialog]');
      await expect(page.locator('[data-api-revoke-cancel]')).toBeFocused();
      for (let index = 0; index < 5; index++) {
        await page.keyboard.press('Tab');
        expect(
          await page
            .locator('[data-api-revoke-dialog]')
            .evaluate((dialog) => dialog.contains(document.activeElement)),
        ).toBe(true);
      }
      await page.keyboard.press('Escape');
      await expect(page.locator('[data-api-revoke-dialog]')).toHaveCount(0);
      await expect(action).toBeFocused();
      expect(deletes).toEqual([]);
    });

    test('navigation clears a live reveal instead of restoring it on return', async ({ page }) => {
      await visit(page, locale);
      await reveal(page);
      await page.goto(localePath(locale, `/orgs/${ORG_ACME}`));
      await expect(page.locator(VALUE)).toHaveCount(0);
      await expectNoCredential(page);
      await visit(page, locale);
      await expectNoCredential(page);
    });

    for (const theme of THEMES) {
      test(`list, form, reveal and revoke pass axe in ${theme}`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await visit(page, locale);
        await expectNoA11yViolations(page, `API tokens ${locale}/${theme} list`);
        await page.locator(`${PAGE} [data-api-create]`).click();
        await expect(page.locator(DIALOG)).toBeVisible();
        await waitForDialogAnimations(page, DIALOG);
        await expectNoA11yViolations(page, `API tokens ${locale}/${theme} form`);
        await page.keyboard.press('Escape');
        await expect(page.locator(DIALOG)).toHaveCount(0);
        await reveal(page);
        await expectNoA11yViolations(page, `API tokens ${locale}/${theme} reveal`);
        await page.getByTestId('reveal-dismiss').click();
        await expect(page.locator(DIALOG)).toHaveCount(0);
        await page.locator(`${PAGE} [data-api-revoke]`).first().click();
        await expect(page.locator('[data-api-revoke-dialog]')).toBeVisible();
        await waitForDialogAnimations(page, '[data-api-revoke-dialog]');
        await expectNoA11yViolations(page, `API tokens ${locale}/${theme} revoke`);
      });
    }

    test('keeps metadata readable without write controls at mobile width', async ({ page }) => {
      await page.setViewportSize({ width: 375, height: 812 });
      await visit(page, locale);
      await expect(page.locator(`${PAGE} [data-api-create]`)).toBeHidden();
      await expect(page.locator(`${PAGE} [data-api-revoke]`)).toBeHidden();
      expect(
        await page.locator(PAGE).evaluate((element) => element.scrollWidth <= element.clientWidth),
      ).toBe(true);
    });
  });
}
