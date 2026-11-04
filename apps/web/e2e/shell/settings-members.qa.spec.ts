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
}
