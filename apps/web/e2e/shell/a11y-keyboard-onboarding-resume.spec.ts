// The resume walk: a fresh document that lands on step 3, Back into step 2, then Next again, with
// the keyboard only. It proves the two panels between the stepper and the forms — the health panel
// and the CLI quickstart — add no Tab stop of their own, and that the step heading takes focus on
// every move.
import { expect, test } from '@playwright/test';

import {
  enterMainByKeyboard,
  expectKeyboardFocus,
  pressTab,
  QUICKSTART_SNIPPET,
  stepButton,
  STEP_TITLE,
} from './onboarding-keyboard';
import {
  backButton,
  expectCurrentStep,
  nextButton,
  onboardingUrl,
  shell,
  TOKEN_USED_LAST_USED_AT,
  TOKEN_USED_NAME,
} from './onboarding-support';

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
