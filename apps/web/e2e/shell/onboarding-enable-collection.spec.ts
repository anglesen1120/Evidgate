// 09.2.1-T5 (plan 09.2.1 §2 AC5, §8 rows T98 and the two idempotency rows, §9 rows 123–124,
// §10 T5.1/T5.2): step 5's advisory-mode preview, its confirm and the terminal "project enabled
// and collecting" panel, in real Chromium against the FIXTURES build
// (`playwright.fixtures.config.ts`).
//
// Four rules shape what these cases assert:
//
// 1. **Step 5 is never derived done.** `contracts/openapi.yaml` declares only
//    `POST /v1/projects/{p}/policy-activation`, with no project-keyed read of a `policy_activation`
//    (plan §10 Notes). The wizard therefore reports activation as unreadable, and a reload shows
//    the confirm again; a repeated confirm is the corpus's `unchanged` answer (00 §9.1: a retry with
//    the same intent is a replay, not an error). The reload case below asserts exactly that, and
//    nothing here claims the step derives done.
//
// 2. **The grant split is the state.** The confirm is derived from `policy:write` alone. The
//    wizard's default grants (`permissions-list-onboarding`, seven codes) do not hold it, so the
//    control is natively `disabled` and the "ask your release approver" copy is rendered; the lone
//    org creator holds `release_approver` from org creation (T98), which is
//    `permissions-list-onboarding-enable` (the same seven codes plus `policy:write`).
//
// 3. **The corpus answers the offers, the page renders the answer.** Every activation answer is a
//    document (`policy-activation-ok`, `-unchanged`, `-invalid-transition`, `-forbidden`); the
//    refusal text asserted below is read from those documents, never retyped, so a corpus edit
//    cannot leave the assertion measuring something else. The page renders the shared
//    `app-error-pattern` for every failure kind (its own choice, `enable-collection.page.html`) —
//    not the generic forbidden panel — so a 403 shows the envelope's message, code and request id
//    too.
//
// 4. **What the browser cannot observe, this suite does not claim.** The `fixtures` build answers
//    the API in process — `provideFixtureBackend()` replaces Angular's `HttpBackend`
//    (`src/app/core/dev-fixtures/fixture-backend.ts`) — so no network request exists for Playwright
//    to intercept, hold or count: `page.route` and `page.on('request')` see none (the same reason
//    `settings-members.qa.spec.ts:1-6` sends retry/double-submit to Vitest). The double-submit case
//    below therefore proves the *effect* the user sees — one terminal panel, no error, with the
//    submit kept in flight by the corpus latency — and never claims to have seen one `POST` or its
//    `Idempotency-Key` header. Those are pinned where they are observable, in the page's own Vitest
//    spec (`steps/enable-collection.page.spec.ts`: one activation per confirm intent, the intent's
//    key reused on every retry, a second click during flight sending nothing).
import { expect, test, type Page } from '@playwright/test';

import activationForbidden from '../../../../fixtures/web-api/policy-activation-forbidden/input.json';
import activationInvalidTransition from '../../../../fixtures/web-api/policy-activation-invalid-transition/input.json';
import {
  activationVariantToggle,
  ENABLE_GRANTS_TOGGLE,
  expectCurrentStep,
  latencyToggle,
  onboardingPath,
  seedQueryWith,
  shell,
} from './onboarding-support';
import {
  currentTheme,
  expectNoA11yViolations,
  LOCALES,
  seedStoredTheme,
  THEMES,
  withVariant,
  type ShellLocale,
} from './support';

/** The seed whose derivation leaves step 5 current (a run with an evidence revision, no policy). */
const STEP_FIVE_SEED = 'onboarding-evidence';

