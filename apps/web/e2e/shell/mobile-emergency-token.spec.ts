import { expect, test, type Page } from '@playwright/test';
import listing from '../../../../fixtures/web-api/api-tokens-list/input.json';
import type { FixtureCallRecord } from '../../src/app/core/dev-fixtures/fixture-request-log';
import { ORG_ACME, localePath, waitForDialogAnimations } from './support';

const PAGE = 'app-api-tokens-page';
const REVOKE_DIALOG = '[data-api-revoke-dialog]';
const CREATE_DIALOG = '[data-api-create-dialog]';
const MOBILE = { width: 390, height: 844 };
const DESKTOP = { width: 1280, height: 800 };
const TOUCH_TARGET = 44;

const active = listing.body.items.find((item) => item.status === 'active');
if (!active) throw new Error('fixture must contain an active token');

async function visit(page: Page): Promise<void> {
  await page.setViewportSize(MOBILE);
  await page.clock.setFixedTime(new Date('2026-11-06T12:00:00Z'));
  await page.goto(
    `${localePath('en', `/dev/settings-api-tokens/orgs/${ORG_ACME}`)}?fixture.variant.listPermissions=settings-api-tokens-write`,
  );
  await expect(page.locator(`${PAGE} [data-api-token-row]`)).toHaveCount(listing.body.items.length);
}

// The fixtures build answers the API in process, so `page.on('request')` sees nothing. Read the
// backend's own log instead, filtered to one method.
async function fixtureCalls(page: Page, method: string): Promise<FixtureCallRecord[]> {
  return page.evaluate((wanted) => {
    const log = window.__evidgateFixtureRequests;
    if (!log) throw new Error('fixture request log missing: the backend recorded no requests');
    return log.filter((call) => call.method === wanted);
  }, method);
}

async function expectNoHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
}

async function openRevoke(page: Page): Promise<void> {
  await page.locator(`${PAGE} [data-api-revoke]`).first().click();
  await expect(page.locator(REVOKE_DIALOG)).toBeVisible();
  await waitForDialogAnimations(page, REVOKE_DIALOG);
}

test.describe('mobile emergency: revoke API token at 390px', () => {
  test('revoke is a 44px touch target and create stays blocked with its notice', async ({
    page,
  }) => {
    await visit(page);
    const revoke = page.locator(`${PAGE} [data-api-revoke]`).first();
    await expect(revoke).toBeEnabled();
    const box = await revoke.boundingBox();
    expect(box?.width).toBeGreaterThanOrEqual(TOUCH_TARGET);
    expect(box?.height).toBeGreaterThanOrEqual(TOUCH_TARGET);

    await expect(page.locator(`${PAGE} [data-api-create]`)).toHaveCount(0);
    await expect(page.locator('app-desktop-required-notice')).toBeVisible();
    await expectNoHorizontalScroll(page);
  });

  test('dialog is an alertdialog, Cancel is focused, and Escape sends no request', async ({
    page,
  }) => {
    await visit(page);
    await openRevoke(page);

    await expect(page.getByRole('alertdialog')).toBeVisible();
    await expect(page.locator('[data-api-revoke-cancel]')).toBeFocused();
    await expect(page.locator(REVOKE_DIALOG)).toContainText(active.name);

    for (const control of ['[data-api-revoke-cancel]', '[data-api-revoke-submit]']) {
      const box = await page.locator(control).boundingBox();
      expect(box?.height).toBeGreaterThanOrEqual(TOUCH_TARGET);
    }

    await page.keyboard.press('Escape');
    await expect(page.locator(REVOKE_DIALOG)).toHaveCount(0);
    expect(await fixtureCalls(page, 'DELETE')).toEqual([]);
  });

  test('confirm sends exactly one DELETE with an Idempotency-Key', async ({ page }) => {
    await visit(page);
    await openRevoke(page);

    await page.locator('[data-api-revoke-submit]').click();
    await expect(page.locator(REVOKE_DIALOG)).toHaveCount(0);

    const deletes = await fixtureCalls(page, 'DELETE');
    expect(deletes).toHaveLength(1);
    const [request] = deletes;
    expect(
      new URL(request!.url, 'http://fixture.local').pathname.endsWith(`/api-tokens/${active.id}`),
    ).toBe(true);
    expect(request!.headers['idempotency-key']?.[0]).toBeTruthy();
  });

  test('resizing to desktop mid-dialog still lets confirm send one DELETE', async ({ page }) => {
    await visit(page);
    await openRevoke(page);

    await page.setViewportSize(DESKTOP);
    await page.locator('[data-api-revoke-submit]').click();
    await expect(page.locator(REVOKE_DIALOG)).toHaveCount(0);
    expect(await fixtureCalls(page, 'DELETE')).toHaveLength(1);
  });

  test('resizing from desktop to 390 mid-dialog sends nothing for a non-allowlisted action', async ({
    page,
  }) => {
    await page.setViewportSize(DESKTOP);
    await page.clock.setFixedTime(new Date('2026-11-06T12:00:00Z'));
    await page.goto(
      `${localePath('en', `/dev/settings-api-tokens/orgs/${ORG_ACME}`)}?fixture.variant.listPermissions=settings-api-tokens-write`,
    );
    await expect(page.locator(`${PAGE} [data-api-token-row]`)).toHaveCount(
      listing.body.items.length,
    );

    await page.locator(`${PAGE} [data-api-create]`).click();
    await expect(page.locator(CREATE_DIALOG)).toBeVisible();
    await page.locator('#api-create-name').fill('Dashboard reader');
    await page.locator('#api-create-expiry').fill('2027-01-01T00:00');
    await page.locator('button[id="api-permission-run:read"]').click();
    await expect(page.locator('[data-api-create-submit]')).toBeEnabled();

    await page.setViewportSize(MOBILE);
    await expect(page.locator('[data-api-create-submit]')).toBeDisabled();
    await page.locator('[data-api-create-submit]').click({ force: true });
    expect(await fixtureCalls(page, 'POST')).toEqual([]);
    expect(await fixtureCalls(page, 'DELETE')).toEqual([]);
  });
});
