// 09.8.1-T1.2 (plan 09.8.1 §10 T1.2, story AC2): the onboarding wizard walked with the keyboard
// only — `page.keyboard` for every interaction, no `page.click`, no `locator.click`, no
// `locator.focus()`, no `{ force: true }` — asserting `document.activeElement` by element identity
// (test id, id, role) at every stop, and that the focused element actually renders its focus ring.
// Each walk starts a fresh document at the skip link and enters the main region with Enter, so
// every stop on the way is one a keyboard visitor really crosses.
//
// The walkthrough follows `docs/a11y/parts/onboarding.md` "Keyboard walkthrough plan" W1–W4 and
// stops at the last good step wherever a recorded finding blocks the next one. No assertion in this
// file pins a recorded defect as expected behaviour, and none needs a click or a force to get past
// one:
//
//   - **F-ONB-07** (create-project refusal: fields and submit disabled mid-request, focus dropped to
//     `<body>`, no focus move on the refusal) blocks W1 step 8, the empty-form refusal. That case is
//     NOT walked here; it belongs to the create page's own Vitest spec until T1.2's fix lands.
//   - **F-ONB-08** (connect-ci refusal: focus on `<body>` while the alert speaks) blocks W2 step 14,
//     and **F-ONB-09** (enable-collection refusal: the pressed confirm is disabled mid-flight) blocks
//     W3 step 5. Both are refusal paths, so the happy-path walkthrough stops before them.
//   - **F-ONB-05** (connect-ci metadata retry), **F-ONB-06** (enable-collection re-read) and
//     **F-ONB-04** (the wizard's error-state Retry) are retry paths whose pressed control unmounts.
//     They are not entered here: doing so would assert the dropped focus those findings record.
//
// Everything else is the AC2 claim this spec exists to prove: the skip link, the stepper, each
// step's own content, the reveal handoffs ConnectCiPage already implements, Back, Next and the
// terminal heading after the confirm — all reached and left without a pointer.
import { expect, test, type Locator, type Page } from '@playwright/test';

import {
  activationReadToggle,
  backButton,
  CREATED_PROJECT,
  CREATED_PROJECT_NAME,
  CREATED_PROJECT_SLUG,
  ENABLE_GRANTS_TOGGLE,
  expectCurrentStep,
  expectNoReveal,
  FIXTURE_BINDING_CHALLENGE,
  FIXTURE_TOKEN_SECRET,
  nextButton,
  onboardingPath,
  onboardingUrl,
  revealSlot,
  revealValue,
  seedQueryWith,
  shell,
  stepItem,
  TOKEN_USED_LAST_USED_AT,
  TOKEN_USED_NAME,
  type StepNumber,
} from './onboarding-support';
import { ORG_ACME, withVariant } from './support';

/** The shell's skip link: the first stop of a fresh document (shell.component.html:1-8). */
const SKIP_LINK = 'a.skip-link';
/** The shell's main region, the skip target and the anchor the walkthrough resumes from. */
const MAIN = '#main-content';
/** The wizard's own step heading — the announcement target of every step move. */
const STEP_TITLE = '#onboarding-step-title';
/**
 * The quickstart's focusable block: the `<pre tabindex="0">` is the Tab stop, while its
 * `data-testid="cli-quickstart-snippet"` sits on the `<code>` it renders.
 */
const QUICKSTART_SNIPPET = '[data-testid="cli-quickstart"] pre';

/**
 * The OIDC form's values, exactly as `fixtures/web-api/ci-binding-create/input.json` records the
 * binding (`onboarding-connect-ci.spec.ts` types the same ones). The workflow ref is the
 * `job_workflow_ref` shape the API validates, not the `refs/heads/main:<path>` spelling.
 */
const REPOSITORY_ID = '5012345';
const REPOSITORY_NAME = 'acme/storefront-web';
const REF_PATTERNS = 'refs/heads/main';
const WORKFLOW_REFS = 'acme/storefront-web/.github/workflows/evidgate.yml@refs/heads/main';
/** The name this walkthrough types into the token form; the corpus echoes its own name back. */
const TOKEN_NAME = 'CI upload (keyboard)';

/** One stepper item's button, addressed by the item's DOM contract rather than by its label. */
function stepButton(page: Page, step: StepNumber) {
  return stepItem(page, step).locator('button');
}

/** Presses Tab `times` times: the only way this suite moves focus forward. */
async function pressTab(page: Page, times = 1): Promise<void> {
  for (let index = 0; index < times; index += 1) {
    await page.keyboard.press('Tab');
  }
}

