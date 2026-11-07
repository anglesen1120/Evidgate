// 09.2.1-T5 (plan 09.2.1 §2 AC5, §8 rows T98 and the two idempotency rows, §9 rows 123–124,
// §10 T5.1/T5.2): step 5's advisory-mode preview, its confirm and the terminal "project enabled
// and collecting" panel, in real Chromium against the FIXTURES build
// (`playwright.fixtures.config.ts`).
//
// Four rules shape what these cases assert:
//
// 1. **Step 5 is derived from its own read.** The page reads `GET
//    /v1/projects/{p}/policy-activation` on entry (09.2.1-T5.2, F18 addendum 1) and the wizard
//    derives `done[5]` from the same answer: a `200` completes the step and the panel below is
//    built from that document, its one `404` is the "never activated" state that leaves the step
//    open with the confirm, and a `403` leaves it open with the confirm natively disabled. The
//    seed `onboarding-evidence` pins the `404`, so the confirm states are reachable; the `=ok` and
//    `=enforcing` overrides reach the terminal-from-read states. Nothing here claims the step is
//    never done, and nothing here claims a request happened: see rule 4.
//
// 2. **The grant and the read are the state.** The confirm is offered only beside a `404` read and
//    enabled only for a session holding `policy:write`. The wizard's default grants
//    (`permissions-list-onboarding`, seven codes) do not hold it, so the control is natively
//    `disabled` and the "ask your release approver" copy is rendered; the lone org creator holds
//    `release_approver` from org creation (T98), which is `permissions-list-onboarding-enable`
//    (the same seven codes plus `policy:write`). D-S104-Q2's acknowledgement checkbox is withdrawn:
//    the warning stays as the confirm's description, not as a gate.
//
// 3. **The corpus answers the offers, the page renders the answer.** Every activation read and
//    every activation answer is a document (`policy-activation-read-*`, `policy-activation-ok`,
//    `-unchanged`, `-invalid-transition`, `-forbidden`); the values and the refusal text asserted
//    below are read from those documents, never retyped, so a corpus edit cannot leave an assertion
//    measuring something else. The page renders the shared `app-error-pattern` for every failure
//    kind (its own choice, `enable-collection.page.html`) — not the generic forbidden panel — so a
//    refused activation shows the envelope's message, code and request id too. A `403` *read* is
//    not a failure of the page: it renders as an unreadable state beside the disabled confirm.
//
// 4. **What the browser cannot observe, this suite does not claim.** The `fixtures` build answers
//    the API in process — `provideFixtureBackend()` replaces Angular's `HttpBackend`
//    (`src/app/core/dev-fixtures/fixture-backend.ts`) — so no network request exists for Playwright
//    to intercept, hold or count: `page.route` and `page.on('request')` see none (the same reason
//    `settings-members.qa.spec.ts:1-6` sends retry/double-submit to Vitest). The read-derived
//    states ARE observable, because they are rendered state; how many reads a visit issues, which
//    `Idempotency-Key` a confirm reuses and that a second click sends nothing are zero-request
//    proofs and stay in the page's own Vitest spec (`steps/enable-collection.page.spec.ts`).
import { expect, test, type Page } from '@playwright/test';

import activationForbidden from '../../../../fixtures/web-api/policy-activation-forbidden/input.json';
import activationInvalidTransition from '../../../../fixtures/web-api/policy-activation-invalid-transition/input.json';
import activationReadEnforcing from '../../../../fixtures/web-api/policy-activation-read-enforcing/input.json';
import activationReadOk from '../../../../fixtures/web-api/policy-activation-read-ok/input.json';
import {
  activationReadToggle,
  activationVariantToggle,
  ENABLE_GRANTS_TOGGLE,
  expectCurrentStep,
  latencyToggle,
  onboardingPath,
  seedQueryWith,
  shell,
  type OnboardingSeed,
} from './onboarding-support';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  currentTheme,
  LOCALES,
  seedStoredTheme,
  THEMES,
  withVariant,
  type ShellLocale,
} from './support';

/** The seed whose derivation leaves step 5 current (a run with an evidence revision, no policy). */
const STEP_FIVE_SEED: OnboardingSeed = 'onboarding-evidence';

