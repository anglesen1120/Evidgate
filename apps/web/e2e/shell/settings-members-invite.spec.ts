// 09.7.1-F12; plan 09.7.1 §2 AC1/AC2/AC3 and the T3 delivery amendment at §10 :177.
// Real Chromium, FIXTURES build: these cases exercise the page and Helm dialogs, not component
// internals. FixtureHttpBackend answers in process, so Playwright cannot intercept/count requests
// or inspect Idempotency-Key. Replay and refusals are separate corpus variants, never inferred
// from clicking twice. Existing data-* hooks and native semantics work in both locale bundles.
//
// A static list does not mutate after a write. Cancel's 204 can prove confirmation, dismissal and
// focus recovery here, but not a changed server list. cancel-invite-dialog.component.spec.ts:91
// pins the re-read; members.store.resend-cancel-remove.spec.ts:204 pins the reloaded row becoming revoked (audit history,
// not deletion). No fake optimistic removal makes a static corpus look stateful.
//
// Native showModal makes the background scope switcher inert while revealing. In addition, the
// corpus's only alternate org (Lumen) is suspended, so selecting it renders /forbidden, not another
// active org. The history cases cover navigation cleanup only: select Lumen, Back, reveal, Forward.
// An active-org switch during reveal cannot be claimed from these fixtures.
import { expect, test, type Locator, type Page } from '@playwright/test';
import { chooseScope, expectScopeValue, scopeControl } from './scope-controls';

import createOk from '../../../../fixtures/web-api/membership-create-ok/input.json';
import cancelNotPending from '../../../../fixtures/web-api/membership-cancel-not-pending/input.json';
import resendNotPending from '../../../../fixtures/web-api/membership-resend-not-pending/input.json';
import resendOk from '../../../../fixtures/web-api/membership-resend-ok/input.json';
import membershipsList from '../../../../fixtures/web-api/memberships-list/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  LOCALES,
  MEMBERS_CANCEL_NOT_PENDING,
  MEMBERS_CANCEL_OK,
  MEMBERS_CREATE_REPLAY,
  MEMBERS_READONLY_GRANTS,
  MEMBERS_RESEND_NOT_PENDING,
  MEMBERS_RESEND_OK,
  MEMBERS_RESEND_REPLAY,
  membersPath,
  ORG_ACME,
  ORG_LUMEN,
  seedStoredTheme,
  SETTINGS_ADMIN_GRANTS,
  THEMES,
  waitForDialogAnimations,
  withVariants,
  type ShellLocale,
} from './support';

const INVITE = '[data-members-dialog="invite"][data-state="open"]';
const RESEND = '[data-members-dialog="resend"][data-state="open"]';
const CANCEL = '[data-members-dialog="cancel-invite"][data-state="open"]';
const ROWS = 'app-members-page [data-members-row]';
const CREATE_TOKEN = createOk.body.invite_token;
const RESEND_TOKEN = resendOk.body.invite_token;
const TOKENS = [CREATE_TOKEN, RESEND_TOKEN];
const PENDING = membershipsList.body.items.find((member) => member.status === 'pending');
if (PENDING === undefined) throw new Error('memberships-list must contain a pending invitation');
const PENDING_ID = PENDING.id;
const PENDING_EMAIL = PENDING.invited_email;

type RevealFlow = 'invite' | 'resend';

/** Visits the real G1 Members tab, with only the fixture answers this case needs changed. */
async function openMembers(
  page: Page,
  locale: ShellLocale,
  variants: readonly string[] = [],
): Promise<void> {
  await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS, ...variants]));
  await expect(page.locator('app-members-page')).toHaveAttribute('data-members-phase', 'ready');
  await expect(page.locator(ROWS)).toHaveCount(membershipsList.body.items.length);
}

function pendingRow(page: Page): Locator {
  return page.locator(`${ROWS}[data-membership-id="${PENDING_ID}"]`);
}

function openerFor(page: Page, flow: RevealFlow): Locator {
  return flow === 'invite'
    ? page.locator('app-members-page [data-members-invite-open]')
    : pendingRow(page).locator('[data-members-resend-open]');
}