/** The step-5 page root; every state below renders inside it. */
const ENABLE_ROOT = '[data-testid="enable-collection"]';
/** The read-only preview of the policy body the step would activate. */
const POLICY_PREVIEW = '[data-testid="enable-collection-policy-preview"]';
/** The explicit "not gating yet / advisory mode" statement AC5 requires. */
const NOT_GATING = '[data-testid="enable-collection-not-gating"]';
/** The confirm control: a real `<button>`, natively `disabled` without `policy:write`. */
const CONFIRM = '[data-testid="enable-collection-confirm"]';
/** D-S104-Q2: the hazard acknowledgement; the confirm stays disabled until it is checked. */
const ACKNOWLEDGE = '[data-testid="enable-collection-acknowledge"]';
/** The replace-policy warning, which the confirm points at while the acknowledgement is open. */
const REPLACES_WARNING = '[data-testid="enable-collection-replaces-active"]';
/** The "ask your release approver" copy, rendered only for a session without `policy:write`. */
const ASK_APPROVER = '[data-testid="enable-collection-ask-approver"]';
/** The terminal panel: the project is enabled and collecting. */
const ENABLED = '[data-testid="enable-collection-enabled"]';
/** The page's typed error panel (a refused activation). */
const ERROR = '[data-testid="enable-collection-error"]';

/**
 * Opens the step-5 address of one locale and waits for the wizard to resolve that step.
 * `seedQueryWith` appends after the seed's own toggles and a later `fixture.variant.<operation>`
 * wins (`fixture-toggles.ts` builds a `Map` from the URL), so `ENABLE_GRANTS_TOGGLE` replaces the
 * wizard's default grant set instead of colliding with it.
 */
async function openStepFive(
  page: Page,
  locale: ShellLocale,
  extra: readonly string[] = [],
): Promise<void> {
  const query = seedQueryWith(STEP_FIVE_SEED, extra);
  await page.goto(withVariant(onboardingPath(locale), query));
  await expect(shell(page)).toBeVisible();
  await expectCurrentStep(page, 5);
  await expect(page.locator(ENABLE_ROOT)).toBeVisible();
}

/**
 * The acknowledgement a real confirm requires (D-S104-Q2): checked with the label the user sees,
 * so a checkbox the page never enabled could not be acknowledged silently.
 */
async function acknowledge(page: Page): Promise<void> {
  const box = page.locator(ACKNOWLEDGE);
  await expect(box).toBeEnabled();
  await box.check();
  await expect(box).toBeChecked();
}

test.describe('step 5 previews the policy in advisory mode (AC5, T5.1)', () => {
  test('the preview, the "not gating yet" statement and an enabled confirm render together', async ({
    page,
  }) => {
    // The lone org creator's grants (T98): the same seed, one code more.
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE]);

    // The preview is read-only prose about the body that would be activated, not an editor: it is
    // rendered and non-empty.
    const preview = page.locator(POLICY_PREVIEW);
    await expect(preview).toBeVisible();
    await expect(preview).toHaveCount(1);
    expect((await preview.innerText()).trim()).not.toBe('');

    // AC5's own sentence: the preview must say the project is not gating yet. The wording is the
    // bundle's; what this suite pins is that a statement exists and is non-empty.
    const notGating = page.locator(NOT_GATING);
    await expect(notGating).toBeVisible();
    expect((await notGating.innerText()).trim()).not.toBe('');

    // The control is a real button; for a holder of `policy:write` it is gated by the hazard
    // acknowledgement alone (D-S104-Q2), which starts unchecked.
    const confirm = page.locator(CONFIRM);
    await expect(confirm).toBeVisible();
    expect(await confirm.evaluate((control) => control.tagName)).toBe('BUTTON');
    await expect(confirm).toBeDisabled();
    const warning = page.locator(REPLACES_WARNING);
    await expect(warning).toBeVisible();
    expect((await warning.innerText()).trim()).not.toBe('');
    // The disabled control names the warning as its reason, and an unchecked box is not a submit.
    await expect(confirm).toHaveAttribute('aria-describedby', /enable-replaces-warning/);
    const box = page.locator(ACKNOWLEDGE);
    await expect(box).not.toBeChecked();
    await page.locator(CONFIRM).click({ force: true });
    await expect(page.locator(ENABLED)).toHaveCount(0);

    await acknowledge(page);
    await expect(confirm).toBeEnabled();
    await expect(confirm).not.toHaveAttribute('aria-describedby', /enable-replaces-warning/);

    // Nothing has been activated yet, so no terminal panel and no error; the ask-approver copy
    // belongs to the disabled state alone.
    await expect(page.locator(ENABLED)).toHaveCount(0);
    await expect(page.locator(ERROR)).toHaveCount(0);
    await expect(page.locator(ASK_APPROVER)).toHaveCount(0);
  });

  test('a session without `policy:write` sees a disabled control and the ask-approver copy', async ({
    page,
  }) => {
    // The seed's own grants (`permissions-list-onboarding`, seven codes, no `policy:write`): the
    // state of the session whose `release_approver` role was reassigned away (plan §9 row 123).
    await openStepFive(page, 'en');

    // The preview stays readable — it is a read, not a write.
    await expect(page.locator(POLICY_PREVIEW)).toBeVisible();
    await expect(page.locator(NOT_GATING)).toBeVisible();

    // Disabled natively, not dressed as disabled: a keyboard or assistive-tech user must not be
    // able to fire an activation the session may not perform, so no submit can happen at all.
    const confirm = page.locator(CONFIRM);
    await expect(confirm).toBeVisible();
    expect(await confirm.evaluate((control) => control.tagName)).toBe('BUTTON');
    await expect(confirm).toBeDisabled();

    // …and the reason is rendered beside it, non-empty.
    const askApprover = page.locator(ASK_APPROVER);
    await expect(askApprover).toBeVisible();
    expect((await askApprover.innerText()).trim()).not.toBe('');

    // The acknowledgement cannot stand in for the grant: its own box is disabled and unchecked, so
    // the state is "ask your approver", never a self-service acknowledgement (D-S104-Q2, T98).
    const box = page.locator(ACKNOWLEDGE);
    await expect(box).toBeVisible();
    await expect(box).toBeDisabled();
    await expect(box).not.toBeChecked();

    // The seeded answer would have been `ok`: nothing was activated, so no terminal panel exists.
    await expect(page.locator(ENABLED)).toHaveCount(0);
    await expect(page.locator(ERROR)).toHaveCount(0);
  });
});