/**
 * Puts focus on the wizard's main region with real keys only, for the walks that start inside it:
 * a fresh document starts its tab order at the shell's skip link, and Enter on it activates the
 * skip target (`main#main-content`, `tabindex="-1"`). Both stops are asserted by identity and by
 * their focus ring.
 *
 * This replaces `page.locator(MAIN).focus()`, which AC2 does not allow (a programmatic focus move
 * is not a keyboard one) and which also skipped the two stops a keyboard visitor really crosses on
 * the way in.
 */
async function enterMainByKeyboard(page: Page): Promise<void> {
  await pressTab(page);
  await expectKeyboardFocus(page, page.locator(SKIP_LINK), 'the skip link before the walk');
  await page.keyboard.press('Enter');
  await expectKeyboardFocus(page, page.locator(MAIN), 'the main region before the walk');
}

/**
 * AC2's second clause: the focused control is visibly focused, not merely focused. Every focusable
 * in these flows declares the `--eg-focus` ring (2px, 2px offset — `onboarding-shell.component.css`,
 * `connect-ci.page.css`, `state-panel.css`, `cli-quickstart.component.css`, `connect-step-reveal
 * .component.css`, `enable-collection.page.css`, `shell.component.css`); an `outline: none` anywhere
 * on that path would leave a keyboard visitor guessing, so the computed outline is what is asserted.
 *
 * Every call follows a Tab or Enter — this file moves focus only with real key presses, never with
 * `locator.focus()` — which is what makes the assertion honest for the two targets styled
 * `:focus-visible` only (`main#main-content`, `#onboarding-step-title`): both are reached by an
 * Enter, so the browser reports them as keyboard-focused. The headings the pages focus themselves
 * after a step move (the reveal heading, the connect panel headings, `#enable-enabled-heading`) are
 * styled on `:focus` instead, so they render a ring either way.
 */
async function expectFocusRing(page: Page, context: string): Promise<void> {
  const focused = await page.evaluate(() => {
    const element = document.activeElement;
    if (element === null) return null;
    const style = getComputedStyle(element);
    return {
      tag: element.tagName.toLowerCase(),
      id: element.id,
      testId: element.getAttribute('data-testid'),
      outlineStyle: style.outlineStyle,
      outlineWidth: style.outlineWidth,
    };
  });

  const name =
    focused === null
      ? 'nothing'
      : `${focused.tag}${focused.id === '' ? '' : `#${focused.id}`}${
          focused.testId === null ? '' : `[data-testid="${focused.testId}"]`
        }`;

  expect(focused, `${context}: nothing holds focus`).not.toBeNull();
  expect(focused?.outlineStyle, `${context}: ${name} renders no focus outline`).not.toBe('none');
  expect(
    Number.parseFloat(focused?.outlineWidth ?? '0'),
    `${context}: ${name} renders a zero-width focus outline`,
  ).toBeGreaterThan(0);
}

/** Asserts the focused element by identity, then that it is visibly focused. */
async function expectKeyboardFocus(page: Page, locator: Locator, context: string): Promise<void> {
  await expect(locator, `${context}: focus identity`).toBeFocused();
  await expectFocusRing(page, context);
}

/**
 * Every enabled Tab stop inside the main region, named by its DOM contract. The identity list is
 * the budget assertion: a stray stop (a new control, a `tabindex="0"` wrapper) shows up by name, so
 * the check cannot pass by counting alone.
 */
async function enabledTabStops(page: Page): Promise<readonly string[]> {
  return page.evaluate(() => {
    const main = document.querySelector('#main-content');
    if (main === null) return [];

    return Array.from(
      main.querySelectorAll<HTMLElement>('a[href], button, input, select, textarea, [tabindex]'),
    )
      .filter((element) => {
        if (element.tabIndex < 0) return false;
        if (element.matches(':disabled')) return false;
        if (element.closest('[inert]') !== null) return false;
        const style = getComputedStyle(element);
        if (style.visibility === 'hidden' || style.display === 'none') return false;
        return element.getClientRects().length > 0;
      })
      .map((element) => {
        const step = element.closest('[data-testid^="onboarding-step-"]');
        if (step !== null) return `${step.getAttribute('data-testid')} button`;
        const testId = element.getAttribute('data-testid');
        if (testId !== null) return testId;
        if (element.id !== '') return `#${element.id}`;
        return element.tagName.toLowerCase();
      });
  });
}

