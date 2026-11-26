import { expect, test, type Page } from '@playwright/test';
import { openConnectStep } from './onboarding-connection-support';
import { onboardingUrl } from './onboarding-support';
import { openRunFilters } from './run-list.support';
import {
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  SETTINGS_AUDIT_ONLY_GRANTS,
  settingsTabPath,
  withVariants,
} from './support';

/**
 * Measure real field rows, not just the icon: the neighboring plain label must align too.
 * `row` aligns control tops on one line; `stack` aligns lefts for a popover's single column.
 */
async function checkRows(
  page: Page,
  ids: string[],
  minimum: number,
  layout: 'row' | 'stack' | 'free' = 'free',
) {
  await expect(page.locator(`#${ids[0]}`)).toBeVisible();
  const rows = await page.evaluate(async (ids) => {
    await document.fonts.ready;
    return ids.map((id) => {
      const label = document.querySelector(`label[for="${id}"]`);
      const control = document.getElementById(id);
      if (!label || !control) throw new Error(`Missing label or control for ${id}`);
      const hint = label.closest('app-field-hint');
      const row = (hint ?? label).getBoundingClientRect();
      const target = hint?.querySelector('button')?.getBoundingClientRect();
      const input = control.getBoundingClientRect();
      return {
        id,
        height: row.height,
        inputTop: input.top,
        inputLeft: input.left,
        gap: input.top - row.bottom,
        target: target
          ? { width: target.width, height: target.height, bottom: target.bottom }
          : null,
      };
    });
  }, ids);
  expect(rows.some((row) => row.target !== null)).toBe(true);
  for (const row of rows) {
    expect(row.height, `${row.id} label row`).toBeCloseTo(rows[0]!.height, 1);
    expect(row.gap, `${row.id} label overlaps its control`).toBeGreaterThanOrEqual(0);
    if (layout === 'row') expect(row.inputTop, `${row.id} control top`).toBe(rows[0]!.inputTop);
    if (row.target) {
      expect(
        Math.min(row.target.width, row.target.height),
        `${row.id} hit area`,
      ).toBeGreaterThanOrEqual(minimum);
      expect(row.target.bottom, `${row.id} hit area overlaps control`).toBeLessThanOrEqual(
        row.inputTop,
      );
    }
  }
  if (layout === 'row' && rows.length > 1) {
    expect(rows[1]!.inputLeft).toBeGreaterThan(rows[0]!.inputLeft);
  }
  if (layout === 'stack') {
    for (const row of rows)
      expect(row.inputLeft, `${row.id} control left`).toBe(rows[0]!.inputLeft);
    for (let i = 1; i < rows.length; i++) {
      expect(rows[i]!.inputTop, `${rows[i]!.id} stacks below`).toBeGreaterThan(
        rows[i - 1]!.inputTop,
      );
    }
  }
}

/** Inline search inputs have sr-only labels and no hint row, so measure the control box. */
async function checkInline(page: Page, selector: string, minimum: number) {
  const box = await page.locator(selector).boundingBox();
  expect(box, `${selector} rendered`).not.toBeNull();
  expect(Math.min(box!.width, box!.height), `${selector} hit area`).toBeGreaterThanOrEqual(minimum);
}

const list = localePath(
  'vi',
  `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs`,
);

for (const width of [1440, 390]) {
  test.describe(`field hint layout at ${width}px`, () => {
    test.use({ viewport: { width, height: 900 }, hasTouch: width === 390 });
    const desktop = width === 1440;
    const minimum = desktop ? 24 : 44;

    test('aligns audit filters with and without guidance', async ({ page }) => {
      await page.goto(withVariants(settingsTabPath('vi', 'audit'), [SETTINGS_AUDIT_ONLY_GRANTS]));
      await page.locator('button[aria-controls="audit-filter-panel"]').click();
      await expect(page.locator('#audit-filter-panel')).toBeVisible();
      await checkRows(
        page,
        ['audit-actor', 'audit-object', 'audit-from', 'audit-to'],
        minimum,
        'stack',
      );
      await checkInline(page, '#audit-action', minimum);
    });

    test('aligns run filters and preserves the create dialog field rhythm', async ({ page }) => {
      await page.goto(withVariants(list, ['?fixture.variant.listPermissions=run-actions']));
      await openRunFilters(page);
      await checkRows(page, ['run-state', 'run-decision', 'run-scope'], minimum, 'stack');
      await checkInline(page, '#run-ref', minimum);
      const trigger = page.locator('[data-run-create-trigger]');
      if (desktop) {
        await trigger.click();
        await checkRows(
          page,
          ['run-create-manifestId', 'run-create-releaseId', 'run-create-purpose'],
          minimum,
        );
      } else {
        await expect(trigger).toHaveCount(0);
      }
    });

    test('keeps retention inputs aligned, or the policy read-only on narrow screens', async ({
      page,
    }) => {
      await page.goto(
        withVariants(settingsTabPath('vi', 'quota-retention'), [
          '?fixture.variant.listPermissions=settings-retention-write',
        ]),
      );
      if (desktop)
        await checkRows(page, ['retention-artifact', 'retention-metadata'], minimum, 'row');
      else {
        await expect(page.locator('[data-retention-current]')).toBeVisible();
        await expect(page.locator('app-retention-form form')).toHaveCount(0);
      }
    });

    test('keeps the token field usable without exposing mutations on mobile', async ({ page }) => {
      await page.goto(onboardingUrl('vi', 'onboarding-project'));
      await openConnectStep(page);
      await expect(page.getByTestId('connect-ci-page')).toBeVisible();
      if (desktop) {
        await checkRows(page, ['connect-token-name'], minimum);
      } else {
        await expect(
          page.locator('app-onboarding-connect-ci app-desktop-required-notice').first(),
        ).toBeVisible();
        await expect(page.locator('app-onboarding-connect-ci form')).toHaveCount(0);
      }
    });

    test('keeps the gallery hint target above its input', async ({ page }) => {
      await page.goto(localePath('vi', '/dev/ui-gallery'));
      await checkRows(page, ['gallery-specimen-project-key'], minimum);
    });
  });
}
