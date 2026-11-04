// 09.7.1-T3 — plan 09.7.1 §2 AC3 and delivery Notes :177, real Chromium / FIXTURES build.
// The G2 product tab stays absent in G1. Only the dev-routes file-replacement seam mounts this
// real page at /dev/settings-secrets/orgs/:orgId, under the normal shell/auth/org/read gates.
//
// Fixtures replace HttpBackend in-process: Playwright cannot intercept/count the rotation request
// or assert its headers. These cases observe the real consumer, SDK error mapping and UI only.
// The static list remains unchanged after a rotate, so focus returns to the still-connected opener;
// successor/fallback focus after a changed list and stale-response guards are Vitest's subject.
// The token corpus is six rows with next_cursor:null; that suite asserts the terminal pagination
// state. The CI-binding track is the one list whose corpus holds two cursor pages
// (09.7.1-F15 note 4), so ITS Load more is clicked here in a real browser: first page, append,
// terminal page, focus back on the section heading.
// Reminder dates are pinned with a browser clock, not compared to the machine's changing calendar.
// No source-text assertions or mock UI.
// Project changes during an overlay are injected as a DOM change event: the focus trap prevents
// reaching the background picker, but a scope-change event must still clear the reveal immediately.
// All selectors use component/data hooks, IDs or roles; localized text is asserted, never selected.
import { expect, test, type Locator, type Page } from '@playwright/test';

import rotation from '../../../../fixtures/web-api/ingest-token-rotate/input.json';
import conflict from '../../../../fixtures/web-api/ingest-token-rotate-conflict/input.json';
import rateLimited from '../../../../fixtures/web-api/ingest-token-rotate-rate-limited/input.json';
import references from '../../../../fixtures/web-api/ingest-tokens-list-settings/input.json';
import ciBindings from '../../../../fixtures/web-api/ci-bindings-list-settings/input.json';
import ciBindingsPageOne from '../../../../fixtures/web-api/ci-bindings-list-settings-page-1/input.json';
import ciBindingsPageTwo from '../../../../fixtures/web-api/ci-bindings-list-settings-page-2/input.json';
import {
  expectNoA11yViolations,
  LOCALES,
  PROJECT_CHECKOUT,
  PROJECT_STOREFRONT,
  SECRETS_LIST,
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
  waitForDialogAnimations,
  type ShellLocale,
} from './support';

const PAGE = 'app-secrets-page';
const ROWS = `${PAGE} [data-secret-reference]`;
const DIALOG = '[role="dialog"]';
const SECRET = rotation.body.secret;
const TARGET = ((): (typeof references.body.items)[number] => {
  const first = references.body.items[0];
  if (first === undefined) {
    throw new Error('fixtures/web-api/ingest-tokens-list-settings holds no reference');
  }
  return first;
})();
const STATUS = {
  en: { active: 'Active', rotated: 'Rotated', revoked: 'Revoked' },
  vi: { active: 'Đang hoạt động', rotated: 'Đã xoay', revoked: 'Đã thu hồi' },
} as const;

/** No plaintext in any template sink, control property, address or persistent browser store. */
async function expectNoPlaintext(page: Page): Promise<void> {
  const sinks = await page.evaluate(() => ({
    html: document.documentElement.innerHTML,
    controls: Array.from(document.querySelectorAll('input, textarea')).map(
      (element) => (element as HTMLInputElement | HTMLTextAreaElement).value,
    ),
    url: location.href,
    local: JSON.stringify(Object.entries(localStorage)),
    session: JSON.stringify(Object.entries(sessionStorage)),
  }));
  for (const [sink, value] of Object.entries(sinks)) {
    const text = JSON.stringify(value);
    expect(text, `${sink} must not retain the one-time credential`).not.toContain(SECRET);
    expect(text, `${sink} must not contain any fixture plaintext`).not.toMatch(
      /(?:egt|evgi)_FIXTURE_ONLY_/,
    );
  }
}

function rotateButton(page: Page): Locator {
  return page.locator(`${PAGE} [data-rotate][data-reference-id="${TARGET.id}"]`);
}

