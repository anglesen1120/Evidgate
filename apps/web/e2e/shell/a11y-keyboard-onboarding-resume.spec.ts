// The resume walk: Back into step 2, through its credential View and quickstart controls,
// then Next to step 3. Every move uses the keyboard; neither panel adds a redundant stop.
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
  TOKEN_USED_LAST_USED_AT,
  TOKEN_USED_NAME,
  TOKEN_USED_PREFIX,
} from './onboarding-connection-support';
import {
  backButton,
  expectCurrentStep,
  nextButton,
  onboardingUrl,
  shell,
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

    // All five steps are discoverable; the last two announce their unmet prerequisite.
    await enterMainByKeyboard(page);
    await pressTab(page);
    await expectKeyboardFocus(
      page,
      page.locator('[data-testid="onboarding-project-context"] a'),
      'Created resources',
    );
    await pressTab(page, 3);
    await expectKeyboardFocus(page, stepButton(page, 3), 'the current step');
    for (const step of [4, 5] as const) {
      await pressTab(page);
      await expectKeyboardFocus(page, stepButton(page, step), `blocked step ${step}`);
      await expect(stepButton(page, step)).toHaveAttribute('aria-disabled', 'true');
    }
    await pressTab(page);
    await expectKeyboardFocus(page, backButton(page), 'Back after the step-3 placeholder');

    // 2. Enter steps back: the heading and the connect page follow.
    await page.keyboard.press('Enter');
    await expectKeyboardFocus(page, page.locator(STEP_TITLE), 'the step-2 heading after Back');
    await expectCurrentStep(page, 2);

    // 3. The row's named View is the only health stop; the table and its panel are not stops.
    const health = page.locator('[data-testid="connection-health"]');
    await expect(health).toHaveAttribute('data-health-state', 'healthy');
    const viewToken = { name: `View ${TOKEN_USED_NAME}`, exact: true };
    const healthView = health.getByRole('button', {
      name: `View ${TOKEN_USED_NAME} (${TOKEN_USED_PREFIX})`,
      exact: true,
    });
    await expect(health).not.toHaveAttribute('tabindex', '0');
    await expect(
      health.locator('button, a[href], input, textarea, select, [tabindex]:not([tabindex="-1"])'),
    ).toHaveCount(1);
    await pressTab(page);
    await expectKeyboardFocus(page, healthView, 'the health row View after the step-2 heading');
    await page.keyboard.press('Shift+Tab');
    await expectKeyboardFocus(
      page,
      stepButton(page, 5),
      'the last step before the health row View',
    );
    await pressTab(page);
    await expectKeyboardFocus(page, healthView, 'the sole health stop after the stepper');

    // 4. Its rows are the server's metadata: the token name, its status pill and the recorded use.
    const credentialRow = health.locator('[data-testid="connection-health-credential"]');
    await expect(credentialRow).toHaveCount(1);
    await expect(credentialRow.getByRole('rowheader')).toContainText(TOKEN_USED_NAME);
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

    // 7. From the first OIDC field: three other fields, OIDC submit, token name and token submit.
    // The token table has its own metadata View and Rotate, followed by Back and Next.
    await pressTab(page, 6);
    await expectKeyboardFocus(page, page.getByTestId('connect-token-submit'), 'create token');
    await pressTab(page);
    await expectKeyboardFocus(
      page,
      page.getByTestId('connect-token-row').getByRole('button', viewToken),
      'the token table row View',
    );
    await pressTab(page);
    await expectKeyboardFocus(page, page.getByTestId('connect-token-rotate'), 'Rotate token');
    await pressTab(page);
    await expectKeyboardFocus(page, backButton(page), 'Back after the token row');
    await pressTab(page);
    await expectKeyboardFocus(page, nextButton(page), 'Next at the end of the step-2 tab order');
    await expect(nextButton(page)).toBeEnabled();
    await page.keyboard.press('Enter');
    await expectKeyboardFocus(page, page.locator(STEP_TITLE), 'the step-3 heading after Next');
    await expectCurrentStep(page, 3);
  });
});
