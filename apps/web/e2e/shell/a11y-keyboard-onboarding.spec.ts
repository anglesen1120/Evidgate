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
import { expect, test, type Page } from '@playwright/test';

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
  tabToHintedField,
  type HintedFieldId,
} from './onboarding-keyboard';
import {
  expectNoReveal,
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

/** The name this walkthrough types into the token form; the corpus echoes its own name back. */
const TOKEN_NAME = 'CI upload (keyboard)';

async function typeHintedField(page: Page, id: HintedFieldId, value: string, seek = false) {
  const field = await tabToHintedField(page, id, seek);
  await page.keyboard.type(value);
  await expect(field).toHaveValue(value);
}

async function fillProject(page: Page, name: string, slug: string, seek = false) {
  await typeHintedField(page, 'onboarding-project-name', name, seek);
  await typeHintedField(page, 'onboarding-project-slug', slug);
}

test.describe('W1 — step 1, create project (keyboard walkthrough plan)', () => {
  test('the skip link, the stepper, both fields and the submit carry the flow to the first run', async ({
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

    await fillProject(page, CREATED_PROJECT_NAME, CREATED_PROJECT_SLUG);

    // 6. The submit is the last stop of step 1: Back and Next are disabled on the first step, so a
    //    keyboard user cannot tab into them.
    await pressTab(page);
    const submit = page.locator('[data-testid="onboarding-project-submit"]');
    await expectKeyboardFocus(page, submit, 'the create-project submit');
    await expect(backButton(page)).toBeDisabled();
    await expect(nextButton(page)).toBeDisabled();

    // The create marker tells the fresh activation to focus the first-run step heading.
    await page.keyboard.press('Enter');
    await expectCurrentStep(page, 3);
    await expect(stepItem(page, 2)).toHaveAttribute('data-done', 'false');
    // The create opened the project the fixture answered: the wizard stands on the created
    // project's own address, and the step it left is reported done.
    await expect
      .poll(() => new URL(page.url()).pathname, 'the create opened the created project')
      .toBe(`/en/orgs/${ORG_ACME}/onboarding/${CREATED_PROJECT}`);
    await expect(stepItem(page, 1)).toHaveAttribute('data-done', 'true');
    await expectKeyboardFocus(page, page.locator(STEP_TITLE), 'the first-run heading after create');
    // The created project's name is rendered from the server's answer, not from the typed draft.
    await expect(
      page
        .getByTestId('onboarding-evidence-panel')
        .getByText(CREATED_PROJECT_NAME, { exact: true }),
    ).toBeVisible();
  });

  test('step 1 exposes five discoverable steps and five form Tab stops in label order', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-empty'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 1);

    expect(
      await enabledTabStops(page),
      'step 1 Tab stops inside main (five explained steps, two hints, two fields and submit)',
    ).toEqual([
      'onboarding-select-step-1',
      'onboarding-select-step-2',
      'onboarding-select-step-3',
      'onboarding-select-step-4',
      'onboarding-select-step-5',
      'hint:onboarding-project-name-hint',
      'onboarding-project-name',
      'hint:onboarding-project-slug-hint',
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

    await fillProject(page, 'Storefront web', 'storefront-web', true);
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

test.describe('W2 — step 2, connect CI with an ingest token', () => {
  test('the token form, its one-time reveal and Back are walkable by keyboard alone', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);

    // The skip link enters one navigator; future steps remain focusable with a prerequisite.
    await enterMainByKeyboard(page);
    await pressTab(page);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="onboarding-project-context"] a'),
      'Created resources',
    );
    await pressTab(page);
    await expectKeyboardFocus(page, stepButton(page, 1), 'stepper 1');
    await pressTab(page);
    await expectKeyboardFocus(page, stepButton(page, 2), 'the optional token setup');
    await page.keyboard.press('Enter');
    await expectCurrentStep(page, 2);
    await expectKeyboardFocus(page, page.locator(STEP_TITLE), 'the token setup heading');

    await pressTab(page);
    const snippet = page.locator(QUICKSTART_SNIPPET);
    await expectKeyboardFocus(page, snippet, 'the CLI quickstart snippet');
    // A scrollable-ish <pre> is a Tab stop by design, and it is named by its heading.
    await expect(snippet).toHaveAttribute('tabindex', '0');
    await expect(snippet).toHaveAccessibleName('CLI quickstart');

    // 2. Copy follows the snippet.
    await pressTab(page);
    await expectKeyboardFocus(page, page.locator('[data-testid="cli-quickstart-copy"]'), 'Copy');

    await typeHintedField(page, 'connect-token-name', TOKEN_NAME);
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
    await expect(revealValue(page)).toHaveJSProperty('readOnly', true);
    await pressTab(page);
    await expectKeyboardFocus(page, revealValue(page), 'the revealed ingest token');
    await pressTab(page);
    await expectKeyboardFocus(page, page.getByTestId('reveal-copy'), 'the ingest-token Copy');
    await pressTab(page);
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

    // A project needs no credential to continue; going Back still returns to step 1.
    await tabUntil(page, '[data-testid="onboarding-back"]');
    await expectKeyboardFocus(page, backButton(page), 'Back after the token panel');
    await expect(nextButton(page)).toBeEnabled();
    await page.keyboard.press('Enter');
    await expectKeyboardFocus(page, page.locator(STEP_TITLE), 'the step-1 heading after Back');
    await expectCurrentStep(page, 1);
  });
  test('a refused token name is announced once and hands focus to the refusal heading', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-project', ['fixture.variant.createIngestToken=name-invalid']),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);
    await enterMainByKeyboard(page);

    await tabUntil(page, '[data-testid="onboarding-select-step-2"]');
    await page.keyboard.press('Enter');
    await expectCurrentStep(page, 2);
    await typeHintedField(page, 'connect-token-name', TOKEN_NAME, true);
    await pressTab(page);
    await expectKeyboardFocus(page, page.getByTestId('connect-token-submit'), 'token submit');
    await page.keyboard.press('Enter');

    await expectKeyboardFocus(
      page,
      page.locator('#connect-failure-heading'),
      'the refusal heading after a refused token name',
    );
    const details = page.getByTestId('connect-ci-details');
    await expect(details).toHaveAttribute('role', 'alert');
    await expect(details).toHaveAccessibleName(/the server refused these values/i);
    await expect(details).toContainText('a token with this name already exists');
    const tokenName = page.locator('#connect-token-name');
    await expect(tokenName).toHaveAttribute('aria-invalid', 'true');
    await expect(tokenName).toHaveAccessibleDescription(/a token with this name already exists/);
    // The field's own node is not a live region while the details alert carries the sentence.
    await expect(page.locator('#connect-token-name-error')).not.toHaveAttribute('aria-live', /.*/);
  });
});