function dialogFor(page: Page, flow: RevealFlow): Locator {
  return page.locator(flow === 'invite' ? INVITE : RESEND);
}

/** Starts through user-facing controls; the token is always the corpus's full raw answer. */
async function issueInvite(page: Page, flow: RevealFlow): Promise<Locator> {
  await openerFor(page, flow).click();
  const dialog = dialogFor(page, flow);
  await expect(dialog).toBeVisible();
  if (flow === 'invite')
    await dialog.locator('input[type="email"]').fill(createOk.body.invited_email);
  await dialog.locator('[data-members-submit]').click();
  await expect(dialog.getByTestId('reveal-value')).toHaveValue(
    flow === 'invite' ? CREATE_TOKEN : RESEND_TOKEN,
  );
  return dialog;
}

/** Includes property-held input values: serializing innerHTML alone would miss [value] binding. */
async function expectNoRetainedToken(page: Page): Promise<void> {
  const surface = await page.evaluate(() => ({
    html: document.documentElement.innerHTML,
    controls: Array.from(
      document.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('input, textarea'),
    ).map((control) => control.value),
    url: decodeURIComponent(location.href),
    local: JSON.stringify(Object.entries(localStorage)),
    session: JSON.stringify(Object.entries(sessionStorage)),
  }));
  for (const token of TOKENS) {
    expect(surface.html).not.toContain(token);
    expect(surface.controls.join('\n')).not.toContain(token);
    expect(surface.url).not.toContain(token);
    expect(surface.local).not.toContain(token);
    expect(surface.session).not.toContain(token);
  }
}

/** The one allowed copy is the readonly control inside the currently open reveal dialog. */
async function expectOnlyOpenReveal(page: Page, dialog: Locator, token: string): Promise<void> {
  await expect(dialog).toBeVisible();
  await expect(dialog.getByTestId('reveal-value')).toHaveValue(token);
  const outside = await page.evaluate(() => {
    const body = document.body.cloneNode(true) as HTMLElement;
    body
      .querySelectorAll('[data-members-dialog][data-state="open"]')
      .forEach((element) => element.remove());
    return {
      html: body.innerHTML,
      controls: Array.from(
        document.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('input, textarea'),
      )
        .filter((control) => control.closest('[data-members-dialog][data-state="open"]') === null)
        .map((control) => control.value),
      url: decodeURIComponent(location.href),
      local: JSON.stringify(Object.entries(localStorage)),
      session: JSON.stringify(Object.entries(sessionStorage)),
    };
  });
  for (const value of TOKENS) {
    expect(outside.html).not.toContain(value);
    expect(outside.controls.join('\n')).not.toContain(value);
    expect(outside.url).not.toContain(value);
    expect(outside.local).not.toContain(value);
    expect(outside.session).not.toContain(value);
  }
}

async function expectFocusInside(dialog: Locator): Promise<void> {
  await expect
    .poll(() => dialog.evaluate((element) => element.contains(document.activeElement)))
    .toBe(true);
}

/** Real Tab presses, checking each stop, rather than focusing the Copy control directly. */
async function tabTo(page: Page, dialog: Locator, target: Locator): Promise<void> {
  for (let step = 0; step < 24; step++) {
    if (await target.evaluate((element) => element === document.activeElement)) return;
    await page.keyboard.press('Tab');
    await expectFocusInside(dialog);
  }
  await expect(target, 'target is keyboard reachable within the modal').toBeFocused();
}

async function expectVerbatimRefusal(
  dialog: Locator,
  error: { code: string; message: string },
): Promise<void> {
  const region = dialog.locator('[data-members-error]');
  await expect(region).toBeVisible();
  await expect(region.locator('p')).toHaveText(error.message);
  await expect(region.locator('[data-members-error-code]')).toHaveText(error.code);
  await expect(dialog).toBeVisible();
  await expect(dialog.getByTestId('reveal-value')).toHaveCount(0);
}

