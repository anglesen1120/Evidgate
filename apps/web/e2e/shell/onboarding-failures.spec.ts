// A progress read the server refuses must render the shell's refusal pattern, never a step.
//
// Two layers answer a refusal here, and both are asserted because a wizard that swallowed either one
// would claim a step nobody earned:
//
//   1. the wizard's own read, at a project address — `getProject` answers 403/404 and the shell
//      renders the forbidden / not-found pattern inside `onboarding-shell` (the later steps are
//      still placeholders, and step 2 only renders for a project the wizard could read, so a
//      "step 2" here would be a fabrication);
//   2. the org's scope read, at the onboarding root — a 403 or 404 on `listProjects` is answered by
//      the shell's scope guard as the same not-found page (a refusal and an absence are one answer
//      on purpose), so the wizard does not render at all.
import { expect, test, type Page } from '@playwright/test';

import { danglingDescriptions } from './dangling-descriptions';
import {
  activationReadToggle,
  CREATED_PROJECT,
  ENABLE_GRANTS_TOGGLE,
  expectCurrentStep,
  expectNoStepDone,
  onboardingPath,
  orgRefusalUrl,
  refusalUrl,
  seedQueryWith,
  shell,
  type OnboardingSeed,
} from './onboarding-support';
import { withVariant } from './support';

test.describe('a refused progress read renders the refusal pattern', () => {
  test('a 403 on the wizard project read renders the forbidden pattern', async ({ page }) => {
    await page.goto(refusalUrl('en', 'onboarding-forbidden', CREATED_PROJECT));

    await expect(shell(page)).toBeVisible();
    await expect(page.locator('app-forbidden-pattern').first()).toBeVisible();
    // The refusal is a 403, not a missing resource: the wizard must not answer "not found".
    await expect(page.locator('app-not-found-pattern')).toHaveCount(0);
    await expectNoStepDone(page);
  });

  test('a 404 on the wizard project read renders the not-found pattern', async ({ page }) => {
    await page.goto(refusalUrl('en', 'onboarding-project-not-found', CREATED_PROJECT));

    await expect(shell(page)).toBeVisible();
    await expect(page.locator('app-not-found-pattern').first()).toBeVisible();
    await expect(page.locator('app-forbidden-pattern')).toHaveCount(0);
    await expectNoStepDone(page);
  });

  test('a refused org scope read never shows a step, whatever it answered', async ({ page }) => {
    for (const failure of ['onboarding-forbidden', 'onboarding-project-not-found'] as const) {
      await page.goto(orgRefusalUrl('en', failure));

      // A 403 and a 404 on the scope read are one answer on purpose.
      await expect(page.locator('app-not-found-pattern').first()).toBeVisible();
      await expect(
        page.locator('[data-testid="onboarding-shell"] [data-testid="onboarding-stepper"]'),
        `${failure} let the wizard render a stepper`,
      ).toHaveCount(0);
      await expectNoStepDone(page);
    }
  });
});

// ---------------------------------------------------------------------------------------------
// Step 5's own read failures, one layer below the wizard refusals above.
//
// The fixtures backend answers a fixed document per (operation, variant), in process
// (`provideFixtureBackend()` replaces `HttpBackend`), so these cases prove only what the browser can
// see: the natively closed confirm, the one recovery control, and — because the `forbidden` document
// is served for every attempt — a second refusal that a keyboard press produced, with focus still on
// the page's own target. How many reads a visit issues stays in the page's own Vitest spec, which has
// the spy.
//
// The 429 state is deliberately absent. The progress read rejects the whole load for any refusal
// other than `403` (`onboarding-progress.service.ts` `readActivation`), so a `429` never reaches step
// 5's panel in this build: the wait it renders is proven in `steps/enable-collection.page.spec.ts`
// against the corpus document (`policy-activation-read-rate-limited`), and its browser path belongs
// to the shell's own slice.
// ---------------------------------------------------------------------------------------------

/** The step-5 page root. */
const ENABLE_ROOT = '[data-testid="enable-collection"]';
/** The page's typed error panel, which a `403` read is *not* (it is a page state, not a load error). */
const ENABLE_ERROR = '[data-testid="enable-collection-error"]';
/** The one recovery a refused (403) read offers. */
const READ_AGAIN = '[data-testid="enable-collection-read-again"]';
/** The refused-read copy. */
const UNREADABLE = '[data-testid="enable-collection-unreadable"]';
/** The confirm: offered only beside a `404` read, and natively disabled beside a `403` one. */
const CONFIRM = '[data-testid="enable-collection-confirm"]';
/** The terminal panel, which a failed read must never render. */
const ENABLED = '[data-testid="enable-collection-enabled"]';

/** The seed whose derivation leaves step 5 current (a run with an evidence revision, no policy). */
const STEP_FIVE_SEED: OnboardingSeed = 'onboarding-evidence';

/**
 * Opens step 5 with its activation read refused — the `403` of a principal without `policy:read` —
 * and the wizard's `policy:write` grant in place (`ENABLE_GRANTS_TOGGLE`), so the confirm's disabled
 * state is attributable to the read rather than to a missing grant. `seedQueryWith` appends after the
 * seed's own pin and a later `fixture.variant.<operation>` wins, so the answer replaces `not-found`.
 */
async function openStepFiveWithRefusedRead(page: Page): Promise<void> {
  const query = seedQueryWith(STEP_FIVE_SEED, [
    ENABLE_GRANTS_TOGGLE,
    activationReadToggle('forbidden'),
  ]);
  await page.goto(withVariant(onboardingPath('en'), query));
  await expect(shell(page)).toBeVisible();
  await expectCurrentStep(page, 5);
  await expect(page.locator(ENABLE_ROOT)).toBeVisible();
}

