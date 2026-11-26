// 09.7.1-T3 — plan 09.7.1 §2 AC3 and delivery Notes :177, real Chromium / FIXTURES build.
// The G2 product tab stays absent in G1. Only the dev-routes file-replacement seam mounts this
// real page at /dev/settings-secrets/orgs/:orgId, under the normal shell/auth/org/read gates.
//
// Fixtures replace HttpBackend in-process: Playwright cannot intercept/count the rotation request
// or assert its headers. These cases observe the real consumer, SDK error mapping and UI only.
// The static list remains unchanged after a rotate, so focus returns to the still-connected opener;
// successor/fallback focus after a changed list and stale-response guards are Vitest's subject.
// The token corpus is one terminal page.
// Reminder dates are pinned with a browser clock, not compared to the machine's changing calendar.
// No source-text assertions or mock UI.
// Project changes during an overlay use the Helm picker's public DOM events: the focus trap
// prevents reaching the background picker, but a scope change must still clear the reveal.
// All selectors use component/data hooks, IDs or roles; localized text is asserted, never selected.
import { expect, test } from '@playwright/test';

import conflict from '../../../../fixtures/web-api/ingest-token-rotate-conflict/input.json';
import rateLimited from '../../../../fixtures/web-api/ingest-token-rotate-rate-limited/input.json';
import references from '../../../../fixtures/web-api/ingest-tokens-list-settings/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  LOCALES,
  PROJECT_CHECKOUT,
  SECRETS_ROTATE_CONFLICT,
  SECRETS_ROTATE_RATE_LIMITED,
  SECRETS_WRITE_GRANTS,
  secretsReviewPath,
  seedStoredTheme,
  SETTINGS_ADMIN_GRANTS,
  SETTINGS_NO_TAB_GRANTS,
  THEMES,
  UNKNOWN_ORG,
  withVariants,
} from './support';

import {
  PAGE,
  ROWS,
  DIALOG,
  SECRET,
  expectNoPlaintext,
  rotateButton,
  projectName,
  rotationConfirmation,
  approveRotation,
  ready,
  visit,
  reveal,
} from './settings-secrets.helpers';

const STATUS = {
  en: { active: 'Active', rotated: 'Rotated', revoked: 'Revoked' },
  vi: { active: 'Đang hoạt động', rotated: 'Đã xoay', revoked: 'Đã thu hồi' },
} as const;

