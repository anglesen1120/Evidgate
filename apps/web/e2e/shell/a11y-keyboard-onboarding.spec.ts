// The onboarding wizard's first two steps walked with the keyboard only — `page.keyboard` for every
// interaction — asserting `document.activeElement` by element identity at every stop, and that the
// focused element actually renders its focus ring. Each walk starts a fresh document at the skip
// link and enters the main region with Enter, so every stop on the way is one a keyboard visitor
// really crosses.
//
// The walkthrough follows `docs/a11y/parts/onboarding.md` "Keyboard walkthrough plan" W1–W2. The
// refusal paths the plan names are walked too: each step that disables or unmounts the control it
// was pressed from parks focus on a target the page owns (the waiting line, then its own refusal
// heading), and no assertion here pins a dropped-focus fallback as expected behaviour.
import { expect, test } from '@playwright/test';

import { danglingDescriptions } from './dangling-descriptions';
import {
  enterMainByKeyboard,
  enabledTabStops,
  expectKeyboardFocus,
  MAIN,
  pressTab,
  QUICKSTART_SNIPPET,
  SKIP_LINK,
  stepButton,
  STEP_TITLE,
  tabUntil,
} from './onboarding-keyboard';
import {
  expectNoReveal,
  FIXTURE_BINDING_CHALLENGE,
  FIXTURE_TOKEN_SECRET,
  revealSlot,
  revealValue,
} from './onboarding-connection-support';
import {
  backButton,
  CREATED_PROJECT,
  CREATED_PROJECT_NAME,
  CREATED_PROJECT_SLUG,
  expectCurrentStep,
  nextButton,
  onboardingPath,
  onboardingUrl,
  seedQueryWith,
  shell,
  stepItem,
} from './onboarding-support';
import { ORG_ACME, withVariant } from './support';

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

    // Blocked steps remain reachable so keyboard visitors can discover their prerequisite.
    await pressTab(page);
    await expectKeyboardFocus(page, stepButton(page, 1), 'stepper 1 is the first wizard stop');
    await expect(stepButton(page, 1)).toHaveAttribute('aria-current', 'step');
    for (const step of [2, 3, 4, 5] as const) {
      await pressTab(page);
      await expectKeyboardFocus(page, stepButton(page, step), `blocked step ${step}`);
      await expect(stepButton(page, step)).toHaveAttribute('aria-disabled', 'true');
    }
    await page.keyboard.press('Enter');
    await expectCurrentStep(page, 1);
    await expectKeyboardFocus(page, stepButton(page, 5), 'blocked activation retains focus');

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

    // 7. Enter creates the project and the wizard opens step 2 on it, with focus on the new step's
    //    own heading: the create marker tells the fresh activation the move is the user's.
    await page.keyboard.press('Enter');
    await expectCurrentStep(page, 2);
    await expect(page.locator('[data-testid="connect-ci-page"]')).toBeVisible();
    // The create opened the project the fixture answered: the wizard stands on the created
    // project's own address, and the step it left is reported done.
    await expect
      .poll(() => new URL(page.url()).pathname, 'the create opened the created project')
      .toBe(`/en/orgs/${ORG_ACME}/onboarding/${CREATED_PROJECT}`);
    await expect(stepItem(page, 1)).toHaveAttribute('data-done', 'true');
    await expectKeyboardFocus(page, page.locator(STEP_TITLE), 'the step 2 heading after a create');
    // The created project's name is rendered from the server's answer, not from the typed draft.
    await expect(
      page
        .getByTestId('onboarding-evidence-panel')
        .getByText(CREATED_PROJECT_NAME, { exact: true }),
    ).toBeVisible();
  });

  test('step 1 exposes five discoverable steps and the three existing form Tab stops', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-empty'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 1);

    expect(
      await enabledTabStops(page),
      'step 1 Tab stops inside main (five explained steps, both fields, the submit)',
    ).toEqual([
      'onboarding-select-step-1',
      'onboarding-select-step-2',
      'onboarding-select-step-3',
      'onboarding-select-step-4',
      'onboarding-select-step-5',
      'onboarding-project-name',
      '#onboarding-project-slug',
      'onboarding-project-submit',
    ]);
  });
  test('a refused create speaks once from a labelled alert and leaves focus on its own heading', async ({
    page,
  }) => {
    // The server refuses the name: the submit is disabled for the request, so focus must land on the
    // page's waiting line and then on the page's own refusal heading — never on <body>, never inside
    // the alert whose text is being read anyway.
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-empty', ['fixture.variant.createProject=name-taken']),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 1);
    await enterMainByKeyboard(page);

    await tabUntil(page, '#onboarding-project-name');
    await page.keyboard.type('Storefront web');
    await pressTab(page);
    await page.keyboard.type('storefront-web');
    await tabUntil(page, '[data-testid="onboarding-project-submit"]');
    await page.keyboard.press('Enter');

    const heading = page.locator('#onboarding-project-failure-heading');
    await expectKeyboardFocus(page, heading, 'the refusal heading after a refused create');
    // The refusal heading is the page's own, outside every alert.
    await expect(heading).not.toHaveAttribute('role', /alert/);

    // The envelope's field row is announced once, by the labelled details alert, and the refused
    // field is marked invalid with that text as its description.
    const details = page.getByTestId('onboarding-project-details');
    await expect(details).toHaveAttribute('role', 'alert');
    await expect(details).toHaveAccessibleName(/the server refused these values/i);
    await expect(details).toContainText('a project with this name already exists');
    const name = page.locator('#onboarding-project-name');
    await expect(name).toHaveAttribute('aria-invalid', 'true');
    await expect(name).toHaveAccessibleDescription(/a project with this name already exists/);
    // Every id a control is described by resolves to a rendered element.
    expect(await danglingDescriptions(page)).toEqual([]);

    // Editing the refused field answers for it: the inline reason and its invalidity go on the first
    // keystroke, while the alert keeps the rows it announced for the values that were submitted.
    await tabUntil(page, '#onboarding-project-name');
    await page.keyboard.type(' 2');
    await expect(name).not.toHaveAttribute('aria-invalid', 'true');
    await expect(name).not.toHaveAccessibleDescription(/a project with this name already exists/);
    await expect(details).toContainText('a project with this name already exists');
    expect(await danglingDescriptions(page)).toEqual([]);
  });
});