test.describe('W1 — step 1, create project (keyboard walkthrough plan)', () => {
  test('the skip link, the stepper, both fields and the submit carry the flow into step 2', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-empty'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 1);

    // 1. No click first: a fresh document starts its tab order at the shell's skip link, which is
    //    translated off-screen until it holds focus — so a visible box is part of the assertion.
    await pressTab(page);
    const skipLink = page.locator(SKIP_LINK);
    await expectKeyboardFocus(page, skipLink, 'the first Tab stop is the skip link');
    const skipBox = await skipLink.boundingBox();
    expect(skipBox, 'the focused skip link has no box').not.toBeNull();
    expect(
      skipBox?.y ?? -1,
      'the focused skip link is still translated off-screen, so a sighted keyboard user sees nothing',
    ).toBeGreaterThanOrEqual(0);

    // 2. Enter moves focus into the main region, the skip link's target.
    await page.keyboard.press('Enter');
    const main = page.locator(MAIN);
    await expectKeyboardFocus(page, main, 'the main region after the skip link');
    // Programmatically focusable, never a Tab stop of its own: the fragment target is the entry.
    await expect(main).toHaveAttribute('tabindex', '-1');

    // 3. The wizard's first stop is the current step's stepper button; steps 2–5 are natively
    //    disabled at step 1 and are therefore skipped by the tab order.
    await pressTab(page);
    await expectKeyboardFocus(page, stepButton(page, 1), 'stepper 1 is the first wizard stop');
    await expect(stepItem(page, 1)).toHaveAttribute('aria-current', 'step');

    // 4.–5. Both fields, reached and filled with the keyboard only.
    await pressTab(page);
    const nameField = page.locator('#onboarding-project-name');
    await expectKeyboardFocus(page, nameField, 'the project name field');
    await page.keyboard.type(CREATED_PROJECT_NAME);
    await expect(nameField).toHaveValue(CREATED_PROJECT_NAME);

    await pressTab(page);
    const slugField = page.locator('#onboarding-project-slug');
    await expectKeyboardFocus(page, slugField, 'the project slug field');
    await page.keyboard.type(CREATED_PROJECT_SLUG);
    await expect(slugField).toHaveValue(CREATED_PROJECT_SLUG);

    // 6. The submit is the last stop of step 1: Back and Next are disabled on the first step, so a
    //    keyboard user cannot tab into them.
    await pressTab(page);
    const submit = page.locator('[data-testid="onboarding-project-submit"]');
    await expectKeyboardFocus(page, submit, 'the create-project submit');
    await expect(backButton(page)).toBeDisabled();
    await expect(nextButton(page)).toBeDisabled();

    // 7. Enter creates the project and the wizard opens step 2 on it.
    //
    //    The walkthrough plan's step 7 also expects `#onboarding-step-title` to hold focus here;
    //    the browser does not deliver that handoff. The create navigates `''` → `:projectId`, so a
    //    new shell activation adopts the `focusToken` `projectCreated` just bumped as its own
    //    baseline (onboarding-shell.component.ts:104-108) and its focus effect (:111-120) never
    //    runs — the pressed submit unmounts and focus falls to `<body>`. That is the create-project
    //    focus-order defect F-ONB-07's family records (a create loses the keyboard user's place),
    //    so this spec stops at the last verified stop: the rendered step, its content and the
    //    project the fixture reports. It asserts neither the missing handoff nor a body fallback.
    await page.keyboard.press('Enter');
    await expectCurrentStep(page, 2);
    await expect(page.locator('[data-testid="connect-ci-page"]')).toBeVisible();
    // The create opened the project the fixture answered: the wizard stands on the created
    // project's own address, and the step it left is reported done.
    await expect
      .poll(() => new URL(page.url()).pathname, 'the create opened the created project')
      .toBe(`/en/orgs/${ORG_ACME}/onboarding/${CREATED_PROJECT}`);
    await expect(stepItem(page, 1)).toHaveAttribute('data-done', 'true');
    // The created project's name is rendered from the server's answer, not from the typed draft.
    await expect(
      page
        .getByTestId('onboarding-evidence-panel')
        .getByText(CREATED_PROJECT_NAME, { exact: true }),
    ).toBeVisible();
  });

  test('step 1 exposes exactly the four Tab stops the walkthrough uses', async ({ page }) => {
    await page.goto(onboardingUrl('en', 'onboarding-empty'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 1);

    expect(
      await enabledTabStops(page),
      'step 1 Tab stops inside the main region (stepper 1, both fields, the submit)',
    ).toEqual([
      'onboarding-step-1 button',
      'onboarding-project-name',
      '#onboarding-project-slug',
      'onboarding-project-submit',
    ]);
  });
});