async function ready(page: Page): Promise<void> {
  await expect(page.locator(PAGE)).toBeVisible();
  await expect(page.locator(ROWS)).toHaveCount(references.body.items.length);
  await expect(page.locator('#secrets-project')).toHaveValue(PROJECT_STOREFRONT);
}

async function visit(
  page: Page,
  locale: ShellLocale,
  grants = SECRETS_WRITE_GRANTS,
  variants: readonly string[] = [],
): Promise<void> {
  await page.clock.setFixedTime(new Date('2026-10-25T12:00:00Z'));
  await page.goto(withVariants(secretsReviewPath(locale), [grants, SECRETS_LIST, ...variants]));
  await ready(page);
}

async function reveal(page: Page): Promise<Locator> {
  await expectNoPlaintext(page);
  await rotateButton(page).click();
  const dialog = page.locator(DIALOG);
  await expect(dialog).toBeVisible();
  await expect(dialog.getByTestId('reveal-value')).toHaveValue(SECRET);
  await waitForDialogAnimations(page, DIALOG);
  // Plaintext belongs to exactly one live reveal control, never to the metadata list or storage.
  await expect(page.getByTestId('reveal-value')).toHaveCount(1);
  await expect(page.locator('app-secret-reference-list')).not.toContainText(SECRET);
  expect(new URL(page.url()).search).not.toContain(SECRET);
  expect(
    await page.evaluate(() =>
      JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]),
    ),
  ).not.toContain(SECRET);
  return dialog;
}

for (const locale of LOCALES) {
  // AC3 / T3.1: every public metadata column, status and reminder is rendered; no current value.
  test(`metadata, status/reminders and terminal pagination (${locale})`, async ({ page }) => {
    await visit(page, locale);
    for (const [index, reference] of references.body.items.entries()) {
      const row = page.locator(ROWS).nth(index);
      await expect(row.getByRole('heading')).toHaveText(reference.name);
      await expect(row.locator('code').filter({ hasText: reference.prefix })).toHaveText(
        reference.prefix,
      );
      await expect(row.locator('dl > div').first().locator('code')).toHaveText(reference.scopes);
      await expect(
        row.locator('app-status-pill').first().locator('.status-pill__label'),
      ).toHaveText(STATUS[locale][reference.status as keyof (typeof STATUS)['en']]);
      for (const date of [
        reference.expires_at,
        reference.overlap_ends_at,
        reference.last_used_at,
        reference.created_at,
      ]) {
        if (date !== null) await expect(row.locator(`time[datetime="${date}"]`)).toHaveText(date);
      }
      await expect(row.locator('[data-rotate]')).toHaveCount(reference.status === 'active' ? 1 : 0);
    }
    await expect(page.locator(ROWS).nth(0).locator('[data-rotation-reminder]')).toHaveCount(0);
    await expect(page.locator(ROWS).nth(1).locator('[data-reminder="expires-soon"]')).toBeVisible();
    await expect(page.locator(ROWS).nth(2).locator('[data-reminder="expired"]')).toBeVisible();
    await expect(page.locator(ROWS).nth(4).locator('[data-rotation-reminder]')).toHaveCount(0);
    await expect(page.locator(`${PAGE} [data-load-more]`)).toHaveCount(0);
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

  // AC3 / T3.2 and keyboard a11y: Enter, initial focus, full wrap, Copy, Escape, focus restoration.
  test(`keyboard reveal: trap, Copy, Escape (${locale})`, async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await visit(page, locale);
    const opener = rotateButton(page);
    await opener.focus();
    await page.keyboard.press('Enter');
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
    await expect(dialog).not.toBeVisible();
    await expect(opener).toBeFocused();
    await expect(page.getByTestId('reveal-value')).toHaveCount(0);
    await expectNoPlaintext(page);
  });

  // AC3 / delivery Notes P5: the exact server 409 is shown; refusal never opens a reveal.
  test(`rotate 409 is verbatim, with no reveal (${locale})`, async ({ page }) => {
    await visit(page, locale, SECRETS_WRITE_GRANTS, [SECRETS_ROTATE_CONFLICT]);
    await rotateButton(page).click();
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
    const narrowNotice = page.locator(`${PAGE} > p[class~="md:hidden"]`);
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
    await page.locator('#secrets-project').evaluate((element, projectId) => {
      const select = element as HTMLSelectElement;
      select.value = projectId;
      select.dispatchEvent(new Event('change', { bubbles: true }));
    }, PROJECT_CHECKOUT);
    await expect(page.locator('#secrets-project')).toHaveValue(PROJECT_CHECKOUT);
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
      const dialog = await reveal(page);
      await expectNoA11yViolations(page, `Secrets open reveal ${locale} ${theme}`);
      await dialog.getByTestId('reveal-dismiss').click();
      await expect(dialog).not.toBeVisible();
      await expectNoPlaintext(page);
    });
  }
}