test.describe('W2 — step 2, connect CI (OIDC then token)', () => {
  test('both connect forms, their reveals and Back are walkable by keyboard alone', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);

    // The skip link enters one navigator; future steps remain focusable with a prerequisite.
    await enterMainByKeyboard(page);
    await pressTab(page);
    await expectKeyboardFocus(page, stepButton(page, 1), 'stepper 1 is enabled at step 2');
    await pressTab(page);
    await expectKeyboardFocus(page, stepButton(page, 2), 'stepper 2 is the current step');
    await expect(stepButton(page, 2)).toHaveAttribute('aria-current', 'step');
    for (const step of [3, 4, 5] as const) {
      await pressTab(page);
      await expectKeyboardFocus(page, stepButton(page, step), `blocked step ${step}`);
      await expect(stepButton(page, step)).toHaveAttribute('aria-disabled', 'true');
    }

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
  test('a refused binding is announced once and hands focus to the refusal heading', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-project', ['fixture.variant.createCiBinding=validation']),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);
    await enterMainByKeyboard(page);

    await tabUntil(page, '#connect-oidc-repo-id');
    await page.keyboard.type(REPOSITORY_ID);
    await pressTab(page);
    await page.keyboard.type(REPOSITORY_NAME);
    await pressTab(page);
    await page.keyboard.type(REF_PATTERNS);
    await pressTab(page);
    await page.keyboard.type(WORKFLOW_REFS);
    await tabUntil(page, '[data-testid="connect-oidc-submit"]');
    await page.keyboard.press('Enter');

    await expectKeyboardFocus(
      page,
      page.locator('#connect-failure-heading'),
      'the refusal heading after a refused binding',
    );
    const details = page.getByTestId('connect-ci-details');
    await expect(details).toHaveAttribute('role', 'alert');
    await expect(details).toHaveAccessibleName(/the server refused these values/i);
    await expect(details).toContainText('the GitHub App cannot read this repository');
    const repository = page.locator('#connect-oidc-repo-name');
    await expect(repository).toHaveAttribute('aria-invalid', 'true');
    await expect(repository).toHaveAccessibleDescription(
      /the GitHub App cannot read this repository/,
    );
    // The field's own node is not a live region while the details alert carries the sentence.
    await expect(page.locator('#connect-oidc-repo-name-error')).not.toHaveAttribute(
      'aria-live',
      /.*/,
    );
  });
});
