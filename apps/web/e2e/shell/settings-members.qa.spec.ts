// QA (09.7.1-T2, independent of the author): a boundary the real Chromium surface can observe on
// the Members invite dialog. The email field declares `maxlength="320"`; typing past it must stop at
// 320 characters, in both locales. Retry (503 then 201 with the same Idempotency-Key) and double
// submit are NOT checked here: the FIXTURES build answers through an Angular HttpBackend, so no
// network request exists for Playwright to intercept or count; those cases live in the Vitest
// `invite-dialog.qa.spec.ts` against the real store.
import { expect, test } from '@playwright/test';

import { LOCALES, membersPath, SETTINGS_ADMIN_GRANTS, withVariants } from './support';

const EMAIL_FIELD_LIMIT = 320;

for (const locale of LOCALES) {
  test(`the invite email field stops at its 320-character limit when typed past it (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
    await page.locator('app-members-page [data-members-invite-open]').click();

    const dialog = page.locator('[data-members-dialog="invite"][data-state="open"]');
    await expect(dialog).toBeVisible();

    const field = dialog.locator('input[type="email"]');
    // 337 keystrokes: the field must refuse the last 17, so the value is exactly the limit.
    await field.pressSequentially(`${'a'.repeat(330)}@x.test`);

    const value = await field.inputValue();
    expect(value.length).toBe(EMAIL_FIELD_LIMIT);
    expect(value).toBe('a'.repeat(EMAIL_FIELD_LIMIT));
  });

  test(`invite email corrections are localized, linked and focus the summary (${locale})`, async ({
    page,
  }) => {
    const requiredCorrection = locale === 'vi' ? 'Nhập địa chỉ email.' : 'Enter an email address.';
    const invalidCorrection =
      locale === 'vi' ? 'Nhập địa chỉ email hợp lệ.' : 'Enter a valid email address.';
    await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
    await page.locator('app-members-page [data-members-invite-open]').click();
    const dialog = page.locator('[data-members-dialog="invite"][data-state="open"]');
    const email = dialog.locator('input[type="email"]');
    const summary = dialog.locator('#members-invite-validation-summary');
    const inline = dialog.locator('app-field-feedback p[id]');
    await expect(dialog.locator('form')).toHaveAttribute('novalidate', '');
    await dialog.locator('[data-members-submit]').click();
    await expect(summary).toBeFocused();
    await expect(summary).toContainText(requiredCorrection);
    await expect(inline).toHaveText(requiredCorrection);
    await expect(email).toHaveAttribute('aria-invalid', 'true');
    const inlineId = await inline.getAttribute('id');
    if (!inlineId) throw new Error('Expected an invitation field correction id');
    await expect(email).toHaveAttribute('aria-describedby', inlineId);
    await summary.locator('a[href="#members-invite-email"]').click();
    await expect(email).toBeFocused();
    await email.fill('not-an-email');
    await expect(inline).toHaveText(invalidCorrection);
    await expect(summary).toContainText(invalidCorrection);
    await expect(email).toBeFocused();
    await email.fill('corrected@example.test');
    await expect(summary).toBeHidden();
    await expect(inline).toHaveCount(0);
    await expect(email).not.toHaveAttribute('aria-invalid', 'true');
    await expect(email).toBeFocused();
    await dialog.locator('footer button[type="button"]').click();
  });
}
