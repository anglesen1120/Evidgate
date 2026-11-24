import { expect, test, type Page } from '@playwright/test';
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

/** Measure real field rows, not just the icon: the neighboring plain label must align too. */
async function checkRows(page: Page, ids: string[], minimum: number, sameRow = false) {
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
    expect(row.height, `${row.id} label row`).toBe(rows[0]!.height);
    expect(row.gap, `${row.id} label overlaps its control`).toBeGreaterThanOrEqual(0);
    if (sameRow) expect(row.inputTop, `${row.id} control top`).toBe(rows[0]!.inputTop);
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
  if (sameRow && rows.length > 1) expect(rows[1]!.inputLeft).toBeGreaterThan(rows[0]!.inputLeft);
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
      await checkRows(
        page,
        ['audit-actor', 'audit-action', 'audit-object', 'audit-from', 'audit-to'],
        minimum,
        desktop,
      );
    });

    test('aligns run filters and preserves the create dialog field rhythm', async ({ page }) => {
      await page.goto(withVariants(list, ['?fixture.variant.listPermissions=run-actions']));
      await openRunFilters(page);
      await checkRows(
        page,
        ['run-state', 'run-decision', 'run-scope', 'run-ref'],
        minimum,
        desktop,
      );
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
        await checkRows(page, ['retention-artifact', 'retention-metadata'], minimum, true);
      else {
        await expect(page.locator('[data-retention-current]')).toBeVisible();
        await expect(page.locator('app-retention-form form')).toHaveCount(0);
      }
    });

    test('keeps the Connect CI fields usable without exposing mutations on mobile', async ({
      page,
    }) => {
      await page.goto(onboardingUrl('vi', 'onboarding-project'));
      await expect(page.getByTestId('connect-ci-page')).toBeVisible();
      if (desktop) {
        await checkRows(
          page,
          ['connect-oidc-repo-id', 'connect-oidc-repo-name', 'connect-token-name'],
          minimum,
        );
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
