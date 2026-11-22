import { expect, test, type Page } from '@playwright/test';
import { MEMBER_ROWS, openMemberAction } from './member-controls';
import { rotateButton, visit as visitSecrets } from './settings-secrets.helpers';
import { localePath, membersPath, ORG_ACME, SETTINGS_ADMIN_GRANTS, withVariants } from './support';

async function expectGeometry(page: Page, width: number): Promise<void> {
  const content = page.locator('hlm-dialog-content[data-state="open"]');
  await expect(content).toBeVisible();
  await expect
    .poll(() => content.evaluate((el) => Math.round(el.getBoundingClientRect().width)))
    .toBe(width);
  if (width === 360) {
    await expect
      .poll(() =>
        content.evaluate((el) => {
          const rect = el.getBoundingClientRect();
          return (
            Math.abs(rect.left) <= 1 &&
            Math.abs(rect.top) <= 1 &&
            Math.abs(rect.bottom - window.innerHeight) <= 1 &&
            el.scrollWidth <= el.clientWidth + 1
          );
        }),
      )
      .toBe(true);
  }
}

for (const surface of ['member', 'token', 'secret'] as const) {
  test(`${surface} View and mutation dialogs use the desktop and mobile size contracts`, async ({
    page,
  }) => {
    const trigger = page
      .locator(
        {
          member: '[data-member-view]',
          token: '[data-api-inspect]',
          secret: '[data-secret-inspect]',
        }[surface],
      )
      .first();
    await page.setViewportSize({ width: 1440, height: 900 });
    if (surface === 'member') {
      await page.goto(withVariants(membersPath('en'), [SETTINGS_ADMIN_GRANTS]));
    } else if (surface === 'token') {
      await page.goto(
        `${localePath('en', `/dev/settings-api-tokens/orgs/${ORG_ACME}`)}?fixture.variant.listPermissions=settings-api-tokens-write`,
      );
    } else {
      await visitSecrets(page, 'en');
    }
    for (const width of [1440, 360]) {
      await page.setViewportSize({ width, height: 900 });
      await trigger.click();
      await expectGeometry(page, width === 1440 ? 560 : 360);
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog')).toHaveCount(0);
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    if (surface === 'member') {
      const row = page
        .locator(MEMBER_ROWS)
        .filter({ has: page.locator('[data-members-status="active"]') })
        .first();
      await openMemberAction(page, row, 'edit-open');
    } else if (surface === 'token') {
      await page.locator('[data-api-revoke]').first().click();
    } else {
      await rotateButton(page).click();
    }
    await expectGeometry(page, 760);
    await page.setViewportSize({ width: 360, height: 900 });
    await expectGeometry(page, 360);
  });
}