/** The same wizard with the activation read answering `200` (`policy-activation-read-ok`). */
const ENABLED_SEED: OnboardingSeed = 'onboarding-enabled';

/** The step-5 page root; every state below renders inside it. */
const ENABLE_ROOT = '[data-testid="enable-collection"]';
/** The read-only preview of the policy body the step would activate. */
const POLICY_PREVIEW = '[data-testid="enable-collection-policy-preview"]';
/** The explicit "not gating yet / advisory mode" statement AC5 requires. */
const NOT_GATING = '[data-testid="enable-collection-not-gating"]';
/** The confirm control: a real `<button>`, offered only beside a `404` read. */
const CONFIRM = '[data-testid="enable-collection-confirm"]';
/** The replace-policy warning, which names the hazard and is the confirm's own description. */
const REPLACES_WARNING = '[data-testid="enable-collection-replaces-active"]';
/** The "ask your release approver" copy, rendered only for a session without `policy:write`. */
const ASK_APPROVER = '[data-testid="enable-collection-ask-approver"]';
/** The unreadable-read copy, rendered only beside a `403` activation read. */
const UNREADABLE = '[data-testid="enable-collection-unreadable"]';
/** The terminal panel: the project is enabled and collecting. */
const ENABLED = '[data-testid="enable-collection-enabled"]';
/** The page's typed error panel (a refused activation). */
const ERROR = '[data-testid="enable-collection-error"]';
/**
 * The terminal panel's `<dl>` cells, in template order: project, policy revision, revision hash,
 * mode, activated by, activated at. Selected positionally on purpose — every `<dt>` is a localized
 * label, and a spec keyed on one would be an `en` spec only.
 */
const TERMINAL_CELLS = `${ENABLED} > dl > div`;
/** The advisory-mode sentence the terminal panel carries only for an `advisory` read. */
const TERMINAL_ADVISORY = `${ENABLED} [data-testid="enable-collection-terminal-advisory"]`;

/** How much of the digest the panel shows (`default-policy.ts`, `HASH_ABBREVIATION_LENGTH`). */
const HASH_ABBREVIATION_LENGTH = 12;

/**
 * The hash as the terminal panel renders it: the `sha256:` prefix plus the first twelve digest
 * characters and an ellipsis, or the whole value when the digest is already that short. Computed
 * here from the fixture's own hash rather than exported from the app, so the panel's claim is
 * checked against the document instead of against the code that made it.
 */
function abbreviatedHash(hash: string): string {
  const prefix = hash.startsWith('sha256:') ? 'sha256:' : '';
  const digest = hash.slice(prefix.length);
  return digest.length <= HASH_ABBREVIATION_LENGTH
    ? hash
    : `${prefix}${digest.slice(0, HASH_ABBREVIATION_LENGTH)}…`;
}

/** One `<dd>` of the terminal panel's metadata list, by its template position. */
function terminalCell(page: Page, index: number) {
  return page.locator(TERMINAL_CELLS).nth(index).locator('dd');
}

/**
 * Opens the step-5 address of one locale and waits for the wizard to resolve that step.
 * `seedQueryWith` appends after the seed's own toggles and a later `fixture.variant.<operation>`
 * wins (`fixture-toggles.ts` builds a `Map` from the URL), so `ENABLE_GRANTS_TOGGLE` replaces the
 * wizard's default grant set instead of colliding with it, and `activationReadToggle('ok')`
 * overrides the seed's own `not-found` pin.
 */
async function openStepFive(
  page: Page,
  locale: ShellLocale,
  extra: readonly string[] = [],
  seed: OnboardingSeed = STEP_FIVE_SEED,
): Promise<void> {
  const query = seedQueryWith(seed, extra);
  await page.goto(withVariant(onboardingPath(locale), query));
  await expect(shell(page)).toBeVisible();
  await expectCurrentStep(page, 5);
  await expect(page.locator(ENABLE_ROOT)).toBeVisible();
}

/**
 * D-S104-Q2's hazard acknowledgement was withdrawn with the read (F18 addendum 1): the confirm is
 * derived from the activation read and the grant alone, so the checkbox must not be rendered in
 * any state — not beside an offered confirm, not beside a disabled one.
 */
async function expectNoAcknowledgement(page: Page): Promise<void> {
  await expect(page.locator('[data-testid="enable-collection-acknowledge"]')).toHaveCount(0);
}

