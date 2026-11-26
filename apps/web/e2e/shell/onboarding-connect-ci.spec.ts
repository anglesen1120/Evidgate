// The optional credential step: ingest-token create and rotate, their one-time values and the
// metadata-only resume, in real Chromium against the FIXTURES build
// (`playwright.fixtures.config.ts`). A project with no token still resumes at first-run step 3;
// these cases reach the credential page with Back.
//
// Three rules shape what these cases assert:
//
// 1. **A one-time value is proved by its footprint, not by its presence.** `secretFootprint` scans
//    every attribute, every form control's `value` property (Angular's `[value]` sets the property,
//    not the attribute), every rendered text node, both storages, `document.cookie`, the address bar
//    and `history.state`. `expectSecretOnlyInReveal` then requires the value to be held by exactly
//    one element inside the slot while it is open; `expectSecretAbsent` requires no holder and no
//    entry anywhere once it closes. A value that leaked into an `aria-label`, a second hidden field
//    or `localStorage` would pass every positive assertion, so this footprint is the test.
// 2. **The fixtures build answers in-process.** The dev backend is installed as Angular's
//    `HttpBackend`, so `page.route` cannot intercept a command. Commands are observed in the
//    backend's own request log (`fixtureWrites`): a load, a Cancel, an Escape or a reload sends no
//    write, and a confirmed command sends exactly one. Which answer the server gave is asserted
//    against the corpus document that answered it.
// 3. **The corpus is static.** A create's answer cannot add a row to the list read that follows
//    it, so these cases prove the reveal and the *seeded* metadata.
import { expect, test, type Page } from '@playwright/test';

import { stepButton } from './onboarding-keyboard';
import {
  connectCiPage,
  expectNoReveal,
  expectSecretAbsent,
  expectSecretOnlyInReveal,
  FIXTURE_ROTATED_TOKEN_SECRET,
  FIXTURE_TOKEN_SECRET,
  fixtureWrites,
  openConnectStep,
  revealSlot,
  revealValue,
} from './onboarding-connection-support';
import {
  expectCurrentStep,
  expectNoStepInStorage,
  expectStepDone,
  nextButton,
  NO_WRITE_GRANTS_TOGGLE,
  onboardingPath,
  onboardingUrl,
  seedQueryWith,
  shell,
  writeVariantToggle,
} from './onboarding-support';
import { withVariant } from './support';

/** The name typed into the token form; the corpus echoes its own name back, never this one. */
const TOKEN_NAME = 'CI upload (e2e)';

/** The step-2 page's own failure panel: the only other render a create can produce. */
function ciError(page: Page) {
  return page.locator('[data-testid="connect-ci-error"]');
}

/** Fills and submits the token form; the caller asserts the answer. */
async function createToken(page: Page): Promise<void> {
  await page.locator('[data-testid="connect-token-name"]').fill(TOKEN_NAME);
  await page.locator('[data-testid="connect-token-submit"]').click();
}

test.describe('step 2 is the optional credential page (plan §10 T2)', () => {
  test('the page offers token creation only and writes nothing on load', async ({ page }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(shell(page)).toBeVisible();
    await openConnectStep(page);

    await expect(page.locator('[data-testid="onboarding-step-pending"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="connect-token-panel"]')).toBeVisible();
    await expect(page.locator('[data-testid="connect-oidc-panel"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="connect-oidc-submit"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="connect-binding-row"]')).toHaveCount(0);

    // Credentials are optional: the step is not done on an empty inventory, and Next is not gated
    // behind it, so a reader can walk on without creating anything.
    await expectStepDone(page, 2, false);
    await expect(nextButton(page)).toBeEnabled();
    await expectNoReveal(page);
    await expect(ciError(page)).toHaveCount(0);
    expect(await fixtureWrites(page), 'opening the page sent a command').toEqual([]);
  });
});