test.describe("step 5's refused activation read", () => {
  test('a twice-refused read keeps focus on its own control and never reaches a command', async ({
    page,
  }) => {
    await openStepFiveWithRefusedRead(page);

    const readAgain = page.locator(READ_AGAIN);
    // One way back, beside the refusal, with the confirm natively disabled next to it.
    await expect(readAgain).toBeVisible();
    await expect(page.locator(UNREADABLE)).toBeVisible();
    await expect(page.locator(CONFIRM)).toBeDisabled();
    // A 403 is a state of the page, not the typed error panel.
    await expect(page.locator(ENABLE_ERROR)).toHaveCount(0);
    // The refused read never names an activation, and holds no terminal panel.
    await expect(page.locator(ENABLED)).toHaveCount(0);

    // Reached by keyboard and pressed with the keyboard: no forced click, no pointer.
    await readAgain.focus();
    await expect(readAgain).toBeFocused();
    await page.keyboard.press('Enter');

    // The answer replaced the control that was pressed, so focus sits on the named target of that
    // answer — never on <body> — and the same refusal is rendered again.
    await expect(readAgain).toBeFocused();
    await expect(page.locator(UNREADABLE)).toBeVisible();
    await expect(page.locator(CONFIRM)).toBeDisabled();

    // A second refusal behaves the same way, so the target is stable across two failures.
    await page.keyboard.press('Enter');
    await expect(readAgain).toBeFocused();
    await expect(page.locator(CONFIRM)).toBeDisabled();
    await expect(page.locator(ENABLED)).toHaveCount(0);
  });
});

// ---------------------------------------------------------------------------------------------
// The three form refusals, in the browser. Each step answers a 422 envelope whose `details` names a
// control, so each is asserted the way a screen-reader user meets it: one labelled alert carries the
// rows, the refused control is marked invalid and described by that text, every description id
// resolves, and focus sits on the page's own heading — outside the alerts — rather than on `<body>`
// where the disabled press left it.
// ---------------------------------------------------------------------------------------------

test.describe('form refusals announce once and keep the visitor’s place', () => {
  test('create project: the name refusal is one labelled alert and focus is on its heading', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-empty', ['fixture.variant.createProject=name-taken']),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 1);
    await page.locator('#onboarding-project-name').fill('Storefront web');
    await page.locator('#onboarding-project-slug').fill('storefront-web');
    await page.locator('[data-testid="onboarding-project-submit"]').press('Enter');

    await expect(page.locator('#onboarding-project-failure-heading')).toBeFocused();
    const details = page.getByTestId('onboarding-project-details');
    await expect(details).toHaveAttribute('role', 'alert');
    await expect(details).toHaveAccessibleName(/the server refused these values/i);
    await expect(details).toContainText('a project with this name already exists');
    // One alert for the rows; the field's own node stays silent so the sentence is not repeated.
    await expect(page.getByRole('alert').filter({ hasText: 'already exists' })).toHaveCount(1);
    await expect(page.locator('#onboarding-project-name-error')).not.toHaveAttribute(
      'aria-live',
      /.*/,
    );
    await expect(page.locator('#onboarding-project-name')).toHaveAttribute('aria-invalid', 'true');
    expect(await danglingDescriptions(page)).toEqual([]);
  });

  test('connect CI: the repository refusal is one labelled alert and focus is on its heading', async ({
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
    await page.locator('#connect-oidc-repo-id').fill('5012345');
    await page.locator('#connect-oidc-repo-name').fill('acme/storefront-web');
    await page.locator('#connect-oidc-refs').fill('refs/heads/main');
    await page
      .locator('#connect-oidc-workflows')
      .fill('acme/storefront-web/.github/workflows/evidgate.yml@refs/heads/main');
    await page.locator('[data-testid="connect-oidc-submit"]').press('Enter');

    await expect(page.locator('#connect-failure-heading')).toBeFocused();
    const details = page.getByTestId('connect-ci-details');
    await expect(details).toHaveAttribute('role', 'alert');
    await expect(details).toHaveAccessibleName(/the server refused these values/i);
    await expect(details).toContainText('the GitHub App cannot read this repository');
    await expect(
      page.getByRole('alert').filter({ hasText: 'cannot read this repository' }),
    ).toHaveCount(1);
    await expect(page.locator('#connect-oidc-repo-name')).toHaveAttribute('aria-invalid', 'true');
    await expect(page.locator('#connect-repo-name-error')).not.toHaveAttribute('aria-live', /.*/);
    expect(await danglingDescriptions(page)).toEqual([]);
  });

  test('enable collection: the refused confirm names its section and takes focus alone', async ({
    page,
  }) => {
    await page.goto(
      withVariant(
        onboardingPath('en'),
        seedQueryWith('onboarding-evidence', [
          ENABLE_GRANTS_TOGGLE,
          'fixture.variant.createPolicyRevision=unknown-field',
        ]),
      ),
    );
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 5);
    await page.locator('[data-testid="enable-collection-confirm"]').press('Enter');

    await expect(page.locator('#enable-confirm-failed-heading')).toBeFocused();
    await expect(page.locator(ENABLE_ERROR)).toHaveAccessibleName(
      /collection could not be enabled/i,
    );
    const details = page.getByTestId('enable-collection-details');
    await expect(details).toHaveAttribute('role', 'alert');
    await expect(details).toHaveAccessibleName(/the server refused these values/i);
    await expect(details).toContainText('/flaky_budget_max');
    // The failed command never claims the terminal panel.
    await expect(page.locator(ENABLED)).toHaveCount(0);
    expect(await danglingDescriptions(page)).toEqual([]);
  });
});
