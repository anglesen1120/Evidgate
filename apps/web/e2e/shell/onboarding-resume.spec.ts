// 09.2.1-T1.4 (plan 09.2.1 §2 AC1, §6, §10 T1.4): the reload-mid-flow case of the wizard, in real
// Chromium against the FIXTURES build. AC1's second clause is the point: "reloading the wizard
// mid-flow (any step) resumes exactly at the first incomplete step, reading resumable state from
// the server, not local storage". A store that persisted its step in `localStorage` would pass a
// create-and-continue check and fail this one, because the reload re-derives every step from the
// five reads the fixtures answer.
import { expect, test } from '@playwright/test';

import {
  CREATED_PROJECT,
  CREATED_PROJECT_NAME,
  CREATED_PROJECT_SLUG,
  expectCurrentStep,
  expectNoStepInStorage,
  expectStepDone,
  grantsOnlyUrl,
  onboardingUrl,
  SEED_STEP,
  shell,
  type OnboardingSeed,
} from './onboarding-support';
import { ORG_ACME } from './support';

/** The seeds whose resume point is worth a reload: one mid-wizard, one on the last derived step. */
const RELOAD_SEEDS: readonly OnboardingSeed[] = ['onboarding-connected', 'onboarding-evidence'];

test.describe('a reload mid-flow resumes from the server (AC1)', () => {
  test('creating the project advances to step 2 and a reload still shows step 2', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-empty'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 1);
    await expectStepDone(page, 1, false);

    await page.locator('[data-testid="onboarding-project-name"]').fill(CREATED_PROJECT_NAME);
    // The slug is the SDK's `CreateProjectRequest`'s second required member; the wizard renders it
    // as a plain form control rather than deriving it from the name (the server owns validation).
    await page
      .locator('input[name="slug"], #onboarding-project-slug')
      .first()
      .fill(CREATED_PROJECT_SLUG);
    await page.locator('[data-testid="onboarding-project-submit"]').click();

    // Step 1 completed: the wizard stands on the created project's own address, not on the org's
    // onboarding root, and it carries none of the review toggles it was opened with — a carried
    // toggle would re-seed the state the create just changed.
    await expect
      .poll(() => new URL(page.url()).pathname)
      .toBe(`/en/orgs/${ORG_ACME}/onboarding/${CREATED_PROJECT}`);
    expect(
      new URL(page.url()).search,
      'the create navigation carried the fixture toggles to the new address',
    ).toBe('');

    await expectCurrentStep(page, 2);
    await expectStepDone(page, 1, true);
    await expectNoStepInStorage(page, 'after the create');

    // The reload of AC1: a browser boot re-reads the permission catalogue, and the default page
    // holds no `project:*` code, so the wizard's grants are re-attached and nothing else is. The
    // step itself must come from the server's resources — the seed the flow started from is gone.
    await page.goto(grantsOnlyUrl('en', CREATED_PROJECT));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);
    await expectStepDone(page, 1, true);
    expect(new URL(page.url()).pathname, 'the reload left the address the wizard resumed on').toBe(
      `/en/orgs/${ORG_ACME}/onboarding/${CREATED_PROJECT}`,
    );
    await expectNoStepInStorage(page, 'after the reload');

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);
    await expectNoStepInStorage(page, 'after the second reload');
  });

  for (const seed of RELOAD_SEEDS) {
    test(`the seeded step survives a reload (${seed})`, async ({ page }) => {
      const step = SEED_STEP[seed];
      await page.goto(onboardingUrl('en', seed));
      await expect(shell(page)).toBeVisible();
      await expectCurrentStep(page, step);
      await expectNoStepInStorage(page, 'the first load');

      await page.reload();
      await expect(shell(page)).toBeVisible();
      await expectCurrentStep(page, step);
      await expectNoStepInStorage(page, 'the reload');
    });
  }
});