test.describe('the token path reveals its secret exactly once (AC2, §8)', () => {
  test('a created token is revealed, copied, and cleared by Dismiss', async ({ page, context }) => {
    // The reveal is only useful if Copy works, and a clipboard read needs the grant.
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(shell(page)).toBeVisible();
    await openConnectStep(page);
    expect(await fixtureWrites(page), 'opening the page sent a command').toEqual([]);

    await createToken(page);

    // The one-time value arrives in the slot wrapper — the single subtree §8 allows it in.
    await expect(revealSlot(page)).toBeVisible();
    const writes = await fixtureWrites(page);
    expect(writes).toHaveLength(1);
    expect(writes[0]).toMatch(/^POST .*\/projects\/[^/]+\/ingest-tokens$/);
    const value = revealValue(page);
    await expect(value).toHaveValue(FIXTURE_TOKEN_SECRET);
    await expect(revealSlot(page).getByRole('heading')).toBeFocused();
    const shownStatus = page.getByTestId('connect-reveal-status');
    await expect(shownStatus).toHaveAttribute('aria-live', 'polite');
    await expect(shownStatus).not.toHaveText(/^\s*$/);
    await expect(shownStatus).not.toContainText(FIXTURE_TOKEN_SECRET);
    // AC2's second clause, before anything is dismissed: the value lives in the slot, in exactly
    // one element, and nowhere else — not in a hidden field, a log buffer, a store, the URL or a
    // history entry.
    await expectSecretOnlyInReveal(page, FIXTURE_TOKEN_SECRET, 'the open reveal');
    await expectNoStepInStorage(page, 'after the token create');
    await expect(ciError(page)).toHaveCount(0);

    // Copy announces the success message, and that message is not the manual-copy warning the same
    // control publishes when the clipboard refuses (its `aria-describedby` target): a status that
    // repeated the warning would claim a copy that never happened.
    const describedBy = await value.getAttribute('aria-describedby');
    expect(describedBy, 'the revealed value must describe itself with its copy warning').not.toBe(
      null,
    );
    const warning = (await page.locator(`#${describedBy}`).innerText()).trim();
    await expect(page.locator('[data-testid="reveal-copy"]')).toBeEnabled();
    await page.locator('[data-testid="reveal-copy"]').click();
    const announcement = page.locator('[data-testid="reveal-copy-status"]');
    await expect(announcement).not.toHaveText(/^\s*$/);
    expect((await announcement.innerText()).trim()).not.toBe(warning);
    expect(
      await page.evaluate(() => navigator.clipboard.readText()),
      'the clipboard did not receive the one-time value',
    ).toBe(FIXTURE_TOKEN_SECRET);

    await page.locator('[data-testid="reveal-dismiss"]').click();
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'after Dismiss');
    await expect(page.locator('#connect-token-heading')).toBeFocused();

    // The corpus is static: the list read after a create answers the seed's empty page, so the page
    // must not invent a row from the answer it just revealed.
    await expect(page.locator('[data-testid="connect-token-row"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="connect-ci-refresh-failed"]')).toHaveCount(0);
  });

  test('the secret does not survive leaving the step or a reload (AC2)', async ({ page }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(shell(page)).toBeVisible();
    await openConnectStep(page);
    await createToken(page);
    await expect(revealValue(page)).toHaveValue(FIXTURE_TOKEN_SECRET);

    // Leaving clears the retained page's scoped slot; returning cannot recover a one-time value
    // from the server's metadata.
    await stepButton(page, 1).click();
    await expectCurrentStep(page, 1);
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'step 1');
    await stepButton(page, 2).click();
    await expectCurrentStep(page, 2);
    await expect(connectCiPage(page)).toBeVisible();
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the revisited step 2');
    expect(await fixtureWrites(page), 'leaving and returning replayed it').toHaveLength(1);

    // A fresh boot derives the step from server metadata; a server that returns a secret once can
    // never re-reveal it, so the reload must show the page with no value at all.
    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the reload');
    await expectNoStepInStorage(page, 'the reload');
    expect(await fixtureWrites(page), 'the reload replayed the command').toEqual([]);
    await openConnectStep(page);
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the reopened step 2');
  });
});