test.describe('confirming activates the project (AC5, T5.2)', () => {
  test('the confirm reaches the terminal "enabled and collecting" panel', async ({ page }) => {
    // `permissions-list-onboarding-enable` holds `policy:write`; `activatePolicyRevision` answers
    // its `ok` document (200, `changed: true`, mode `advisory`).
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE]);

    await acknowledge(page);
    await page.locator(CONFIRM).click();

    const terminal = page.locator(ENABLED);
    await expect(terminal).toBeVisible();
    // The panel is the step's terminal state, not a second preview: one panel and no error.
    await expect(terminal).toHaveCount(1);
    await expect(page.locator(ERROR)).toHaveCount(0);
    // QA-01: the confirm the user pressed is gone, so focus must not be dropped on `<body>`.
    await expect(page.locator('#enable-enabled-heading')).toBeFocused();
  });

  test('a replay answer (`changed: false`) is a success, and a reload never derives the step done', async ({
    page,
  }) => {
    // `policy-activation-unchanged` is the contract's replay: same revision, same hash,
    // `changed: false`. After a reload the wizard is back on step 5 (there is no read to derive it
    // from), and the second confirm is answered with the same replay — not with an error.
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE, activationVariantToggle('unchanged')]);

    await acknowledge(page);
    await page.locator(CONFIRM).click();
    await expect(page.locator(ENABLED)).toBeVisible();
    await expect(page.locator(ERROR)).toHaveCount(0);

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 5);
    // Not derived: the terminal panel is gone and the confirm is back — and unacknowledged again,
    // because the acknowledgement is per-visit state and never persisted (D-S104-Q2).
    await expect(page.locator(ENABLED)).toHaveCount(0);
    await expect(page.locator(ACKNOWLEDGE)).not.toBeChecked();
    await expect(page.locator(CONFIRM)).toBeDisabled();

    await acknowledge(page);
    await page.locator(CONFIRM).click();
    await expect(page.locator(ENABLED)).toBeVisible();
    await expect(page.locator(ERROR)).toHaveCount(0);
  });
});

