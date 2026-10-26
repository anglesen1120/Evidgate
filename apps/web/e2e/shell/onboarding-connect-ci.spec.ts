// 09.2.1-T2 (plan 09.2.1 §2 AC2, AC7, §8 "Secret handling", §10 T2.1/T2.2): step 2's two connect
// paths, its one-time values and its metadata-only resume, in real Chromium against the FIXTURES
// build (`playwright.fixtures.config.ts`).
//
// Three rules shape what these cases assert:
//
// 1. **A one-time value is proved by its footprint, not by its presence.** `secretFootprint` scans
//    every attribute, every form control's `value` property (Angular's `[value]` sets the property,
//    not the attribute), every rendered text node, both storages, `document.cookie`, the address bar
//    and `history.state`. `expectSecretOnlyInReveal` then requires the value to be held by exactly
//    one element inside the slot while it is open; `expectSecretAbsent` requires no holder and no
//    entry anywhere once it closes. A value that leaked into an `aria-label`, a second hidden field
//    or `localStorage` would pass every positive assertion, so this footprint is the test of §8.
// 2. **The fixtures build answers in-process.** The dev backend is installed as Angular's
//    `HttpBackend` (`src/app/core/dev-fixtures/fixture-backend.ts`), so the SDK's requests never
//    reach the network: `page.route` cannot intercept a create and `page.on('request')` sees none.
//    Every create path in `ConnectCiPage` ends in exactly one render — the `connect-reveal` slot
//    for a 2xx, `connect-ci-error` for a >= 400 — so "no create was sent" is asserted as "neither
//    render is present", and which answer the server gave is asserted against the corpus document
//    that answered it.
// 3. **The corpus is static.** A create's answer cannot add a row to the list read that follows it
//    (plan §10 T2 Notes), so these cases prove the reveal and the *seeded* metadata. The exact
//    request body is the component spec's assertion (`steps/connect-ci.page.spec.ts`, "creates OIDC
//    once with the exact numeric/trimmed SDK body"); here the form is proved to survive the submit
//    unchanged and to reach the server, because the value the reveal shows is the response body's
//    own one-time field. Request *counting* ("no create on load", "no automatic retry") is asserted
//    through the same two renders, since the in-process backend offers no network seam to count.
import { expect, test, type Page } from '@playwright/test';

import {
  connectCiPage,
  expectCurrentStep,
  expectNoReveal,
  expectNoStepInStorage,
  expectSecretAbsent,
  expectSecretOnlyInReveal,
  FIXTURE_BINDING_CHALLENGE,
  FIXTURE_ROTATED_TOKEN_SECRET,
  FIXTURE_TOKEN_SECRET,
  nextButton,
  NO_WRITE_GRANTS_TOGGLE,
  onboardingPath,
  onboardingUrl,
  revealSlot,
  revealValue,
  seedQueryWith,
  shell,
  stepItem,
  writeVariantToggle,
} from './onboarding-support';
import { withVariant } from './support';

/** The name typed into the token form; the corpus echoes its own name back, never this one. */
const TOKEN_NAME = 'CI upload (e2e)';

/**
 * The OIDC form's values, as `ci-binding-create/input.json` records the binding. The workflow ref is
 * the `job_workflow_ref` shape the real API validates
 * (`src/Modules/Evidgate.Modules.Identity/Ci/RefGlob.cs:77`, contracts/openapi.yaml:6654) — the
 * `refs/heads/main:<path>` spelling the T1 list rows still carry would be a 422 `invalid` server
 * side, so this spec never types it.
 */
const REPOSITORY_ID = '5012345';
const REPOSITORY_NAME = 'acme/storefront-web';
const REF_PATTERNS = 'refs/heads/main';
const WORKFLOW_REFS = 'acme/storefront-web/.github/workflows/evidgate.yml@refs/heads/main';

/** The step-2 page's own failure panel: the only other render a create can produce. */
function ciError(page: Page) {
  return page.locator('[data-testid="connect-ci-error"]');
}