test.describe('the resume is metadata-only (AC2, AC7)', () => {
  test('a token makes step 3 the resume point, and step 2 shows metadata without a secret', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-token'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);
    // Nothing on load creates anything: neither render a command can produce is present, no
    // error is, and the backend log holds no write.
    await expectNoReveal(page);
    await expect(ciError(page)).toHaveCount(0);
    expect(await fixtureWrites(page), 'the load sent a command').toEqual([]);

    await openConnectStep(page);

    const row = page.locator('[data-testid="connect-token-row"]');
    await expect(row).toHaveCount(1);
    await expect(row).toHaveAttribute('data-status', 'active');
    // The server's metadata only: the prefix it stores, never a value it has already handed out.
    await expect(row.locator('code')).toHaveText('egt_fixture');
    const view = row.getByRole('button', { name: /View / });
    await view.click();
    const dialog = page.getByRole('dialog', { name: 'Ingest token details' });
    const title = dialog.getByRole('heading', { name: 'Ingest token details' });
    await expect(title).toBeFocused();
    await expect(dialog).toHaveAttribute('aria-labelledby', (await title.getAttribute('id')) ?? '');
    await expect(dialog).toContainText('egt_fixture');
    await expect(dialog).toContainText('Scopes');
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the token metadata dialog');
    await expectSecretAbsent(page, FIXTURE_ROTATED_TOKEN_SECRET, 'the token metadata dialog');
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(view).toBeFocused();
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the resumed step 2');
    await expectSecretAbsent(page, FIXTURE_ROTATED_TOKEN_SECRET, 'the resumed step 2');
    await expectNoReveal(page);
    // Credentials are optional: the seeded token does not mark the step done, and Next stays open.
    await expectStepDone(page, 2, false);
    expect(await fixtureWrites(page), 'viewing metadata sent a command').toEqual([]);
  });

  test('a reload with no token resumes at step 3 and reveals nothing', async ({ page }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(shell(page)).toBeVisible();
    await openConnectStep(page);
    await expectNoReveal(page);

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);
    await expectNoReveal(page);
    await openConnectStep(page);
    await expectNoReveal(page);
    await expect(ciError(page)).toHaveCount(0);
    await expect(page.locator('[data-testid="connect-token-row"]')).toHaveCount(0);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the reload of an empty step 2');
    expect(await fixtureWrites(page), 'the reload sent a command').toEqual([]);
  });
});

test.describe('rotating a token reveals its successor once (AC2)', () => {
  test('Rotate requires consent, Cancel/Escape preserve the token, and Dismiss clears the confirmed secret', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-token'));
    await expect(shell(page)).toBeVisible();
    await openConnectStep(page);

    const rotate = page.locator('[data-testid="connect-token-rotate"]');
    await expect(rotate).toBeEnabled();
    await rotate.click();
    const consent = page.getByRole('alertdialog');
    const cancel = page.getByTestId('connect-rotation-cancel');
    await expect(consent).toBeVisible();
    await expect(cancel).toBeFocused();
    await expectNoReveal(page);
    await cancel.click();
    await expect(consent).toHaveCount(0);
    await expect(rotate).toBeFocused();
    await expectNoReveal(page);
    await rotate.click();
    await expect(cancel).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(consent).toHaveCount(0);
    await expect(rotate).toBeFocused();
    await expectNoReveal(page);
    expect(await fixtureWrites(page), 'Cancel or Escape sent a command').toEqual([]);
    await rotate.click();
    await expect(cancel).toBeFocused();
    await consent.getByTestId('connect-rotation-confirm').click();
    await expect(consent).toHaveCount(0);

    await expect(revealValue(page)).toHaveValue(FIXTURE_ROTATED_TOKEN_SECRET);
    const writes = await fixtureWrites(page);
    expect(writes).toHaveLength(1);
    expect(writes[0]).toMatch(/^POST .*\/ingest-tokens\/[^/]+\/rotate$/);
    await expectSecretOnlyInReveal(page, FIXTURE_ROTATED_TOKEN_SECRET, 'the open rotation reveal');
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the open rotation reveal');
    // The overlap the server reports is metadata and stays: it is when the old token stops working.
    const overlap = connectCiPage(page).locator('time[datetime="2026-10-26T06:10:00Z"]');
    await expect(overlap).toBeVisible();
    await expect(ciError(page)).toHaveCount(0);

    await page.locator('[data-testid="reveal-dismiss"]').click();
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_ROTATED_TOKEN_SECRET, 'after Dismiss');
    // Metadata survives Dismiss on purpose: the overlap end is not a secret and a reader who
    // dismissed the value still needs to know how long the predecessor keeps working.
    await expect(overlap).toBeVisible();
    await expectNoStepInStorage(page, 'after the rotation');
  });
});