// 09.7.1-F15: the independent CI-binding track is list-only and metadata-only.
const CI_SECTION = `${PAGE} app-ci-binding-list`;
const CI_ROWS = `${CI_SECTION} [data-ci-binding]`;
const CI_STATUS = {
  en: { active: 'Active', pending_verification: 'Pending verification', disabled: 'Disabled' },
  vi: {
    active: 'Đang hoạt động',
    pending_verification: 'Chờ xác minh',
    disabled: 'Đã vô hiệu hóa',
  },
} as const;

async function expectNoBindingChallenge(page: Page): Promise<void> {
  await expectNoPlaintext(page);
  const sinks = await page.evaluate(() => [
    document.documentElement.innerHTML,
    ...Array.from(document.querySelectorAll('input, textarea')).map(
      (element) => (element as HTMLInputElement | HTMLTextAreaElement).value,
    ),
    location.href,
    JSON.stringify(Object.entries(localStorage)),
    JSON.stringify(Object.entries(sessionStorage)),
  ]);
  for (const sink of sinks) {
    expect(sink).not.toContain('binding_challenge');
    expect(sink).not.toContain('DO_NOT_RENDER_BINDING_CHALLENGE_DECOY');
  }
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const variant of ['settings', 'settings-empty', 'settings-forbidden'] as const) {
      test(`CI bindings ${variant} axe (${locale}, ${theme})`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await visit(page, locale, SETTINGS_ADMIN_GRANTS, [
          `?fixture.variant.listCiBindings=${variant}`,
        ]);
        const section = page.locator(CI_SECTION);
        await expect(section).toBeVisible();
        await expect(section.locator('section')).toHaveAttribute('aria-busy', 'false');
        await expect(page.locator('html')).toHaveAttribute('lang', locale);
        await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
        if (variant === 'settings') {
          await expect(page.locator(CI_ROWS)).toHaveCount(ciBindings.body.items.length);
          for (const [index, binding] of ciBindings.body.items.entries()) {
            const row = page.locator(CI_ROWS).nth(index);
            await expect(row.getByRole('heading')).toHaveText(binding.repository_full_name);
            await expect(row.getByRole('heading')).toHaveAttribute(
              'title',
              binding.repository_full_name,
            );
            await expect(row.locator('.status-pill__label')).toHaveText(
              CI_STATUS[locale][binding.status as keyof (typeof CI_STATUS)['en']],
            );
          }
        } else {
          await expect(
            section.locator(
              variant === 'settings-empty' ? '[data-ci-empty]' : '[data-ci-forbidden]',
            ),
          ).toBeVisible();
          await expect(page.locator(CI_ROWS)).toHaveCount(0);
          await expect(section.locator('button')).toHaveCount(0);
        }
        // Current fixture's next_cursor:null is terminal; no fabricated cursor/button.
        await expect(section.locator('[data-ci-load-more]')).toHaveCount(0);
        await expectNoBindingChallenge(page);
        await expectNoA11yViolations(page, `CI bindings ${variant} ${locale} ${theme}`);
      });
    }
  }

  test(`CI bindings 360px keyboard allowlists and no horizontal overflow (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await visit(page, locale, SETTINGS_ADMIN_GRANTS, ['?fixture.variant.listCiBindings=settings']);
    const section = page.locator(CI_SECTION);
    const toggles = section.locator('[data-ci-show-all]');
    await expect(toggles).toHaveCount(2);
    // Traverse actual Tab stops from the project picker to both overflow controls.
    await page.locator('#secrets-project').focus();
    await page.keyboard.press('Tab');
    await expect(toggles.nth(0)).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(toggles.nth(0)).toHaveAttribute('aria-expanded', 'true');
    const refsId = await toggles.nth(0).getAttribute('aria-controls');
    await expect(section.locator(`[id=\"${refsId}\"] > li`)).toHaveCount(8);
    await page.keyboard.press('Space');
    await expect(toggles.nth(0)).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('Tab');
    await expect(toggles.nth(1)).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(toggles.nth(1)).toHaveAttribute('aria-expanded', 'true');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await expectNoBindingChallenge(page);
    await expectNoA11yViolations(page, `CI bindings expanded ${locale} 360px`);
  });
}