for (const locale of LOCALES) {
  for (const flow of ['invite', 'resend'] as const) {
    // AC3 + §10 T3/F12: a full token is ephemeral and never part of the continuing screen.
    test(`${flow} shows its raw token once; Copy and Dismiss clear the DOM (${locale})`, async ({
      page,
      context,
    }) => {
      await context.grantPermissions(['clipboard-read', 'clipboard-write']);
      await openMembers(page, locale, [MEMBERS_RESEND_OK]);
      await expectNoRetainedToken(page);
      const dialog = await issueInvite(page, flow);
      const token = flow === 'invite' ? CREATE_TOKEN : RESEND_TOKEN;
      await expectOnlyOpenReveal(page, dialog, token);
      await dialog.getByTestId('reveal-copy').click();
      await expect(dialog.getByTestId('reveal-copy-status')).toHaveText(/\S/);
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(token);
      await dialog.getByTestId('reveal-dismiss').click();
      await expect(dialog).not.toBeVisible();
      await expectNoRetainedToken(page);
      // Reopening starts a new form/confirmation, never a second look at the old plaintext.
      await expect(page.locator('app-members-page')).toHaveAttribute('data-members-phase', 'ready');
      await openerFor(page, flow).click();
      await expect(dialog).toBeVisible();
      await expect(dialog.getByTestId('reveal-value')).toHaveCount(0);
      await expectNoRetainedToken(page);
    });

    // AC3 + §10 F12: an Idempotency-Key replay returns null, not an empty reveal or a fake secret.
    test(`${flow} replay offers the resend hint, never an empty reveal (${locale})`, async ({
      page,
    }) => {
      await openMembers(page, locale, [
        flow === 'invite' ? MEMBERS_CREATE_REPLAY : MEMBERS_RESEND_REPLAY,
      ]);
      await openerFor(page, flow).click();
      const dialog = dialogFor(page, flow);
      if (flow === 'invite')
        await dialog.locator('input[type="email"]').fill(createOk.body.invited_email);
      await dialog.locator('[data-members-submit]').click();
      await expect(dialog.locator('[data-members-invite-replay]')).toBeVisible();
      // The recovery guidance is located by its stable hook and only required to say something; its
      // wording is a copy decision, not a behaviour (the Resend path itself is covered below).
      await expect(dialog.locator('[data-members-invite-replay]')).toHaveText(/\S/);
      await expect(dialog.locator('app-connect-step-reveal')).toHaveCount(0);
      await expect(dialog.getByTestId('reveal-copy')).toHaveCount(0);
      await expectNoRetainedToken(page);
      await page.keyboard.press('Escape');
      await expect(dialog).not.toBeVisible();
      await expect(openerFor(page, flow)).toBeFocused();
    });

    // AC3 + W9: every step is keyboard-operable; modal focus must never fall behind the reveal.
    test(`${flow} reveal traps Tab, reaches Copy and Escape restores its opener (${locale})`, async ({
      page,
      context,
    }) => {
      await context.grantPermissions(['clipboard-read', 'clipboard-write']);
      await openMembers(page, locale, [MEMBERS_RESEND_OK]);
      const opener = openerFor(page, flow);
      await opener.focus();
      await page.keyboard.press('Enter');
      const dialog = dialogFor(page, flow);
      await expect(dialog).toBeVisible();
      await expectFocusInside(dialog);
      if (flow === 'invite') {
        await expect(dialog.locator('input[type="email"]')).toBeFocused();
        await page.keyboard.insertText(createOk.body.invited_email);
      }
      await tabTo(page, dialog, dialog.locator('[data-members-submit]'));
      await page.keyboard.press('Enter');
      const token = flow === 'invite' ? CREATE_TOKEN : RESEND_TOKEN;
      await expect(dialog.getByTestId('reveal-value')).toHaveValue(token);
      await expectFocusInside(dialog);
      await tabTo(page, dialog, dialog.getByTestId('reveal-copy'));
      await page.keyboard.press('Enter');
      await expect(dialog.getByTestId('reveal-copy-status')).toHaveText(/\S/);
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(token);
      // More than two complete cycles across value / Copy / Dismiss, in both directions.
      for (const key of ['Tab', 'Shift+Tab']) {
        for (let step = 0; step < 8; step++) {
          await page.keyboard.press(key);
          await expectFocusInside(dialog);
        }
      }
      await page.keyboard.press('Escape');
      await expect(dialog).not.toBeVisible();
      await expect(opener).toBeFocused();
      await expectNoRetainedToken(page);
    });
  }

  // AC2 error contract + F12: the 409 remains the server's answer, not a locally invented guard.
  for (const action of ['resend', 'cancel'] as const) {
    test(`${action} shows 409 invite_not_pending verbatim (${locale})`, async ({ page }) => {
      await openMembers(page, locale, [
        action === 'resend' ? MEMBERS_RESEND_NOT_PENDING : MEMBERS_CANCEL_NOT_PENDING,
      ]);
      await pendingRow(page)
        .locator(
          action === 'resend' ? '[data-members-resend-open]' : '[data-members-cancel-invite-open]',
        )
        .click();
      const dialog = page.locator(action === 'resend' ? RESEND : CANCEL);
      await dialog.locator('[data-members-submit]').click();
      await expectVerbatimRefusal(
        dialog,
        action === 'resend' ? resendNotPending.body.error : cancelNotPending.body.error,
      );
      await expect(page.locator('app-members-page > p[role="status"]')).toBeEmpty();
      await expectNoRetainedToken(page);
    });
  }

  // AC2 + F12: cancel has an explicit confirmation and announces success only after the 204.
  test(`cancel 204 closes its confirmation and recovers focus (${locale})`, async ({ page }) => {
    await openMembers(page, locale, [MEMBERS_CANCEL_OK]);
    const opener = pendingRow(page).locator('[data-members-cancel-invite-open]');
    await opener.click();
    const dialog = page.locator(CANCEL);
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('strong')).toHaveText(PENDING_EMAIL);
    await dialog.locator('[data-members-submit]').click();
    await expect(dialog).not.toBeVisible();
    await expect(page.locator('app-members-page > p[role="status"]')).toHaveText(/\S/);
    await expect(page.locator('app-members-page')).toHaveAttribute('data-members-phase', 'ready');
    // The cancelled row need not exist in the real answer: the page intentionally recovers focus
    // at its heading (completed(true)), not at a potentially removed row's opener.
    await expect(page.locator('app-members-page > header h2')).toBeFocused();
    await expect(dialog.locator('[data-members-error]')).toHaveCount(0);
    await expectNoRetainedToken(page);
  });

  // AC1 + F12: these mutations belong to pending rows AND membership:write, not merely to a role.
  test(`resend and cancel are absent on non-pending rows and for readonly grants (${locale})`, async ({
    page,
  }) => {
    await openMembers(page, locale);
    const pendingCount = membershipsList.body.items.filter(
      (member) => member.status === 'pending',
    ).length;
    await expect(page.locator(`${ROWS} [data-members-resend-open]`)).toHaveCount(pendingCount);
    await expect(page.locator(`${ROWS} [data-members-cancel-invite-open]`)).toHaveCount(
      pendingCount,
    );
    for (const member of membershipsList.body.items.filter((item) => item.status !== 'pending')) {
      const row = page.locator(`${ROWS}[data-membership-id="${member.id}"]`);
      await expect(row).toHaveAttribute('data-members-status', member.status);
      await expect(
        row.locator('[data-members-resend-open], [data-members-cancel-invite-open]'),
      ).toHaveCount(0);
    }
    await page.goto(withVariants(membersPath(locale), [MEMBERS_READONLY_GRANTS]));
    await expect(page.locator('app-members-page')).toHaveAttribute('data-members-phase', 'ready');
    await expect(pendingRow(page)).toHaveCount(1);
    await expect(
      page.locator(
        '[data-members-invite-open], [data-members-resend-open], [data-members-cancel-invite-open]',
      ),
    ).toHaveCount(0);
    await expect(page.locator(`${INVITE}, ${RESEND}, ${CANCEL}`)).toHaveCount(0);
  });

  // AC1/AC3 + W9: at 360px Settings is inspection-only, not a compressed mutation surface.
  test(`360px offers inspection and no invite lifecycle actions (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await openMembers(page, locale);
    await expect(page.locator('app-members-page .narrow-notice')).toBeVisible();
    await expect(page.locator('app-members-page .narrow-notice')).toHaveText(/\S/);
    await expect(pendingRow(page)).toBeVisible();
    for (const selector of [
      '[data-members-invite-open]',
      '[data-members-resend-open]',
      '[data-members-cancel-invite-open]',
    ]) {
      await expect(page.locator(`app-members-page ${selector}:visible`)).toHaveCount(0);
    }
    await expect(page.getByTestId('reveal-value')).toHaveCount(0);
    await expectNoRetainedToken(page);
    await expectNoA11yViolations(page, `members invite inspection-only ${locale}/360px`);
  });
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    // AC3 + W9: scan all overlaid lifecycle steps with real contrast, in BOTH locale/theme axes.
    test(`invite form, reveal, resend and cancel have no axe violations (${locale}/${theme})`, async ({
      page,
    }) => {
      await seedStoredTheme(page, theme);
      await openMembers(page, locale, [MEMBERS_RESEND_OK]);
      await expect(page.locator('html')).toHaveAttribute('lang', locale);
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      await openerFor(page, 'invite').click();
      const invite = page.locator(INVITE);
      await expect(invite).toBeVisible();
      await waitForDialogAnimations(page, INVITE);
      await expectNoA11yViolations(page, `invite form ${locale}/${theme}`);
      await invite.locator('input[type="email"]').fill(createOk.body.invited_email);
      await invite.locator('[data-members-submit]').click();
      await expect(invite.getByTestId('reveal-value')).toHaveValue(CREATE_TOKEN);
      await expectNoA11yViolations(page, `invite reveal ${locale}/${theme}`);
      await invite.getByTestId('reveal-dismiss').click();
      await expectNoRetainedToken(page);
      await openerFor(page, 'resend').click();
      const resend = page.locator(RESEND);
      await expect(resend).toBeVisible();
      await waitForDialogAnimations(page, RESEND);
      await expectNoA11yViolations(page, `resend confirmation ${locale}/${theme}`);
      await resend.locator('[data-members-submit]').click();
      await expect(resend.getByTestId('reveal-value')).toHaveValue(RESEND_TOKEN);
      await expectNoA11yViolations(page, `resend reveal ${locale}/${theme}`);
      await resend.getByTestId('reveal-dismiss').click();
      await expectNoRetainedToken(page);
      await pendingRow(page).locator('[data-members-cancel-invite-open]').click();
      await expect(page.locator(CANCEL)).toBeVisible();
      await waitForDialogAnimations(page, CANCEL);
      await expectNoA11yViolations(page, `cancel confirmation ${locale}/${theme}`);
    });
  }
}

for (const flow of ['invite', 'resend'] as const) {
  // AC3 + §10 F12: leaving the route destroys an open reveal, including property-held values.
  test(`${flow} reveal is cleared when browser history leaves Members`, async ({ page }) => {
    await openMembers(page, 'en', [MEMBERS_RESEND_OK]);
    const organization = scopeControl(page, 0);
    await expect(organization).toBeEnabled();
    await expectScopeValue(organization, ORG_ACME);
    await chooseScope(page, organization, ORG_LUMEN);
    await expectScopeValue(organization, ORG_LUMEN);
    // The only second org of orgs-list is suspended: the real switcher refuses its destination.
    await expect(page).toHaveURL(/\/forbidden$/);
    await page.goBack();
    await expect(page.locator('app-members-page')).toHaveAttribute('data-members-phase', 'ready');
    await expectScopeValue(organization, ORG_ACME);
    const dialog = await issueInvite(page, flow);
    await expectFocusInside(dialog);
    // Native browser history is reachable while a modal is open; the inert select is not.
    await page.goForward();
    await expect(page).toHaveURL(/\/forbidden$/);
    await expect(page.locator('[data-members-dialog][data-state="open"]')).toHaveCount(0);
    await expectNoRetainedToken(page);
    await page.goBack();
    await expect(page.locator('app-members-page')).toHaveAttribute('data-members-phase', 'ready');
    await expectNoRetainedToken(page);
    await openerFor(page, flow).click();
    await expect(dialogFor(page, flow).getByTestId('reveal-value')).toHaveCount(0);
  });
}