test.describe('a session without the write grants cannot create a token (AC2, §8)', () => {
  test('the submit is disabled with a localized reason in aria-describedby', async ({ page }) => {
    // `permissions-list-onboarding-no-writes`: the wizard's own catalogue minus
    // `ingest_token:write`, keeping `project:create` so the route itself still opens.
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-project', [NO_WRITE_GRANTS_TOGGLE]),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await openConnectStep(page);

    // The control is natively disabled, not dressed as disabled: a keyboard or assistive-tech
    // user must not be able to submit a create the session may not perform.
    const ingestSubmit = page.locator('[data-testid="connect-token-submit"]');
    await expect(ingestSubmit).toBeDisabled();
    await expect(ingestSubmit).toHaveAttribute('aria-describedby', 'connect-token-permission');
    expect((await page.locator('#connect-token-permission').innerText()).trim()).not.toBe('');
    await expect(page.locator('[data-testid="connect-token-name"]')).toBeDisabled();

    // No create happened and none can: nothing to reveal, no error, no command in the backend log.
    await expectNoReveal(page);
    await expect(ciError(page)).toHaveCount(0);
    expect(await fixtureWrites(page), 'a disabled control sent a command').toEqual([]);
  });
});

test.describe('a refused rotation shows the server error verbatim (§8, 00 §9.2)', () => {
  /**
   * `fixtures/web-api/ingest-token-rotate-conflict`: a 409 in the 00 §9.2 envelope, answered
   * through the same typed error path a real refusal takes, with `retryable: false` because a
   * secret-bearing command is never replayed.
   */
  const REFUSAL_MESSAGE =
    'this ingest token has already been rotated; create a new token instead of rotating it again';

  test('a conflict is rendered, the controls come back, and no secret appears', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-token', [writeVariantToggle('rotateIngestToken', 'conflict')]),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await openConnectStep(page);

    const rotate = page.locator('[data-testid="connect-token-rotate"]');
    await rotate.click();
    await page.getByRole('alertdialog').getByTestId('connect-rotation-confirm').click();

    const error = ciError(page);
    await expect(error).toBeVisible();
    // Verbatim: the page echoes the envelope's `message` and the pattern shows its `code`.
    await expect(error.getByRole('alert')).toHaveCount(1);
    await expect(error.getByText(REFUSAL_MESSAGE, { exact: true })).toBeVisible();
    await expect(error.locator('code')).toHaveText('invalid_transition');
    // The envelope's own `request_id` reaches the reader (00 §9.2); it is the document's value,
    // so "no automatic retry" rests on the write count below, never on this id.
    await expect(error.locator('[data-testid="error-request-id"]')).toContainText(
      '018f0000-0000-7000-8000-0000000000e5',
    );
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_ROTATED_TOKEN_SECRET, 'the refused rotation');
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the refused rotation');

    // The attempt is over: not busy, the control is back, and there is no retry affordance because
    // the envelope says `retryable: false` — only a deliberate user action can send it again.
    await expect(connectCiPage(page)).toHaveAttribute('aria-busy', 'false');
    await expect(rotate).toBeEnabled();
    await expect(error.locator('button')).toHaveCount(0);
    expect(await fixtureWrites(page), 'the refusal was retried').toHaveLength(1);

    // Leaving clears the attempt feedback without replaying the command.
    await stepButton(page, 1).click();
    await expectCurrentStep(page, 1);
    await stepButton(page, 2).click();
    await expectCurrentStep(page, 2);
    await expect(ciError(page)).toHaveCount(0);
    await expectNoReveal(page);
    expect(await fixtureWrites(page), 'leaving replayed the refused command').toHaveLength(1);

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);
    await expect(ciError(page)).toHaveCount(0);
    await expectNoReveal(page);
    await expectNoStepInStorage(page, 'the reload after a refusal');
    expect(await fixtureWrites(page), 'the reload replayed the refused command').toEqual([]);
  });
});