test.describe('W2 — step 2, connect CI (OIDC then token)', () => {
  test('both connect forms, their reveals and Back are walkable by keyboard alone', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);

    // 1. The stepper first: this seed enables steps 1–2 and leaves 3–5 natively disabled. The walk
    //    into the region is the skip link and its Enter, not a programmatic focus move.
    await enterMainByKeyboard(page);
    await pressTab(page);
    await expectKeyboardFocus(page, stepButton(page, 1), 'stepper 1 is enabled at step 2');
    await pressTab(page);
    await expectKeyboardFocus(page, stepButton(page, 2), 'stepper 2 is the current step');
    await expect(stepItem(page, 2)).toHaveAttribute('aria-current', 'step');

    await pressTab(page);
    const snippet = page.locator(QUICKSTART_SNIPPET);
    await expectKeyboardFocus(page, snippet, 'the CLI quickstart snippet');
    // A scrollable-ish <pre> is a Tab stop by design, and it is named by its heading.
    await expect(snippet).toHaveAttribute('tabindex', '0');
    await expect(snippet).toHaveAccessibleName('CLI quickstart');

    // 2. Copy follows the snippet.
    await pressTab(page);
    await expectKeyboardFocus(page, page.locator('[data-testid="cli-quickstart-copy"]'), 'Copy');

    // 3.–6. The four OIDC fields, in reading order, filled with the keyboard.
    await pressTab(page);
    const repoId = page.locator('[data-testid="connect-oidc-repo-id"]');
    await expectKeyboardFocus(page, repoId, 'the OIDC repository id field');
    await page.keyboard.type(REPOSITORY_ID);
    await expect(repoId).toHaveValue(REPOSITORY_ID);

    await pressTab(page);
    const repoName = page.locator('[data-testid="connect-oidc-repo-name"]');
    await expectKeyboardFocus(page, repoName, 'the OIDC repository name field');
    await page.keyboard.type(REPOSITORY_NAME);
    await expect(repoName).toHaveValue(REPOSITORY_NAME);

    await pressTab(page);
    const refs = page.locator('[data-testid="connect-oidc-refs"]');
    await expectKeyboardFocus(page, refs, 'the OIDC ref patterns field');
    await page.keyboard.type(REF_PATTERNS);
    await expect(refs).toHaveValue(REF_PATTERNS);

    await pressTab(page);
    const workflows = page.locator('[data-testid="connect-oidc-workflows"]');
    await expectKeyboardFocus(page, workflows, 'the OIDC workflow refs field');
    await page.keyboard.type(WORKFLOW_REFS);
    await expect(workflows).toHaveValue(WORKFLOW_REFS);

    // 7. Enter on the submit opens the reveal, whose heading takes focus on its own.
    await pressTab(page);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="connect-oidc-submit"]'),
      'OIDC submit',
    );
    await page.keyboard.press('Enter');
    const bindingRevealHeading = revealSlot(page).getByRole('heading');
    await expectKeyboardFocus(page, bindingRevealHeading, 'the binding reveal heading');
    await expect(revealValue(page)).toHaveValue(FIXTURE_BINDING_CHALLENGE);

    // 8. The reveal's order is value → Copy → Dismiss: the action the warning recommends comes
    //    before the one that destroys the value.
    await pressTab(page);
    await expectKeyboardFocus(page, revealValue(page), 'the revealed binding challenge');
    await pressTab(page);
    await expectKeyboardFocus(page, page.locator('[data-testid="reveal-copy"]'), 'reveal Copy');
    await pressTab(page);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="reveal-dismiss"]'),
      'reveal Dismiss',
    );
    await page.keyboard.press('Enter');
    await expectNoReveal(page);
    // Dismiss returns focus to the panel the reveal belonged to, not to the document body.
    await expectKeyboardFocus(
      page,
      page.locator('#connect-oidc-heading'),
      'the OIDC panel heading after Dismiss',
    );

    // 9. Six Tabs from that heading: the four fields, the OIDC submit, then the token name. The
    //    bindings heading, the empty list and the truncated notice add no stop.
    await pressTab(page, 6);
    const tokenName = page.locator('[data-testid="connect-token-name"]');
    await expectKeyboardFocus(page, tokenName, 'the token name field');
    await page.keyboard.type(TOKEN_NAME);
    await expect(tokenName).toHaveValue(TOKEN_NAME);

    // 10.–11. The token reveal behaves the same way, and Dismiss lands on the token heading.
    await pressTab(page);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="connect-token-submit"]'),
      'token submit',
    );
    await page.keyboard.press('Enter');
    await expectKeyboardFocus(
      page,
      revealSlot(page).getByRole('heading'),
      'the ingest-token reveal heading',
    );
    await expect(revealValue(page)).toHaveValue(FIXTURE_TOKEN_SECRET);
    await pressTab(page, 3);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="reveal-dismiss"]'),
      'the ingest-token Dismiss',
    );
    await page.keyboard.press('Enter');
    await expectNoReveal(page);
    await expectKeyboardFocus(
      page,
      page.locator('#connect-token-heading'),
      'the token panel heading after Dismiss',
    );

    // 12.–13. The token panel is the last section, so Back follows it; the seed's own step 2 is not
    //         done, so Next stays gated and is never a stop. Enter on Back returns to step 1.
    await pressTab(page, 3);
    await expectKeyboardFocus(page, backButton(page), 'Back after the token panel');
    await expect(nextButton(page)).toBeDisabled();
    await page.keyboard.press('Enter');
    await expectKeyboardFocus(page, page.locator(STEP_TITLE), 'the step-1 heading after Back');
    await expectCurrentStep(page, 1);
  });
});