/**
 * The terminal panel a `getPolicyActivation` 200 renders. Every value it shows is the read's own —
 * the project, the revision, the abbreviated hash, the mode, who activated it and when — because
 * nothing was confirmed on this visit, and it shows no confirm and no preview: the preview belongs
 * to the confirm the server's answer makes unnecessary.
 */
async function expectTerminalFromRead(
  page: Page,
  read: {
    readonly body: {
      readonly project_id: string;
      readonly policy_revision_id: string;
      readonly hash: string;
      readonly mode: string;
      readonly activated_by: string;
      readonly activated_at: string;
    };
  },
): Promise<void> {
  const terminal = page.locator(ENABLED);
  await expect(terminal).toBeVisible();
  await expect(terminal).toHaveCount(1);
  // Six cells: the read carries no `previous_policy_revision_id`, and the appended-revision row of
  // a confirm is never rendered here because no revision was appended.
  await expect(page.locator(TERMINAL_CELLS)).toHaveCount(6);
  await expect(terminalCell(page, 0)).toHaveText(read.body.project_id);
  await expect(terminalCell(page, 1)).toHaveText(read.body.policy_revision_id);
  await expect(terminalCell(page, 2)).toHaveText(abbreviatedHash(read.body.hash));
  if (read.body.mode === 'advisory') {
    // `advisory` is the one mode with a translated label, so the cell is asserted non-empty and the
    // mode is proven by the sentence only that mode renders.
    expect((await terminalCell(page, 3).innerText()).trim()).not.toBe('');
    await expect(page.locator(TERMINAL_ADVISORY)).toBeVisible();
  } else {
    // Every other server mode is rendered verbatim, so the assertion needs no bundle knowledge.
    await expect(terminalCell(page, 3)).toHaveText(read.body.mode);
    await expect(page.locator(TERMINAL_ADVISORY)).toHaveCount(0);
  }
  await expect(terminalCell(page, 4)).toHaveText(read.body.activated_by);
  const instant = terminalCell(page, 5).locator('time');
  await expect(instant).toHaveAttribute('datetime', read.body.activated_at);
  expect((await instant.innerText()).trim()).not.toBe('');
  // An active project offers nothing to confirm and nothing to preview.
  await expect(page.locator(CONFIRM)).toHaveCount(0);
  await expect(page.locator(POLICY_PREVIEW)).toHaveCount(0);
  await expect(page.locator(ERROR)).toHaveCount(0);
}

test.describe('step 5 previews the policy in advisory mode (AC5, T5.1)', () => {
  test('the preview, the "not gating yet" statement and an enabled confirm render together', async ({
    page,
  }) => {
    // The lone org creator's grants (T98): the same seed, one code more. The seed's own `404` read
    // is what makes the confirm permitted, and the grant is what makes it enabled.
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

    // The control is a real button, and with the read answering `404` and `policy:write` held it is
    // enabled: nothing else gates the press (D-S104-Q2's checkbox is withdrawn).
    const confirm = page.locator(CONFIRM);
    await expect(confirm).toBeVisible();
    expect(await confirm.evaluate((control) => control.tagName)).toBe('BUTTON');
    await expect(confirm).toBeEnabled();
    await expectNoAcknowledgement(page);

    // The hazard is still stated, and it is exactly the confirm's description: no permission and
    // no unreadable-read reason belongs to this state.
    const warning = page.locator(REPLACES_WARNING);
    await expect(warning).toBeVisible();
    expect((await warning.innerText()).trim()).not.toBe('');
    await expect(confirm).toHaveAttribute('aria-describedby', 'enable-replaces-warning');

    // Nothing has been read as activated, so no terminal panel; the ask-approver copy belongs to
    // the disabled state alone.
    await expect(page.locator(ENABLED)).toHaveCount(0);
    await expect(page.locator(ERROR)).toHaveCount(0);
    await expect(page.locator(ASK_APPROVER)).toHaveCount(0);
  });

  test('a session without `policy:write` sees a natively disabled control and the ask-approver copy', async ({
    page,
  }) => {
    // The seed's own grants (`permissions-list-onboarding`, seven codes, no `policy:write`): the
    // state of the session whose `release_approver` role was reassigned away (plan §9 row 123).
    await openStepFive(page, 'en');

    // The preview stays readable — it is a read, not a write.
    await expect(page.locator(POLICY_PREVIEW)).toBeVisible();
    await expect(page.locator(NOT_GATING)).toBeVisible();

    // AC-F18-d: disabled natively, not dressed as disabled. A keyboard or assistive-tech user must
    // not be able to fire an activation the session may not perform, so no submit can happen at all,
    // and the button stays enabled-proof: no forced click, no `aria-disabled` imitation.
    const confirm = page.locator(CONFIRM);
    await expect(confirm).toBeVisible();
    expect(await confirm.evaluate((control) => control.tagName)).toBe('BUTTON');
    await expect(confirm).toBeDisabled();

    // …and the reason is rendered beside it, non-empty, and named by the button's own description
    // so a screen reader reads the reason when it reaches the disabled control.
    const askApprover = page.locator(ASK_APPROVER);
    await expect(askApprover).toBeVisible();
    expect((await askApprover.innerText()).trim()).not.toBe('');
    await expect(page.locator('#enable-permission')).toHaveCount(1);
    await expect(confirm).toHaveAttribute('aria-describedby', /enable-permission/);
    await expect(confirm).toHaveAttribute('aria-describedby', /enable-replaces-warning/);
    await expect(confirm).not.toHaveAttribute('aria-describedby', /enable-activation-unreadable/);
    await expectNoAcknowledgement(page);

    // The `404` read would have permitted the confirm and the seeded answer would have been `ok`:
    // the missing grant is the whole reason nothing happened, so no terminal panel and no error.
    await expect(page.locator(ENABLED)).toHaveCount(0);
    await expect(page.locator(ERROR)).toHaveCount(0);
  });
});

