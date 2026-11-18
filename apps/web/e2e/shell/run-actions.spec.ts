import { expect, test } from '@playwright/test';
import collecting from '../../../../fixtures/web-api/run-detail-collecting/input.json';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
  type ShellLocale,
} from './support';

function path(locale: ShellLocale, run = 'detail-cancel-poll', grants = 'run-actions'): string {
  return withVariants(
    localePath(
      locale,
      `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${collecting.body.run_id}`,
    ),
    [`?fixture.variant.listPermissions=${grants}`, `?fixture.variant.getRun=${run}`],
  );
}

test.use({ viewport: { width: 1280, height: 900 } });

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test(`cancel records 202, then polls collection state (${locale}, ${theme})`, async ({
      page,
    }) => {
      await seedStoredTheme(page, theme);
      await page.goto(path(locale));
      const trigger = page.locator('[data-run-cancel-trigger]');
      await expect(trigger).not.toHaveAttribute('aria-disabled', 'true');
      await trigger.focus();
      await page.keyboard.press('Enter');
      const dialog = page.getByRole('alertdialog');
      await expect(dialog).toBeVisible();
      const keep = dialog.getByRole('button', {
        name: locale === 'vi' ? 'Giữ lượt chạy' : 'Keep run',
        exact: true,
      });
      await expect(keep).toBeFocused();
      await dialog
        .getByRole('textbox', {
          name: locale === 'vi' ? 'Lý do (không bắt buộc)' : 'Reason (optional)',
        })
        .fill('Deployment window changed');
      await dialog
        .getByRole('button', {
          name: locale === 'vi' ? 'Ghi nhận yêu cầu hủy' : 'Record cancel request',
          exact: true,
        })
        .click();
      await expect(dialog).toBeHidden();
      const status = page.locator('[data-run-cancel-status]');
      await expect(status.getByRole('status')).toContainText(
        locale === 'vi' ? 'Đang chờ trạng thái thu thập' : 'Waiting for collection state',
      );
      await expect(status).toContainText('202');
      await expect(trigger).toBeFocused();
      const collectionState = page.locator('app-run-header app-run-state-pill').first();
      await expect(collectionState).not.toContainText(locale === 'vi' ? 'Đã huỷ' : 'Canceled');
      await expect(collectionState).toContainText(locale === 'vi' ? 'Đã huỷ' : 'Canceled', {
        timeout: 10_000,
      });
      await expect(status.getByRole('status')).toContainText(
        locale === 'vi' ? 'có thể vẫn đang dừng lại' : 'execution may still be stopping',
      );
      await expect(trigger).toHaveAttribute('aria-disabled', 'true');
      await expect(
        page.getByText(locale === 'vi' ? 'Đã dừng' : 'Stopped', { exact: true }),
      ).toHaveCount(0);
    });
  }

  test(`cancel permission is denied without opening a dialog (${locale})`, async ({ page }) => {
    await page.goto(path(locale, 'detail-collecting', 'settings-policy-readonly'));
    const trigger = page.locator('[data-run-cancel-trigger]');
    await expect(trigger).toHaveAttribute('aria-disabled', 'true');
    await expect(page.locator('#run-cancel-disabled-reason')).toContainText('run:cancel');
    await trigger.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
  });

  for (const variant of ['detail-ready', 'detail-canceled']) {
    test(`terminal run does not offer another cancellation (${locale}, ${variant})`, async ({
      page,
    }) => {
      await page.goto(path(locale, variant));
      const trigger = page.locator('[data-run-cancel-trigger]');
      await expect(trigger).toHaveAttribute('aria-disabled', 'true');
      await expect(page.locator('#run-cancel-disabled-reason')).toContainText(
        locale === 'vi' ? 'đã ở trạng thái kết thúc' : 'already terminal',
      );
      await trigger.focus();
      await page.keyboard.press('Enter');
      await expect(page.getByRole('alertdialog')).toHaveCount(0);
    });
  }

  test(`narrow detail keeps cancellation desktop-only (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await page.goto(path(locale, 'detail-collecting'));
    const trigger = page.locator('[data-run-cancel-trigger]');
    await expect(trigger).toHaveAttribute('aria-disabled', 'true');
    await expect(page.locator('#run-cancel-disabled-reason')).toContainText(
      locale === 'vi' ? 'máy tính để bàn' : 'desktop-sized window',
    );
    await trigger.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
  });
}