test.describe('a refused activation is rendered verbatim (00 §9.2)', () => {
  test('409 `invalid_transition` shows the server envelope and no terminal panel', async ({
    page,
  }) => {
    // `policy-activation-invalid-transition`: a retired revision can never be activated again. The
    // session holds `policy:write`, so the refusal can only come from the server.
    await openStepFive(page, 'en', [
      ENABLE_GRANTS_TOGGLE,
      activationVariantToggle('invalid-transition'),
    ]);

    await acknowledge(page);
    await page.locator(CONFIRM).click();

    const error = page.locator(ERROR);
    await expect(error).toBeVisible();
    // Verbatim: the page echoes the envelope's `message` and shows its `code`.
    await expect(error).toContainText(activationInvalidTransition.body.error.message);
    await expect(error.locator('code')).toHaveText(activationInvalidTransition.body.error.code);
    // The support path 00 §9.2 asks for: the document's own `request_id`, not a per-call header.
    await expect(error.locator('[data-testid="error-request-id"]')).toContainText(
      activationInvalidTransition.body.error.request_id,
    );
    // A refusal is not an activation, and the attempt is over: a deliberate second submit is still
    // possible.
    await expect(page.locator(ENABLED)).toHaveCount(0);
    await expect(page.locator(CONFIRM)).toBeEnabled();
  });

  test('403 `forbidden` shows the server envelope and no terminal panel', async ({ page }) => {
    // The other refusal a confirm can meet: the session may render the step (the grants say so) but
    // the server decides otherwise — the answer a request rejected for `policy:write` produces. The
    // page renders the typed refusal here on purpose (`app-error-pattern`, not the generic
    // forbidden panel), so the envelope is asserted verbatim, as for the 409.
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE, activationVariantToggle('forbidden')]);

    await acknowledge(page);
    await page.locator(CONFIRM).click();

    const error = page.locator(ERROR);
    await expect(error).toBeVisible();
    await expect(error).toContainText(activationForbidden.body.error.message);
    await expect(error.locator('code')).toHaveText(activationForbidden.body.error.code);
    await expect(error.locator('[data-testid="error-request-id"]')).toContainText(
      activationForbidden.body.error.request_id,
    );
    // A refusal is not an activation, and the attempt is over.
    await expect(page.locator(ENABLED)).toHaveCount(0);
    await expect(page.locator(CONFIRM)).toBeEnabled();
  });
});

test.describe('an in-flight double click produces one activation render (00 §9.1, plan §9 row 124)', () => {
  /**
   * The corpus latency keeps the submit observable while it is in flight; the second click of the
   * `dblclick` lands inside that window. What is asserted is the effect — exactly one terminal
   * panel, no error — because the `fixtures` build answers in process and offers no network seam
   * to count requests or read the `Idempotency-Key` (header comment, rule 4). The one-intent /
   * one-key / one-call rule is the page spec's assertion.
   */
  const IN_FLIGHT_MS = 400;

  test('the terminal panel renders once and no error appears', async ({ page }) => {
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE, latencyToggle(IN_FLIGHT_MS)]);

    await acknowledge(page);
    await page.locator(CONFIRM).dblclick();

    const terminal = page.locator(ENABLED);
    await expect(terminal).toHaveCount(1);
    await expect(terminal).toBeVisible();
    await expect(page.locator(ERROR)).toHaveCount(0);
  });
});

test.describe('the step 5 page is WCAG 2.2 AA clean (09.1.1 AC6, plan §8)', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`step 5, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await openStepFive(page, locale, [ENABLE_GRANTS_TOGGLE]);

        // The scanned surface is the whole step: the preview prose, the "not gating yet"
        // statement and the confirm are the controls AC5 adds.
        await expect(page.locator(POLICY_PREVIEW)).toBeVisible();
        await expect(page.locator(NOT_GATING)).toBeVisible();
        await expect(page.locator(CONFIRM)).toBeVisible();
        // The acknowledgement row is part of the scanned surface: its label association and the
        // warning it points at are what the scan must not flag (D-S104-Q2).
        await expect(page.locator(ACKNOWLEDGE)).toBeVisible();
        await expect(page.locator(REPLACES_WARNING)).toBeVisible();
        await expect(page.locator('#onboarding-step-title')).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('lang', locale);
        // The theme the label claims: a scan of the wrong token set would be a false pass.
        expect(await currentTheme(page)).toBe(theme);

        await expectNoA11yViolations(page, `onboarding step 5 ${locale}/${theme}`);
      });
    }
  }
});