/** Fills and submits the OIDC form; the caller asserts the answer. */
async function createBinding(page: Page): Promise<void> {
  await page.locator('[data-testid="connect-oidc-repo-id"]').fill(REPOSITORY_ID);
  await page.locator('[data-testid="connect-oidc-repo-name"]').fill(REPOSITORY_NAME);
  await page.locator('[data-testid="connect-oidc-refs"]').fill(REF_PATTERNS);
  await page.locator('[data-testid="connect-oidc-workflows"]').fill(WORKFLOW_REFS);
  await page.locator('[data-testid="connect-oidc-submit"]').click();
}

/** Fills and submits the token form; the caller asserts the answer. */
async function createToken(page: Page): Promise<void> {
  await page.locator('[data-testid="connect-token-name"]').fill(TOKEN_NAME);
  await page.locator('[data-testid="connect-token-submit"]').click();
}

test.describe('step 2 renders the connect page (plan §10 T2)', () => {
  test('the connect page replaces the T1 placeholder, with Next still gated', async ({ page }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);

    // The page T2 ships, not the placeholder steps 3–5 still render while T3…T5 own them.
    await expect(connectCiPage(page)).toBeVisible();
    await expect(page.locator('[data-testid="onboarding-step-pending"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="connect-oidc-panel"]')).toBeVisible();
    await expect(page.locator('[data-testid="connect-token-panel"]')).toBeVisible();

    // The seed holds no binding and no token, so step 2 is not done and Next must stay disabled:
    // an enabled Next here would let a reader walk past work nobody did (AC2).
    await expect(nextButton(page)).toBeDisabled();
    await expectNoReveal(page);
    await expect(ciError(page)).toHaveCount(0);
  });
});

test.describe('the token path reveals its secret exactly once (AC2, §8)', () => {
  test('a created token is revealed, copied, and cleared by Dismiss', async ({ page, context }) => {
    // The reveal is only useful if Copy works, and a clipboard read needs the grant.
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(connectCiPage(page)).toBeVisible();

    await createToken(page);

    // The one-time value arrives in the slot wrapper — the single subtree §8 allows it in.
    await expect(revealSlot(page)).toBeVisible();
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
    await expect(connectCiPage(page)).toBeVisible();
    await createToken(page);
    await expect(revealValue(page)).toHaveValue(FIXTURE_TOKEN_SECRET);

    // Leaving and re-entering the step destroys the page component, and the `RevealSlot` is
    // component-scoped, so the revisited step cannot hold the value the first one was given.
    await stepItem(page, 1).locator('button').click();
    await expectCurrentStep(page, 1);
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'step 1');
    await stepItem(page, 2).locator('button').click();
    await expectCurrentStep(page, 2);
    await expect(connectCiPage(page)).toBeVisible();
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the revisited step 2');

    // A fresh boot derives the step from server metadata; a server that returns a secret once can
    // never re-reveal it, so the reload must show the page with no value at all.
    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the reload');
    await expectNoStepInStorage(page, 'the reload');
  });
});

test.describe('the OIDC path reveals the challenge once (AC2, §8)', () => {
  test('a created binding reveals its challenge and keeps the posted form', async ({ page }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(connectCiPage(page)).toBeVisible();

    await createBinding(page);

    await expect(revealValue(page)).toHaveValue(FIXTURE_BINDING_CHALLENGE);
    await expect(revealSlot(page).getByRole('heading')).toBeFocused();
    await expectSecretOnlyInReveal(page, FIXTURE_BINDING_CHALLENGE, 'the open challenge');
    await expectNoStepInStorage(page, 'after the binding create');
    // The reveal holds the response body's own `binding_challenge`, so the create did reach the
    // server with input every client validator accepted: no field reported an error.
    await expect(ciError(page)).toHaveCount(0);
    for (const id of [
      'connect-repo-id-error',
      'connect-repo-name-error',
      'connect-refs-error',
      'connect-workflows-error',
    ]) {
      await expect(page.locator(`#${id}`)).toHaveText('');
    }
    // The form still shows what it posted (`createCiBinding`'s body is built from these values at
    // submit time), and the repository is not editable behind the open reveal.
    await expect(page.locator('[data-testid="connect-oidc-repo-id"]')).toHaveValue(REPOSITORY_ID);
    await expect(page.locator('[data-testid="connect-oidc-repo-name"]')).toHaveValue(
      REPOSITORY_NAME,
    );
    await expect(page.locator('[data-testid="connect-oidc-refs"]')).toHaveValue(REF_PATTERNS);
    await expect(page.locator('[data-testid="connect-oidc-workflows"]')).toHaveValue(WORKFLOW_REFS);

    await page.locator('[data-testid="reveal-dismiss"]').click();
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_BINDING_CHALLENGE, 'after Dismiss');
    await expect(page.locator('#connect-oidc-heading')).toBeFocused();
    // Same static-corpus caveat as the token path: no row may be fabricated from the create.
    await expect(page.locator('[data-testid="connect-binding-row"]')).toHaveCount(0);
  });
});