test.describe('a 403 activation read disables the confirm (AC-T5.2-3)', () => {
  test('the unreadable-state reason is rendered and no terminal panel appears', async ({
    page,
  }) => {
    // `policy-activation-read-forbidden`: the principal may render the step and confirm nothing,
    // because the page never learned whether an activation exists. The grant is the creator's, so
    // the read is the only thing standing between the user and the press.
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE, activationReadToggle('forbidden')]);

    const confirm = page.locator(CONFIRM);
    await expect(confirm).toBeVisible();
    await expect(confirm).toBeDisabled();

    // The reason is the unreadable read, rendered and described by the disabled control; the
    // permission copy stays absent, because the grant is held.
    const unreadable = page.locator(UNREADABLE);
    await expect(unreadable).toBeVisible();
    expect((await unreadable.innerText()).trim()).not.toBe('');
    await expect(page.locator('#enable-activation-unreadable')).toHaveCount(1);
    await expect(confirm).toHaveAttribute('aria-describedby', /enable-activation-unreadable/);
    await expect(confirm).toHaveAttribute('aria-describedby', /enable-replaces-warning/);
    await expect(confirm).not.toHaveAttribute('aria-describedby', /enable-permission/);
    await expect(page.locator(ASK_APPROVER)).toHaveCount(0);
    await expectNoAcknowledgement(page);

    // A refused read is a state, not a failed load: the shared error panel stays away, and no
    // terminal panel can be claimed from a read that was never answered.
    await expect(page.locator(ENABLED)).toHaveCount(0);
    await expect(page.locator(ERROR)).toHaveCount(0);
  });
});

