// 09.2.1-T1 (plan 09.2.1 §2 AC1, §6 "a reload resumes at the derived step", §8 failure path): a
// progress read the server refuses must render the shell's T8 pattern, never a step.
//
// Two layers answer a refusal here, and both are asserted because a wizard that swallowed either one
// would claim a step nobody earned:
//
//   1. the wizard's own read, at a project address — `getProject` answers 403/404 and the shell
//      renders the forbidden / not-found pattern inside `onboarding-shell` (steps 3–4 are still the
//      placeholders T3/T4 own, and step 2 only renders for a project the wizard could read, so a
//      "step 2" here would be a fabrication);
//   2. the org's scope read, at the onboarding root — a 403 or 404 on `listProjects` is answered by
//      the shell's scope guard as the same not-found page (00 §9.2 never distinguishes "you may
//      not" from "it is not there"), so the wizard does not render at all.
import { expect, test, type Page } from '@playwright/test';

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

test.describe('a refused progress read renders the T8 pattern (AC1)', () => {
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

      // 00 §9.2: a 403 and a 404 on the scope read are one answer on purpose.
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
// F63: step 5's own read failures (AC-F63-b/c), one layer below the wizard refusals above.
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
/** AC-F63-c: the one recovery a refused (403) read offers. */
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

test.describe("step 5's refused activation read (F63)", () => {
  test('a twice-refused read keeps focus on its own control and never reaches a command (AC-F63-b, AC-F63-c)', async ({
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
    // answer (AC-F63-b) — never on <body> — and the same refusal is rendered again.
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