test.describe('the resume is metadata-only (AC2, AC7)', () => {
  test('a token makes step 3 the resume point, and step 2 shows metadata without a secret', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-token'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);
    // Nothing on load creates anything: neither render a create can produce is present, and no
    // error is. (The fixtures build answers in-process, so there is no request to listen for.)
    await expectNoReveal(page);
    await expect(ciError(page)).toHaveCount(0);

    await page.locator('[data-testid="onboarding-back"]').click();
    await expectCurrentStep(page, 2);
    await expect(connectCiPage(page)).toBeVisible();

    const row = page.locator('[data-testid="connect-token-row"]');
    await expect(row).toHaveCount(1);
    await expect(row).toHaveAttribute('data-status', 'active');
    // The server's metadata only: the prefix it stores, never a value it has already handed out.
    await expect(row.locator('code')).toHaveText('egt_fixture');
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the resumed step 2');
    await expectSecretAbsent(page, FIXTURE_ROTATED_TOKEN_SECRET, 'the resumed step 2');
    await expectNoReveal(page);
    // Step 2 is done on this seed, so the wizard may advance: the resume is a real, gated step.
    await expect(nextButton(page)).toBeEnabled();
  });

  test('a binding row resumes with its server status and never a challenge (AC7)', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-connected'));
    await expectCurrentStep(page, 3);
    await page.locator('[data-testid="onboarding-back"]').click();
    await expectCurrentStep(page, 2);

    const row = page.locator('[data-testid="connect-binding-row"]');
    await expect(row).toHaveCount(1);
    await expect(row).toHaveAttribute('data-status', 'active');
    await expect(row).toContainText(REPOSITORY_NAME);
    await expect(row.locator('time')).toHaveAttribute('datetime', '2026-10-20T07:30:00Z');
    await expectSecretAbsent(page, FIXTURE_BINDING_CHALLENGE, 'the resumed step 2');
    await expectNoReveal(page);
    await expect(nextButton(page)).toBeEnabled();
  });

  test('a reload on step 2 with empty lists stays on step 2 and reveals nothing', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expectCurrentStep(page, 2);
    await expectNoReveal(page);

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);
    await expect(connectCiPage(page)).toBeVisible();
    await expectNoReveal(page);
    await expect(ciError(page)).toHaveCount(0);
    await expect(page.locator('[data-testid="connect-token-row"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="connect-binding-row"]')).toHaveCount(0);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'the reload of an empty step 2');
  });
});