test.describe('step 5 completes from its read, and the confirm activates it (AC5, T5.2)', () => {
  test('the confirm reaches the terminal "enabled and collecting" panel', async ({ page }) => {
    // `permissions-list-onboarding-enable` holds `policy:write`; `activatePolicyRevision` answers
    // its `ok` document (200, `changed: true`, mode `advisory`).
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE]);

    await page.locator(CONFIRM).click();

    const terminal = page.locator(ENABLED);
    await expect(terminal).toBeVisible();
    // The panel is the step's terminal state, not a second preview: one panel and no error.
    await expect(terminal).toHaveCount(1);
    await expect(page.locator(ERROR)).toHaveCount(0);
    // QA-01: the confirm the user pressed is gone, so focus must not be dropped on `<body>`.
    await expect(page.locator('#enable-enabled-heading')).toBeFocused();
  });

  test('a replay answer (`changed: false`) is a success, and a reload offers the confirm again only under the 404 read', async ({
    page,
  }) => {
    // `policy-activation-unchanged` is the contract's replay: same revision, same hash,
    // `changed: false`. The seed's read stays the `404`, so after a reload the wizard is back on
    // step 5 with the confirm — because the server still says "never activated", not because the
    // page forgot anything — and the second confirm is answered with the same replay.
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE, activationVariantToggle('unchanged')]);

    await page.locator(CONFIRM).click();
    await expect(page.locator(ENABLED)).toBeVisible();
    await expect(page.locator(ERROR)).toHaveCount(0);

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 5);
    // Not restored: this project's read answers `404`, so nothing on the server completes step 5.
    // The confirm is back — and permitted again by the same read, with no acknowledgement to tick.
    await expect(page.locator(ENABLED)).toHaveCount(0);
    await expect(page.locator(CONFIRM)).toBeVisible();
    await expect(page.locator(CONFIRM)).toBeEnabled();
    await expect(page.locator(CONFIRM)).toHaveAttribute(
      'aria-describedby',
      'enable-replaces-warning',
    );
    await expectNoAcknowledgement(page);

    await page.locator(CONFIRM).click();
    await expect(page.locator(ENABLED)).toBeVisible();
    await expect(page.locator(ERROR)).toHaveCount(0);
  });

  test("the terminal panel is restored from the read, with the read's own values, before and after a reload", async ({
    page,
  }) => {
    // `policy-activation-read-ok` is the answer of a project that IS activated: the read completes
    // step 5, so the panel is built from the document and there is nothing to confirm. Read and
    // reload must render the same panel, because it never depended on this visit.
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE, activationReadToggle('ok')]);
    await expectTerminalFromRead(page, activationReadOk);

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 5);
    await expectTerminalFromRead(page, activationReadOk);
  });

  test('a mode the wizard never sends is reported verbatim, without the advisory sentence', async ({
    page,
  }) => {
    // `policy-activation-read-enforcing`: the same pointer in `enforcing` mode. The panel reports
    // the wire value and must not describe it as advisory or as gating on the server's behalf —
    // that is why the mode cell is asserted verbatim and the advisory sentence is absent.
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE, activationReadToggle('enforcing')]);
    await expectTerminalFromRead(page, activationReadEnforcing);
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
    // forbidden panel), so the envelope is asserted verbatim, as for the 409. This is the command's
    // 403, not the read's: `policy-activation-read-forbidden` is the other state, asserted above.
    await openStepFive(page, 'en', [ENABLE_GRANTS_TOGGLE, activationVariantToggle('forbidden')]);

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
      test(`step 5 confirm, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await openStepFive(page, locale, [ENABLE_GRANTS_TOGGLE]);

        // The scanned surface is the whole step: the preview prose, the "not gating yet"
        // statement, the hazard warning and the offered confirm are the controls AC5 leaves.
        await expect(page.locator(POLICY_PREVIEW)).toBeVisible();
        await expect(page.locator(NOT_GATING)).toBeVisible();
        await expect(page.locator(REPLACES_WARNING)).toBeVisible();
        await expect(page.locator(CONFIRM)).toBeVisible();
        await expect(page.locator(CONFIRM)).toBeEnabled();
        await expect(page.locator('#onboarding-step-title')).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('lang', locale);
        // The theme the label claims: a scan of the wrong token set would be a false pass.
        expect(await currentTheme(page)).toBe(theme);

        await expectNoA11yViolations(page, `onboarding step 5 confirm ${locale}/${theme}`);
      });

      test(`step 5 terminal from the read, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        // The state the read produces on its own: heading, intro sentence, the metadata list and,
        // for an `advisory` activation, the not-gating sentence — no confirm and no preview.
        await openStepFive(page, locale, [], ENABLED_SEED);

        await expect(page.locator(ENABLED)).toBeVisible();
        await expect(page.locator(TERMINAL_ADVISORY)).toBeVisible();
        await expect(page.locator(CONFIRM)).toHaveCount(0);
        await expect(page.locator('#onboarding-step-title')).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('lang', locale);
        expect(await currentTheme(page)).toBe(theme);

        await expectNoA11yViolations(page, `onboarding step 5 terminal ${locale}/${theme}`);
      });
    }
  }
});