for (const locale of LOCALES) {
  // AC3 / T3.1: every public metadata column, status and reminder is rendered; no current value.
  test(`metadata, status/reminders and terminal pagination (${locale})`, async ({ page }) => {
    await visit(page, locale);
    for (const [index, reference] of references.body.items.entries()) {
      const row = page.locator(ROWS).nth(index);
      await expect(row.getByRole('heading')).toContainText(reference.name);
      await row.locator('[data-secret-inspect]').click();
      const detail = page.getByRole('dialog', { name: reference.name, exact: true });
      await expect(detail.getByRole('heading')).toHaveText(reference.name);
      await expect(row.locator('code').filter({ hasText: reference.prefix })).toHaveText(
        reference.prefix,
      );
      await expect(detail.locator('dl > div').first().locator('code')).toHaveText(reference.scopes);
      await expect(
        row.locator('app-status-pill').first().locator('.status-pill__label'),
      ).toHaveText(STATUS[locale][reference.status as keyof (typeof STATUS)['en']]);
      const dates = [
        reference.expires_at,
        reference.overlap_ends_at,
        reference.last_used_at,
        reference.created_at,
      ].filter((date) => date !== null);
      await expect(detail.locator('time')).toHaveCount(dates.length);
      for (const date of new Set(dates)) {
        const times = detail.locator(`time[datetime="${date}"]`);
        await expect(times).toHaveCount(dates.filter((candidate) => candidate === date).length);
        for (const time of await times.all()) await expect(time).toContainText('UTC');
      }
      await page.keyboard.press('Escape');
      await expect(detail).not.toBeVisible();
      await expect(row.locator('[data-secret-inspect]')).toBeFocused();
      await expect(row.locator('[data-rotate]')).toHaveCount(reference.status === 'active' ? 1 : 0);
    }
    await expect(page.locator(ROWS).nth(0).locator('[data-rotation-reminder]')).toHaveCount(0);
    await expect(page.locator(ROWS).nth(1).locator('[data-reminder="expires-soon"]')).toBeVisible();
    await expect(page.locator(ROWS).nth(2).locator('[data-reminder="expired"]')).toBeVisible();
    await expect(page.locator(ROWS).nth(4).locator('[data-rotation-reminder]')).toHaveCount(0);
    await expect(page.locator('app-secret-reference-list app-data-table-pager button')).toHaveCount(
      0,
    );
    await expectNoPlaintext(page);
  });

  // AC3 / T3.2: permission-derived mutation absence (not merely disabled) while read stays usable.
  test(`read-only grant offers no Rotate (${locale})`, async ({ page }) => {
    await visit(page, locale, SETTINGS_ADMIN_GRANTS);
    await expect(page.locator(`${PAGE} [data-rotate]`)).toHaveCount(0);
    await expectNoPlaintext(page);
  });

  // AC3 / T3.2: a real clipboard write, explicit dismiss, then all browser-visible sinks are empty.
  test(`rotate, Copy, dismiss and metadata-only reload (${locale})`, async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await visit(page, locale);
    const dialog = await reveal(page);
    await dialog.getByTestId('reveal-copy').click();
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(SECRET);
    await expect(dialog.getByTestId('reveal-copy-status')).not.toBeEmpty();
    await dialog.getByTestId('reveal-dismiss').click();
    await expect(dialog).not.toBeVisible();
    await expect(page.getByTestId('reveal-value')).toHaveCount(0);
    await expect(rotateButton(page)).toBeFocused();
    await expectNoPlaintext(page);
    // Reload is a metadata-only resume, not a second reveal of the successful rotation response.
    await page.reload();
    await ready(page);
    await expect(page.locator(DIALOG)).not.toBeVisible();
    await expectNoPlaintext(page);
  });

  // Consent Escape cancels; reveal Escape preserves the irreplaceable value until explicit dismiss.
  test(`keyboard confirmation and reveal: trap, Copy, Escape, explicit dismiss (${locale})`, async ({
    page,
    context,
  }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await visit(page, locale);
    const opener = rotateButton(page);
    await opener.focus();
    await page.keyboard.press('Enter');
    const cancelled = await rotationConfirmation(page);
    await page.keyboard.press('Escape');
    await expect(cancelled).not.toBeVisible();
    await expect(opener).toBeFocused();
    await expectNoPlaintext(page);
    await page.keyboard.press('Enter');
    const confirmation = await rotationConfirmation(page);
    await page.keyboard.press('Tab');
    await expect(confirmation.locator('[data-rotation-confirm-submit]')).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(confirmation).not.toBeVisible();
    const dialog = page.locator(DIALOG);
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId('reveal-value')).toHaveValue(SECRET);
    const focusInside = () =>
      page.evaluate((selector) => {
        const modal = document.querySelector(selector);
        return modal !== null && modal.contains(document.activeElement);
      }, DIALOG);
    await expect.poll(focusInside).toBe(true);
    let reachedCopy = false;
    // Three interactive stops (value, Copy, dismiss); two full cycles prove the wrapping boundary.
    for (let stop = 0; stop < 8; stop++) {
      await page.keyboard.press('Tab');
      await expect.poll(focusInside).toBe(true);
      if (
        await dialog
          .getByTestId('reveal-copy')
          .evaluate((button) => button === document.activeElement)
      ) {
        reachedCopy = true;
        await page.keyboard.press('Enter');
        await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(SECRET);
      }
    }
    expect(reachedCopy, 'Copy must be reachable by Tab, not only pointer').toBe(true);
    await page.keyboard.press('Shift+Tab');
    await expect.poll(focusInside).toBe(true);
    await page.keyboard.press('Escape');
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId('reveal-value')).toHaveValue(SECRET);
    await expect.poll(focusInside).toBe(true);
    await dialog.getByTestId('reveal-dismiss').click();
    await expect(dialog).not.toBeVisible();
    await expect(opener).toBeFocused();
    await expect(page.getByTestId('reveal-value')).toHaveCount(0);
    await expectNoPlaintext(page);
  });

  // AC3 / delivery Notes P5: the exact server 409 is shown; refusal never opens a reveal.
  test(`rotate 409 is verbatim, with no reveal (${locale})`, async ({ page }) => {
    await visit(page, locale, SECRETS_WRITE_GRANTS, [SECRETS_ROTATE_CONFLICT]);
    await rotateButton(page).click();
    await approveRotation(page);
    const alert = page.locator(PAGE).getByRole('alert');
    await expect(alert.locator('p')).toHaveText(conflict.body.error.message);
    await expect(alert.locator('code')).toHaveText(conflict.body.error.code);
    await expect(page.locator(DIALOG)).not.toBeVisible();
    await expect(rotateButton(page)).toBeEnabled();
    await expectNoPlaintext(page);
  });

  // AC3 / delivery Notes P5: 429 is a real SDK-mapped refusal with Retry-After, not a fake success.
  test(`rotate 429 shows Retry-After and blocks early retry (${locale})`, async ({ page }) => {
    await visit(page, locale, SECRETS_WRITE_GRANTS, [SECRETS_ROTATE_RATE_LIMITED]);
    await rotateButton(page).click();
    await approveRotation(page);
    const alert = page.locator(PAGE).getByRole('alert');
    await expect(alert.locator('p')).toHaveText(rateLimited.body.error.message);
    await expect(alert.locator('code')).toHaveText(rateLimited.body.error.code);
    const notice = page.locator(`${PAGE} app-retry-after`);
    await expect(notice).toBeVisible();
    await expect(notice.getByRole('status')).toContainText('30');
    await expect(notice.locator('code')).toHaveText([
      rateLimited.body.error.code,
      rateLimited.body.error.request_id,
    ]);
    await expect(notice.getByRole('button')).toBeDisabled();
    await expect(rotateButton(page)).toBeDisabled();
    await expect(page.locator(DIALOG)).not.toBeVisible();
    await expectNoPlaintext(page);
  });

  // AC3 / delivery Notes P6: below 768px Settings is inspection-only, never a squeezed mutation UI.
  test(`360px is inspection-only (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await visit(page, locale);
    const narrowNotice = page.locator(`${PAGE} app-desktop-required-notice`);
    await expect(narrowNotice).toBeVisible();
    await expect(narrowNotice).not.toBeEmpty();
    await expect(page.locator(`${PAGE} [data-rotate]:visible`)).toHaveCount(0);
    // CSS-hidden controls must not be keyboard stops either.
    for (const button of await page.locator(`${PAGE} [data-rotate]`).all()) {
      await expect(button).toBeHidden();
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await expectNoPlaintext(page);
    await expectNoA11yViolations(page, `Secrets inspection-only ${locale} 360px`);
  });

  // A scope-change event must erase the reveal even while the overlay traps user focus.
  test(`project scope change closes and erases reveal (${locale})`, async ({ page }) => {
    await visit(page, locale);
    const dialog = await reveal(page);
    await page.locator('#secrets-project').evaluate((button: HTMLButtonElement) => button.click());
    await page
      .getByRole('option', {
        name: projectName(PROJECT_CHECKOUT),
        exact: true,
        includeHidden: true,
      })
      .evaluate((option: HTMLElement) => option.click());
    await expect(page.locator('#secrets-project')).toHaveText(projectName(PROJECT_CHECKOUT));
    await expect(page.locator('#secrets-project-value')).toHaveText(projectName(PROJECT_CHECKOUT));
    await expect(dialog).not.toBeVisible();
    await expect(page.getByTestId('reveal-value')).toHaveCount(0);
    await expectNoPlaintext(page);
  });

  // AC3 / AC1: dev review omits only the phase gate, never authentication/org/read authorization.
  test(`review route refuses missing read grant and foreign org (${locale})`, async ({ page }) => {
    await page.goto(withVariants(secretsReviewPath(locale), [SETTINGS_NO_TAB_GRANTS]));
    await expect(page.locator('app-forbidden-pattern')).toBeVisible();
    await expect(page.locator(PAGE)).toHaveCount(0);
    await page.goto(withVariants(secretsReviewPath(locale, UNKNOWN_ORG), [SECRETS_WRITE_GRANTS]));
    await expect(page.locator('app-not-found-pattern')).toBeVisible();
    await expect(page.locator(PAGE)).toHaveCount(0);
  });

  for (const theme of THEMES) {
    // Contrast is checked in both the metadata list and the portalled reveal.
    test(`axe has no violations on list and open reveal (${locale}, ${theme})`, async ({
      page,
    }) => {
      await seedStoredTheme(page, theme);
      await visit(page, locale);
      await expect(page.locator('html')).toHaveAttribute('lang', locale);
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      const scopePickers = page.getByRole('banner').getByRole('combobox');
      await expect(scopePickers).toHaveCount(2);
      for (const picker of await scopePickers.all()) {
        await expect(picker).toHaveAttribute('aria-expanded', 'false');
        await expect(picker).toHaveAccessibleName(/\S/u);
      }
      await expectNoA11yViolations(page, `Secrets list ${locale} ${theme}`);
      const dialog = await reveal(page, `Secrets rotation confirmation ${locale} ${theme}`);
      await expectNoA11yViolations(page, `Secrets open reveal ${locale} ${theme}`);
      await dialog.getByTestId('reveal-dismiss').click();
      await expect(dialog).not.toBeVisible();
      await expectNoPlaintext(page);
    });
  }
}
