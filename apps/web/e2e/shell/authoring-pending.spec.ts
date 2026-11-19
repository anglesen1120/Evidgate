import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import appliedFixture from '../../../../fixtures/web-api/authoring-pending-apply/input.json';
import {
  FIXTURE_REQUEST_LOG,
  type FixtureCallRecord,
} from '../../src/app/core/dev-fixtures/fixture-request-log';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariant,
} from './support';

const base = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/pending-changes`;
const COMMENT = '018f0000-0000-7000-8000-00000000cd01';
const DELETE = '018f0000-0000-7000-8000-00000000cd02';
const MERGE = '018f0000-0000-7000-8000-00000000cd03';
const SPLIT = '018f0000-0000-7000-8000-00000000cd04';
async function calls(page: Page): Promise<FixtureCallRecord[]> {
  return page.evaluate(
    (key) =>
      (window[key] ?? []).filter((record) =>
        new URL(record.url, location.origin).pathname.endsWith('/authoring/changes:apply'),
      ),
    FIXTURE_REQUEST_LOG,
  );
}
async function queuedIds(page: Page): Promise<string[]> {
  return page
    .locator('[data-pending-change-id]')
    .evaluateAll((elements) =>
      elements.map((element) => element.getAttribute('data-pending-change-id') ?? ''),
    );
}
async function keyboardTo(page: Page, target: Locator): Promise<void> {
  for (let step = 0; step < 160; step++) {
    if (await target.evaluate((element) => element === document.activeElement)) return;
    await page.keyboard.press('Tab');
  }
  await expect(target).toBeFocused();
}
async function selectSupported(page: Page): Promise<void> {
  for (const id of [MERGE, SPLIT]) {
    const box = page.getByRole('checkbox', { name: new RegExp(id) });
    await keyboardTo(page, box);
    await page.keyboard.press('Space');
    await expect(box).not.toBeChecked();
  }
}
for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const width of [360, 1280]) {
      test(`pending queue is readable and confirm-only (${locale}, ${theme}, ${width})`, async ({
        page,
      }) => {
        await page.setViewportSize({ width, height: 900 });
        await seedStoredTheme(page, theme);
        await page.goto(localePath(locale, base));
        await expect(page.locator('main h1')).toHaveCount(1);
        await expect(page.locator('[data-pending-preview]')).toHaveCount(4);
        const beforeIds = await queuedIds(page);
        expect(beforeIds).toEqual([COMMENT, DELETE, MERGE, SPLIT]);
        expect(await calls(page)).toEqual([]);
        const preview = page.locator('[data-pending-preview]').first();
        await keyboardTo(page, preview);
        await page.keyboard.press('Enter');
        await expect(page.locator('[data-pending-preview-content]')).toContainText(
          'Please confirm the refund wording with support.',
        );
        if (width === 360) {
          await expect(page.locator('[data-pending-apply]')).toHaveCount(0);
          await expect(page.locator('[data-pending-discard]')).toHaveCount(0);
          await expect(page.locator('#pending-local-reason')).toHaveCount(0);
          await expect(page.locator('app-desktop-required-notice')).toBeVisible();
        } else {
          await expect(page.locator('[data-pending-apply]')).toBeDisabled();
          await selectSupported(page);
          const apply = page.locator('[data-pending-apply]');
          await expect(apply).toBeEnabled();
          await keyboardTo(page, apply);
          await page.keyboard.press('Enter');
          await expect(page.locator('[data-pending-cancel]')).toBeFocused();
          const dialog = page.getByRole('alertdialog');
          await expect(dialog).toContainText(COMMENT);
          await expect(dialog).toContainText(DELETE);
          await expectNoA11yViolations(page, `pending consent ${locale} ${theme}`);
          expect(await calls(page)).toEqual([]);
          await page.keyboard.press('Escape');
          await expect(dialog).toHaveCount(0);
          await expect(apply).toBeFocused();
          await page.keyboard.press('Enter');
          await expect(page.locator('[data-pending-cancel]')).toBeFocused();
          await page.keyboard.press('Tab');
          await page.keyboard.press('Enter');
          await expect(page.locator('[data-pending-receipt]')).toBeVisible();
          await expect(page.locator('[data-pending-version]')).toHaveCount(1);
          const receipt = page.locator('[data-pending-receipt]');
          const returned = appliedFixture.body;
          expect(
            await receipt
              .locator('[data-pending-applied-id]')
              .evaluateAll((elements) =>
                elements.map((element) => element.getAttribute('data-pending-applied-id')),
              ),
          ).toEqual(returned.applied_change_ids);
          expect(
            await receipt.locator('[data-pending-version]').evaluateAll((elements) =>
              elements.map((element) => ({
                entity_id: element.getAttribute('data-pending-version-entity'),
                version_no: Number(element.getAttribute('data-pending-version-number')),
              })),
            ),
          ).toEqual(
            returned.versions.map(({ entity_id, version_no }) => ({ entity_id, version_no })),
          );
          expect(await queuedIds(page)).toEqual(
            beforeIds.filter((id) => !returned.applied_change_ids.includes(id)),
          );
          await expect(page.locator('[data-pending-preview]')).toHaveCount(2);
          const written = await calls(page);
          expect(written).toHaveLength(1);
          expect(written[0]?.headers['if-match']).toEqual(['"batch-7"']);
          expect(written[0]?.headers['idempotency-key']?.[0]).toBeTruthy();
          await expect(page.locator('[data-pending-receipt]')).toContainText(COMMENT);
          await expect(page.locator('[data-pending-receipt]')).toContainText(DELETE);
          await expect(page.locator('[data-pending-receipt]')).not.toContainText(MERGE);
          await expect(page.locator('[data-pending-receipt]')).not.toContainText(SPLIT);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
        await expectNoA11yViolations(page, `pending queue ${locale} ${theme} ${width}`);
      });
    }
  }
  test(`412 keeps every queued id and the reason until explicit reload (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(
      withVariant(localePath(locale, base), 'fixture.variant.applyAuthoringChanges=stale'),
    );
    await expect(page.locator('[data-pending-preview]')).toHaveCount(4);
    const beforeIds = await queuedIds(page);
    await page.locator('#pending-local-reason').fill('Keep my local reason');
    await selectSupported(page);
    await page.locator('[data-pending-apply]').click();
    await expect(page.locator('[data-pending-cancel]')).toBeFocused();
    await page.locator('[data-pending-confirm]').click();
    await expect(page.getByRole('dialog')).toBeVisible();
    expect(await calls(page)).toHaveLength(1);
    await expect(page.locator('[data-pending-preview]')).toHaveCount(4);
    expect(await queuedIds(page)).toEqual(beforeIds);
    await expect(page.locator('#pending-local-reason')).toHaveValue('Keep my local reason');
    await expect(page.locator('[data-pending-receipt]')).toHaveCount(0);
    await expectNoA11yViolations(page, `pending conflict ${locale}`);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(await calls(page)).toHaveLength(1);
  });
}
