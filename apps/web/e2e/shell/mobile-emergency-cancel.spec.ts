import { expect, test, type Locator, type Page } from '@playwright/test';
import collecting from '../../../../fixtures/web-api/run-detail-collecting/input.json';
import {
  FIXTURE_REQUEST_LOG,
  type FixtureCallRecord,
} from '../../src/app/core/dev-fixtures/fixture-request-log';
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
const MANIFEST = '018f0000-0000-7000-8000-000000000041';

function detailPath(locale: ShellLocale): string {
  return withVariants(
    localePath(
      locale,
      `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${collecting.body.run_id}`,
    ),
    ['?fixture.variant.listPermissions=run-actions', '?fixture.variant.getRun=detail-cancel-poll'],
  );
}

function listPath(locale: ShellLocale): string {
  return withVariants(
    localePath(locale, `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs`),
    ['?fixture.variant.listPermissions=run-actions', '?fixture.variant.createRun=ok'],
  );
}

async function requestsTo(
  page: Page,
  method: string,
  suffix: string,
): Promise<FixtureCallRecord[]> {
  return page.evaluate(
    ({ key, wanted, ending }) =>
      (window[key] ?? []).filter(
        (record) =>
          record.method === wanted &&
          new URL(record.url, location.origin).pathname.endsWith(ending),
      ),
    { key: FIXTURE_REQUEST_LOG, wanted: method, ending: suffix },
  );
}

async function expectTouchTarget(locator: Locator): Promise<void> {
  const box = await locator.boundingBox();
  expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
}

async function openCancelDialog(page: Page): Promise<Locator> {
  const trigger = page.locator('[data-run-cancel-trigger]');
  await trigger.focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('alertdialog');
  await expect(dialog).toBeVisible();
  // The dialog content zooms in on open; measure only after the animation settles.
  await expect
    .poll(() => dialog.evaluate((el) => el.getAnimations({ subtree: true }).length))
    .toBe(0);
  return dialog;
}

for (const locale of LOCALES) {
  test(`cancel is a 44px touch target that opens an alertdialog with Keep focused at 390px (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize(NARROW);
    await page.goto(detailPath(locale));
    const trigger = page.locator('[data-run-cancel-trigger]');
    await expect(trigger).toBeVisible();
    await expectTouchTarget(trigger);
    const dialog = await openCancelDialog(page);
    await expect(dialog.locator('[data-run-cancel-keep]')).toBeFocused();
    await expectTouchTarget(dialog.locator('[data-run-cancel-keep]'));
    await expectTouchTarget(dialog.locator('[data-run-cancel-confirm]'));
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
  });

  test(`Escape cancels the emergency dialog at 390px with zero cancel requests (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize(NARROW);
    await page.goto(detailPath(locale));
    const dialog = await openCancelDialog(page);
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(page.locator('[data-run-cancel-trigger]')).toBeFocused();
    expect(await requestsTo(page, 'POST', '/cancel')).toHaveLength(0);
  });

  test(`confirm sends exactly one keyed cancel request at 390px (${locale})`, async ({ page }) => {
    await page.setViewportSize(NARROW);
    await page.goto(detailPath(locale));
    const dialog = await openCancelDialog(page);
    await dialog.locator('[data-run-cancel-confirm]').click();
    await expect.poll(async () => (await requestsTo(page, 'POST', '/cancel')).length).toBe(1);
    const [sent] = await requestsTo(page, 'POST', '/cancel');
    expect(new URL(sent?.url ?? '', 'http://fixture.local').pathname).toMatch(
      new RegExp(`/runs/${collecting.body.run_id}/cancel$`),
    );
    expect(sent?.headers['idempotency-key']).toHaveLength(1);
    expect(sent?.headers['idempotency-key']?.[0]).toBeTruthy();
  });

  test(`resizing to 1280 mid-dialog keeps cancel working (${locale})`, async ({ page }) => {
    await page.setViewportSize(NARROW);
    await page.goto(detailPath(locale));
    const dialog = await openCancelDialog(page);
    await page.setViewportSize(WIDE);
    await expect(dialog.locator('[data-run-cancel-confirm]')).toBeEnabled();
    await dialog.locator('[data-run-cancel-confirm]').click();
    await expect.poll(async () => (await requestsTo(page, 'POST', '/cancel')).length).toBe(1);
  });

  test(`resizing 1280 to 390 mid-dialog sends nothing for a non-allowlisted create (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize(WIDE);
    await page.goto(listPath(locale));
    await page.locator('[data-run-create-trigger]').click();
    const dialog = page.locator('[data-run-create-dialog]');
    await expect(dialog).toBeVisible();
    await dialog.locator('#run-create-manifestId').fill(MANIFEST);
    const submit = dialog.locator('[data-run-create-submit]');
    await expect(submit).toBeEnabled();
    await page.setViewportSize(NARROW);
    await expect(submit).toBeDisabled();
    await page
      .locator('#run-create-form')
      .evaluate((form) => (form as HTMLFormElement).requestSubmit());
    expect(await requestsTo(page, 'POST', '/runs')).toHaveLength(0);
  });
}