// 09.7.1-F15: the corpus now holds a real two-page cursor walk for `listCiBindings`, so the
// section's Load more is exercised here in Chromium instead of only in the store and component
// specs. `…-page-1` names the cursor the fixture backend answers with `…-page-2`, whose
// `next_cursor` is null. The walk is locale-independent (the rows, the ids and the focus target are
// the same in both), so it runs once, in `en`; the axe and 360 px cases above already cover `vi`.
test('CI bindings Load more appends the cursor page and ends on the section heading (en)', async ({
  page,
}) => {
  const firstPageRows = ciBindingsPageOne.body.items;
  const secondPageRows = ciBindingsPageTwo.body.items;
  expect(
    ciBindingsPageOne.body.next_cursor,
    'the first page must name a cursor, or the control never renders',
  ).toBeTruthy();
  expect(ciBindingsPageTwo.body.next_cursor, 'the second page must end the walk').toBeNull();

  await visit(page, 'en', SETTINGS_ADMIN_GRANTS, [
    '?fixture.variant.listCiBindings=settings-page-1',
  ]);
  const section = page.locator(CI_SECTION);
  await expect(section.locator('section')).toHaveAttribute('aria-busy', 'false');
  await expect(page.locator(CI_ROWS)).toHaveCount(firstPageRows.length);
  await expect(page.locator(`${CI_ROWS} h3`)).toHaveText(
    firstPageRows.map((binding) => binding.repository_full_name),
  );

  // Keyboard: focus the control and press Enter, exactly as a keyboard user reaches it.
  const more = section.getByRole('button', { name: 'Load more CI bindings' });
  await expect(more).toHaveCount(1);
  await more.focus();
  await expect(more).toBeFocused();
  await page.keyboard.press('Enter');

  // The second page is appended, in order: page one is still there, page two follows it.
  await expect(page.locator(CI_ROWS)).toHaveCount(firstPageRows.length + secondPageRows.length);
  await expect(page.locator(`${CI_ROWS} h3`)).toHaveText(
    [...firstPageRows, ...secondPageRows].map((binding) => binding.repository_full_name),
  );

  // No row appears twice: the appended page must add rows, not re-render one of them. A row's id is
  // the one the header renders in its allowlist labels (`ci-label-<id>-refs`), so the ids read
  // straight off the DOM are the ids the store de-duplicated by.
  const rendered = await page.locator(CI_ROWS).evaluateAll((rows) =>
    rows.map((row) => ({
      name: row.querySelector('h3')?.textContent?.trim() ?? '',
      labelIds: Array.from(row.querySelectorAll('dt[id]')).map(
        (label) => label.getAttribute('id') ?? '',
      ),
    })),
  );
  const names = rendered.map((row) => row.name);
  expect(new Set(names).size, 'a repository name was rendered twice').toBe(names.length);
  const labelIds = rendered.flatMap((row) => row.labelIds);
  expect(new Set(labelIds).size, 'a row id was rendered twice').toBe(labelIds.length);
  for (const binding of [...firstPageRows, ...secondPageRows]) {
    expect(labelIds, `${binding.id} is missing from the rendered rows`).toContain(
      `ci-label-${binding.id}-refs`,
    );
  }

  // The last page removes the control; focus must not be dropped to the document, so the component
  // restores it to the section heading.
  await expect(section.locator('[data-ci-load-more]')).toHaveCount(0);
  await expect(section.locator('#ci-bindings-heading')).toBeFocused();
  await expect(section.locator('section')).toHaveAttribute('aria-busy', 'false');
  await expectNoBindingChallenge(page);
  await expectNoA11yViolations(page, 'CI bindings after Load more en');
});