test.describe('rotating a token reveals its successor once (AC2)', () => {
  test('Rotate reveals the new secret and the overlap end, and Dismiss clears the secret', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-token'));
    await expectCurrentStep(page, 3);
    await page.locator('[data-testid="onboarding-back"]').click();
    await expectCurrentStep(page, 2);

    const rotate = page.locator('[data-testid="connect-token-rotate"]');
    await expect(rotate).toBeEnabled();
    await rotate.click();

    await expect(revealValue(page)).toHaveValue(FIXTURE_ROTATED_TOKEN_SECRET);
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

test.describe('a session without the write grants cannot connect (AC2, §8)', () => {
  test('both submit paths are disabled with a localized reason in aria-describedby', async ({
    page,
  }) => {
    // `permissions-list-onboarding-no-writes`: the wizard's own catalogue minus `ci_binding:write`
    // and `ingest_token:write` (and, since 09.2.1-T5, plus `policy:write`), keeping
    // `project:create` so the route itself still opens.
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-project', [NO_WRITE_GRANTS_TOGGLE]),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);
    await expect(connectCiPage(page)).toBeVisible();

    // The control is natively disabled, not dressed as disabled: a keyboard or assistive-tech
    // user must not be able to submit a create the session may not perform.
    const bindingSubmit = page.locator('[data-testid="connect-oidc-submit"]');
    await expect(bindingSubmit).toBeDisabled();
    expect(await bindingSubmit.evaluate((control) => control.tagName)).toBe('BUTTON');
    await expect(bindingSubmit).toHaveAttribute('aria-describedby', 'connect-binding-permission');
    // And the reason it points at is rendered, non-empty (the bundle owns the wording).
    expect((await page.locator('#connect-binding-permission').innerText()).trim()).not.toBe('');
    await expect(page.locator('[data-testid="connect-oidc-repo-id"]')).toBeDisabled();

    const ingestSubmit = page.locator('[data-testid="connect-token-submit"]');
    await expect(ingestSubmit).toBeDisabled();
    await expect(ingestSubmit).toHaveAttribute('aria-describedby', 'connect-token-permission');
    expect((await page.locator('#connect-token-permission').innerText()).trim()).not.toBe('');
    await expect(page.locator('[data-testid="connect-token-name"]')).toBeDisabled();

    // No create happened and none can: nothing to reveal, no error, and Next still gated.
    await expectNoReveal(page);
    await expect(ciError(page)).toHaveCount(0);
    await expect(nextButton(page)).toBeDisabled();
  });
});

test.describe('a refused create shows the server error verbatim (§8, 00 §9.2)', () => {
  /**
   * `fixtures/web-api/ci-binding-create-conflict`: a 409 in the 00 §9.2 envelope, answered through
   * the same typed error path a real server refusal takes (`ApiGateway` → `isApiRequestError` → the
   * page's `failure` signal), with `retryable: false` because a secret-bearing create is never
   * replayed. The binding path stands for both creates: they share `perform`.
   */
  const REFUSAL_MESSAGE =
    'a CI binding for acme/storefront-web already exists; disable it before adding another';

  test('a conflict is rendered, the controls come back, and no secret appears', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-project', [writeVariantToggle('createCiBinding', 'conflict')]),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);
    await expect(connectCiPage(page)).toBeVisible();

    await createBinding(page);

    const error = ciError(page);
    await expect(error).toBeVisible();
    // Verbatim: the page echoes the envelope's `message` and the pattern shows its `code`.
    await expect(error.getByRole('alert')).toHaveCount(1);
    await expect(error.getByText(REFUSAL_MESSAGE, { exact: true })).toBeVisible();
    await expect(error.locator('code')).toHaveText('binding_exists');
    // …and the envelope's `request_id` reaches the reader, which is the support path 00 §9.2 asks
    // for. It is the document's own value, not the per-call `X-Request-Id` header
    // (`error-mapper.ts` reads `body.request_id`), so the same id would come back from a repeated
    // submission: "no automatic retry" therefore rests on the remount and reload assertions below,
    // never on this value.
    await expect(error.locator('[data-testid="error-request-id"]')).toContainText(
      '018f0000-0000-7000-8000-0000000000e4',
    );
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_BINDING_CHALLENGE, 'the refused create');

    // The attempt is over: not busy, and the form accepts a deliberate second submit.
    await expect(connectCiPage(page)).toHaveAttribute('aria-busy', 'false');
    await expect(page.locator('[data-testid="connect-oidc-repo-id"]')).toBeEnabled();
    await expect(page.locator('[data-testid="connect-oidc-submit"]')).toBeEnabled();
    // No retry affordance, because the envelope says `retryable: false`: the only way to send the
    // create again is a user submit, never a timer or a remount.
    await expect(error.locator('button')).toHaveCount(0);

    // A step change and a reload must not re-send it: a remounted page starts from the server's
    // metadata, and neither render a create can produce comes back.
    await stepItem(page, 1).locator('button').click();
    await expectCurrentStep(page, 1);
    await stepItem(page, 2).locator('button').click();
    await expectCurrentStep(page, 2);
    await expect(ciError(page)).toHaveCount(0);
    await expectNoReveal(page);

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);
    await expect(ciError(page)).toHaveCount(0);
    await expectNoReveal(page);
    await expectNoStepInStorage(page, 'the reload after a refusal');
  });
});
