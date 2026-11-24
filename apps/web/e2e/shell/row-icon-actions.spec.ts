import { expect, test } from '@playwright/test';
import { LOCALES, ORG_ACME, PROJECT_STOREFRONT, localePath, seedStoredTheme } from './support';

const scope = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`;
const screens = [
  ['mapping-resolution', ''],
  ['studio-gallery', 'settings-policy-readonly'],
  ['generated-scripts', ''],
  ['deploy-targets', ''],
  ['release-decision', 'settings-policy-readonly'],
  ['agent-pools', ''],
  ['webhooks', ''],
  ['support-access', ''],
  ['run-list', 'settings-policy-readonly'],
] as const;

for (const locale of LOCALES) {
  for (const [screen, variant] of screens) {
    test(`${screen}: contextual icon actions keep native semantics (${locale})`, async ({
      page,
    }) => {
      const query = variant ? `?fixture.variant.listPermissions=${variant}` : '';
      await page.goto(localePath(locale, `/dev/${screen}${scope}${query}`));
      const actions = page.locator('button[appRowAction]:visible, a[appRowAction]:visible');
      await expect(actions.first()).toBeVisible();
      for (const control of await actions.all()) {
        const label = await control.getAttribute('aria-label');
        expect(label?.trim()).toBeTruthy();
        const role = (await control.getAttribute('href')) === null ? 'button' : 'link';
        await expect(page.getByRole(role, { name: label!, exact: true }).first()).toBeVisible();
        await expect(control).toHaveText('');
        await expect(control.locator('hlm-icon')).toHaveAttribute('aria-hidden', 'true');
        const box = await control.boundingBox();
        expect(box!.width).toBeGreaterThanOrEqual(24);
        expect(box!.height).toBeGreaterThanOrEqual(24);
      }
      const trigger = actions.filter({ hasNot: page.locator(':disabled') }).first();
      if (await trigger.isEnabled()) {
        await trigger.focus();
        await expect(trigger).toBeFocused();
        await expect(page.getByRole('tooltip')).toHaveText(
          (await trigger.getAttribute('aria-label'))!,
        );
      }
    });
  }

  test(`gallery directory uses the same contextual icon controls (${locale})`, async ({ page }) => {
    await page.goto(localePath(locale, '/dev/ui-gallery'));
    const directory = page.locator('app-gallery-member-directory');
    const view = directory.getByRole('button', { name: /linh@example\.test/ }).first();
    await expect(view).toHaveText('');
    await expect(view.locator('hlm-icon')).toHaveAttribute('aria-hidden', 'true');
    await view.click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(view).toBeFocused();
  });
}

test('captures row icons in both themes, narrow layout and Vietnamese', async ({ page }) => {
  for (const [locale, theme, width, name] of [
    ['en', 'light', 1440, '1440-light'],
    ['en', 'dark', 1440, '1440-dark'],
    ['en', 'light', 390, '390'],
    ['vi', 'light', 1440, 'vi'],
  ] as const) {
    await seedStoredTheme(page, theme);
    await page.setViewportSize({ width, height: width === 390 ? 844 : 1100 });
    await page.goto(
      localePath(
        locale,
        `/dev/run-list${scope}?fixture.variant.listPermissions=settings-policy-readonly`,
      ),
    );
    await expect(page.locator('button[appRowAction]:visible').first()).toBeVisible();
    await page.screenshot({ path: `/tmp/s676-shots/pr3-after-${name}.png`, fullPage: true });
  }
});