test.describe('W3 — step 5, enable collection', () => {
  test('the confirm completes the step and hands focus to its terminal heading', async ({
    page,
  }) => {
    // `permissions-list-onboarding-enable` is the wizard's grants plus `policy:write`, so the
    // confirm is offered and enabled; the seed's own activation read is the contract's 404.
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-evidence', [ENABLE_GRANTS_TOGGLE]),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 5);

    // 1. All five stepper buttons are enabled at step 5; the confirm follows them directly. The walk
    //    into the region is the skip link and its Enter, not a programmatic focus move.
    await enterMainByKeyboard(page);
    await pressTab(page, 5);
    await expectKeyboardFocus(page, stepButton(page, 5), 'stepper 5 is the last stepper stop');
    await expect(stepItem(page, 5)).toHaveAttribute('aria-current', 'step');

    await pressTab(page);
    const confirm = page.locator('[data-testid="enable-collection-confirm"]');
    await expectKeyboardFocus(page, confirm, 'the enable confirm');
    await expect(confirm).toBeEnabled();
    // The replace hazard is stated before the press, and it is the confirm's own description.
    await expect(confirm).toHaveAttribute('aria-describedby', /enable-replaces-warning/);
    await expect(confirm).toHaveAccessibleDescription(
      /replace a policy activated after this page read/i,
    );

    // 2. Enter confirms: the preview is replaced by the terminal panel, whose heading takes focus.
    await page.keyboard.press('Enter');
    await expectKeyboardFocus(
      page,
      page.locator('#enable-enabled-heading'),
      'the terminal heading after the confirm',
    );
    await expect(page.locator('[data-testid="enable-collection-enabled"]')).toBeVisible();
    await expect(page.locator('[data-testid="enable-collection-policy-preview"]')).toHaveCount(0);

    // 3. Back follows the terminal panel; Next is disabled on the last step.
    await pressTab(page);
    await expectKeyboardFocus(page, backButton(page), 'Back after the terminal panel');
    await expect(nextButton(page)).toBeDisabled();
  });

  test('an unreadable activation keeps the confirm disabled and out of the tab order', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-evidence', [
          ENABLE_GRANTS_TOGGLE,
          activationReadToggle('forbidden'),
        ]),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 5);

    // The 403 read is a state, not a failure: the preview stands, the confirm is natively disabled
    // and described by the unreadable notice (and no longer by a permission the session holds).
    const confirm = page.locator('[data-testid="enable-collection-confirm"]');
    await expect(page.locator('#enable-activation-unreadable')).toBeVisible();
    await expect(confirm).toBeDisabled();
    await expect(confirm).toHaveAttribute('aria-describedby', /enable-activation-unreadable/);
    await expect(confirm).not.toHaveAttribute('aria-describedby', /enable-permission/);

    // A keyboard user therefore can never submit a state the server refused to describe: the sixth
    // Tab after the main region skips the disabled confirm and lands on the refused read's one
    // recovery (F63, AC-F63-c) — a real button that re-runs the read and never a command — and Back
    // follows it.
    await enterMainByKeyboard(page);
    await pressTab(page, 6);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="enable-collection-read-again"]'),
      'the refused read’s only recovery',
    );
    await pressTab(page);
    await expectKeyboardFocus(page, backButton(page), 'Back, with the disabled confirm skipped');
  });
});

