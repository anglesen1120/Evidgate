// Step 5 walked with the keyboard only, in the two states the step publishes: the confirm that
// completes the wizard and hands focus to the terminal heading, and the states where the confirm is
// natively disabled so a keyboard visitor can never submit what the server refused to describe. The
// refusal path is walked too: the page parks focus on its own heading, outside the alerts.
import { expect, test } from '@playwright/test';

import {
  enterMainByKeyboard,
  expectKeyboardFocus,
  pressTab,
  stepButton,
  tabUntil,
} from './onboarding-keyboard';
import {
  activationReadToggle,
  backButton,
  ENABLE_GRANTS_TOGGLE,
  expectCurrentStep,
  nextButton,
  onboardingPath,
  seedQueryWith,
  shell,
} from './onboarding-support';
import { withVariant } from './support';

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
    await pressTab(page);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="onboarding-project-context"] a'),
      'Created resources',
    );
    await pressTab(page, 5);
    await expectKeyboardFocus(page, stepButton(page, 5), 'stepper 5 is the last stepper stop');
    await expect(stepButton(page, 5)).toHaveAttribute('aria-current', 'step');

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

    // 3. Back follows the terminal panel; completion replaces Next with Open project.
    await pressTab(page);
    await expectKeyboardFocus(page, backButton(page), 'Back after the terminal panel');
    await expect(nextButton(page)).toHaveCount(0);
    await expect(page.getByTestId('onboarding-complete')).toBeVisible();
    await expectCurrentStep(page, 5);
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

    // After the resource link and five step controls, the seventh Tab skips the disabled confirm
    // and lands on the refused read's one recovery — a read-only button — before Back.
    await enterMainByKeyboard(page);
    await pressTab(page, 7);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="enable-collection-read-again"]'),
      'the refused read’s only recovery',
    );
    await pressTab(page);
    await expectKeyboardFocus(page, backButton(page), 'Back, with the disabled confirm skipped');
  });
  test('a refused confirm names itself and hands focus to the heading that names it', async ({
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
    await enterMainByKeyboard(page);

    await tabUntil(page, '[data-testid="enable-collection-confirm"]');
    await page.keyboard.press('Enter');

    const heading = page.locator('#enable-confirm-failed-heading');
    await expectKeyboardFocus(page, heading, 'the page-owned heading of a refused confirm');
    // The section takes its name from that heading, so the refusal is announced as one region.
    await expect(page.locator('[data-testid="enable-collection-error"]')).toHaveAccessibleName(
      /collection could not be enabled/i,
    );
    const details = page.getByTestId('enable-collection-details');
    await expect(details).toHaveAttribute('role', 'alert');
    await expect(details).toHaveAccessibleName(/the server refused these values/i);
    await expect(details).toContainText('/flaky_budget_max');
    await expect(page.locator('[data-testid="enable-collection-enabled"]')).toHaveCount(0);
  });
});