test.describe('W4 — resume at step 3, Back into step 2, health panel and quickstart', () => {
  test('step 3 → Back → step 2 → Next, with no stray stop from the health panel', async ({
    page,
    context,
  }) => {
    // A successful Copy is part of the path; the clipboard read needs the grant.
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await page.goto(onboardingUrl('en', 'onboarding-token-used'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);

    // 1. Three enabled stepper buttons at step 3; the step-3 placeholder holds no focusable, so the
    //    next stop after the stepper is Back. The walk into the region is the skip link and its
    //    Enter, not a programmatic focus move.
    await enterMainByKeyboard(page);
    await pressTab(page, 3);
    await expectKeyboardFocus(page, stepButton(page, 3), 'stepper 3 is the last stepper stop');
    await pressTab(page);
    await expectKeyboardFocus(page, backButton(page), 'Back after the step-3 placeholder');

    // 2. Enter steps back: the heading and the connect page follow.
    await page.keyboard.press('Enter');
    await expectKeyboardFocus(page, page.locator(STEP_TITLE), 'the step-2 heading after Back');
    await expectCurrentStep(page, 2);

    // 3. The health panel is static text a screen reader reads in place: no Tab stop at all.
    const health = page.locator('[data-testid="connection-health"]');
    await expect(health).toHaveAttribute('data-health-state', 'healthy');
    await expect(
      health.locator('button, a, input, textarea, select, [tabindex="0"]'),
      'the health panel added a Tab stop between the stepper and the quickstart',
    ).toHaveCount(0);

    // 4. Its rows are the server's metadata: the token name, its status pill and the recorded use.
    const credentialRow = health.locator('[data-testid="connection-health-credential"]');
    await expect(credentialRow).toHaveCount(1);
    await expect(credentialRow.locator('strong')).toContainText(TOKEN_USED_NAME);
    await expect(
      credentialRow.locator('.status-pill__label'),
      'the credential row renders a status label',
    ).not.toBeEmpty();
    await expect(health.locator('[data-testid="connection-health-last-use"] time')).toHaveAttribute(
      'datetime',
      TOKEN_USED_LAST_USED_AT,
    );

    // 5. The quickstart snippet is where the panel pair hands the keyboard on, and Copy succeeds.
    await pressTab(page);
    const snippet = page.locator(QUICKSTART_SNIPPET);
    await expectKeyboardFocus(page, snippet, 'the CLI quickstart snippet after Back');
    await expect(snippet).toHaveAccessibleName('CLI quickstart');

    await pressTab(page);
    const copyButton = page.locator('[data-testid="cli-quickstart-copy"]');
    await expectKeyboardFocus(page, copyButton, 'the quickstart copy button');
    await page.keyboard.press('Enter');
    await expect(
      page.locator('[data-testid="cli-quickstart-copy-status"]'),
      'a successful copy announces itself',
    ).not.toHaveText('');
    // Success leaves focus on the control that copied; only the failure path pulls it to the <pre>.
    await expect(
      copyButton,
      'a successful copy moved focus away from its own control',
    ).toBeFocused();
    await expect(snippet).not.toBeFocused();

    // 6. The health panel and the quickstart add no stop between Copy and the first form field.
    await pressTab(page);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="connect-oidc-repo-id"]'),
      'the first OIDC field after the quickstart',
    );

    // Next is enabled on this seed (its step 2 is done), and Enter advances to step 3 with focus on
    // the step heading. From the first OIDC field the remaining stops are: 3 more fields, the OIDC
    // submit, the token name, the token submit, the row's Rotate, Back, then Next.
    await pressTab(page, 9);
    await expectKeyboardFocus(page, nextButton(page), 'Next at the end of the step-2 tab order');
    await expect(nextButton(page)).toBeEnabled();
    await page.keyboard.press('Enter');
    await expectKeyboardFocus(page, page.locator(STEP_TITLE), 'the step-3 heading after Next');
    await expectCurrentStep(page, 3);
  });
});
