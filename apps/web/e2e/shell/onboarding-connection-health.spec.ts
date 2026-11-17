// 09.2.1-T7 (plan 09.2.1 §2 AC2/AC7, §8; contracts/cli.md §§3, 4, 10): step 2's
// connection-health panel and its CLI quickstart, in real Chromium against the FIXTURES build
// (`playwright.fixtures.config.ts`).
//
// Four rules shape what these cases assert:
//
// 1. **Health is a derivation, never a step.** `deriveConnectionHealth` reads the two lists
//    `OnboardingProgressService` already fetched; it feeds no done-condition. So a project whose
//    only credentials are a revoked and an expired token still completes step 2 (its gate is
//    presence, exactly as 09.2.1-T1 shipped it) while the panel reports `unhealthy`. The cases
//    below assert both halves in one go: a health state that quietly gated Next would pass a
//    panel-only check and still break the wizard.
// 2. **The panel and the quickstart show server metadata only.** The last-use line renders the
//    `last_used_at` the server answered, in a `<time datetime>`; the quickstart names the token's
//    name/prefix and the project slug, and never a value that could act as a credential. §8's
//    "a one-time value is shown once" is therefore proved against these two panels as well: the
//    create case re-uses `secretFootprint` and then also reads both panels' `innerHTML` (an
//    attribute would pass a text-only check), and it re-scans after Dismiss, while the panels stay
//    mounted, so the empty result is not the "page went away" case.
// 3. **No localized string identifies an element.** Every assertion keys on the DOM contract the
//    panel publishes (`data-health-state`, the `data-testid` hooks, `<time datetime>`, the native
//    `disabled` attribute), so the same spec passes on the `en` and the `vi` bundle.
// 4. **A refused credential read rejects the resume.** Step 2's token list is one of the three
//    project reads `load` issues together; a 403/404 on it must render the shell's forbidden /
//    not-found pattern *instead of* a step with an empty panel (00 §9.2: a wrong step is worse
//    than no step). Reaching that state needs its own corpus variants (`onboarding-forbidden`,
//    `onboarding-project-not-found` of `listIngestTokens`), because the existing refusals answer
//    `getProject` and would prove a different read.
import { expect, test, type Page } from '@playwright/test';

import {
  connectCiPage,
  expectNoReveal,
  expectSecretAbsent,
  expectSecretOnlyInReveal,
  FIXTURE_TOKEN_SECRET,
  ONBOARDING_PROJECT_SLUG,
  revealValue,
  TOKEN_USED_LAST_USED_AT,
  TOKEN_USED_NAME,
  TOKEN_USED_PREFIX,
} from './onboarding-connection-support';
import {
  backButton,
  CREATED_PROJECT,
  credentialListRefusalUrl,
  expectCurrentStep,
  expectNoStepDone,
  expectNoStepInStorage,
  expectStepDone,
  nextButton,
  onboardingUrl,
  shell,
  type CredentialListFailure,
  type OnboardingSeed,
  type StepNumber,
} from './onboarding-support';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { currentTheme, LOCALES, seedStoredTheme, THEMES, type ShellLocale } from './support';

/** The token name the create case types; the corpus echoes its own name back, never this one. */
const TOKEN_NAME = 'CI upload (health e2e)';

/**
 * The panel's own DOM contract: the host carries `data-health-state`, one row per credential, and
 * the last-use line wraps the server's timestamp in a `<time datetime>`.
 */
function healthPanel(page: Page) {
  return page.locator('[data-testid="connection-health"]');
}

function credentialRows(page: Page) {
  return page.locator('[data-testid="connection-health-credential"]');
}

function lastUseLine(page: Page) {
  return page.locator('[data-testid="connection-health-last-use"]');
}

function quickstart(page: Page) {
  return page.locator('[data-testid="cli-quickstart"]');
}

function quickstartSnippet(page: Page) {
  return page.locator('[data-testid="cli-quickstart-snippet"]');
}

/**
 * Opens a seed and steps Back to step 2, the only step the connect page — and with it the health
 * panel and the quickstart — renders on. A seed whose token page holds rows derives step 3
 * (step 2 is satisfied by presence), so Back is the documented way to read the panel there.
 *
 * The locale is a parameter because a bundle is a separate build: a case that scanned `/en/`
 * while asserting `html[lang]="vi"` would fail loudly, and one that relaxed that assertion would
 * scan a bundle nobody asked about.
 */
async function openStepTwo(
  page: Page,
  locale: ShellLocale,
  seed: OnboardingSeed,
  derivedStep: StepNumber,
): Promise<void> {
  await page.goto(onboardingUrl(locale, seed));
  await expect(shell(page)).toBeVisible();
  await expectCurrentStep(page, derivedStep);
  if (derivedStep !== 2) {
    await backButton(page).click();
    await expectCurrentStep(page, 2);
  }
  await expect(connectCiPage(page)).toBeVisible();
}

/** Every `localStorage`/`sessionStorage` entry whose key or value carries `value`. */
async function storageHits(page: Page, value: string): Promise<readonly string[]> {
  return page.evaluate((needle) => {
    const hits: string[] = [];
    for (const [label, store] of [
      ['localStorage', window.localStorage],
      ['sessionStorage', window.sessionStorage],
    ] as const) {
      for (let index = 0; index < store.length; index += 1) {
        const key = store.key(index) ?? '';
        if (key.includes(needle) || (store.getItem(key) ?? '').includes(needle)) {
          hits.push(`${label}[${key}]`);
        }
      }
    }
    return hits;
  }, value);
}

test.describe('the health panel reads the server credential metadata (AC2)', () => {
  test('an active token with a recorded use renders healthy and a token-aware quickstart', async ({
    page,
  }) => {
    await openStepTwo(page, 'en', 'onboarding-token-used', 3);

    // One active token, nothing else: the healthy state comes from that row, not from the step.
    await expect(healthPanel(page)).toBeVisible();
    await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'healthy');
    await expect(credentialRows(page)).toHaveCount(1);
    await expect(credentialRows(page).first()).toContainText(TOKEN_USED_NAME);
    await expect(credentialRows(page).first()).toContainText(TOKEN_USED_PREFIX);
    // The row and the page's own token list describe the same credential.
    await expect(page.locator('[data-testid="connect-token-row"]')).toHaveCount(1);
    await expect(page.locator('[data-testid="connect-token-rotate"]')).toHaveAttribute(
      'aria-label',
      new RegExp(TOKEN_USED_NAME.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')),
    );

    // The last-use line renders the answer's own timestamp, not a client-side "now".
    await expect(lastUseLine(page).locator('time')).toHaveAttribute(
      'datetime',
      TOKEN_USED_LAST_USED_AT,
    );

    // The quickstart names the metadata §10 of the CLI contract needs: the project slug (the
    // `EVIDGATE_PROJECT` variable) and the token's name and prefix, which is the most a page may
    // ever show of an ingest token.
    const snippet = quickstartSnippet(page);
    await expect(snippet).toContainText(ONBOARDING_PROJECT_SLUG);
    await expect(snippet).toContainText(TOKEN_USED_NAME);
    await expect(snippet).toContainText(TOKEN_USED_PREFIX);
    await expect(page.locator('[data-testid="cli-quickstart-copy"]')).toBeVisible();
    // Metadata only: the corpus's one-time values are the only `FIXTURE_ONLY` strings in it, and a
    // snippet that named one would be the §8 leak in the very panel meant to prevent it.
    expect(await snippet.innerText(), 'the quickstart rendered a one-time value').not.toContain(
      'FIXTURE_ONLY',
    );
  });

  test('unusable credentials leave the derivation alone and report unhealthy', async ({ page }) => {
    await page.goto(onboardingUrl('en', 'onboarding-unusable'));
    await expect(shell(page)).toBeVisible();

    // The gate is presence (09.2.1-T1's `done[2]`): a revoked and an expired token are still two
    // rows, so the wizard stands on step 3 even though neither can verify anything. Health must not
    // move a step in either direction — that is what makes the panel safe to add.
    await expectCurrentStep(page, 3);
    await expectStepDone(page, 2, true);
    await expectStepDone(page, 3, false);
    // Next follows `done[3]` (there is no run yet), not the health state.
    await expect(nextButton(page)).toBeDisabled();

    await backButton(page).click();
    await expectCurrentStep(page, 2);
    await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'unhealthy');
    await expect(credentialRows(page)).toHaveCount(2);
    // No token was ever used, so the line says "not recorded" instead of inventing a timestamp.
    await expect(lastUseLine(page).locator('time')).toHaveCount(0);
    // Step 2 is done on presence, so the same Next is enabled on this step (the T1.3 gate).
    await expect(nextButton(page)).toBeEnabled();
    // With no usable credential the snippet keeps its "create a token first" branch: it names no
    // token, because there is none to name.
    expect(await quickstartSnippet(page).innerText()).not.toContain(TOKEN_USED_NAME);
  });
});

test.describe('a reload re-derives the panel exactly as it re-derives the step (AC1, AC2)', () => {
  test('a reload on step 2 lands on step 2 and re-renders the panel', async ({ page }) => {
    await openStepTwo(page, 'en', 'onboarding-project', 2);

    // Step 2 is the derived current step here, and the project has no credential at all: the panel
    // still renders, reporting "no credentials" rather than staying blank.
    await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'unhealthy');
    await expect(credentialRows(page)).toHaveCount(0);
    await expectNoStepInStorage(page, 'the first load');

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 2);
    await expect(connectCiPage(page)).toBeVisible();
    await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'unhealthy');
    await expectNoStepInStorage(page, 'the reload');
  });

  test('a reload of a credentialed project re-reads the last use from the server', async ({
    page,
  }) => {
    await openStepTwo(page, 'en', 'onboarding-token-used', 3);
    await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'healthy');
    await expect(lastUseLine(page).locator('time')).toHaveAttribute(
      'datetime',
      TOKEN_USED_LAST_USED_AT,
    );

    // The reload re-derives the step from the server, so the wizard stands on step 3 again; the
    // panel is not remembered anywhere, which is why Back has to re-read it from the same reads.
    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);
    await expectNoStepInStorage(page, 'the reload');

    await backButton(page).click();
    await expectCurrentStep(page, 2);
    await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'healthy');
    await expect(lastUseLine(page).locator('time')).toHaveAttribute(
      'datetime',
      TOKEN_USED_LAST_USED_AT,
    );
  });
});

test.describe('the unhealthy health panel is WCAG 2.2 AA clean (plan §8)', () => {
  // `onboarding-a11y.spec.ts` already scans step 2 with one usable credential (the metadata rows
  // of the `onboarding-token` seed). The state nothing else covers is this one: two credential
  // rows whose pills carry the "unusable" tone — the panel's own negative state, its "not
  // recorded" last-use line and its reason copy — which is where a contrast or labelling
  // regression would land first.
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`revoked and expired rows, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await openStepTwo(page, locale, 'onboarding-unusable', 3);

        await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'unhealthy');
        await expect(credentialRows(page)).toHaveCount(2);
        await expect(lastUseLine(page).locator('time')).toHaveCount(0);
        await expect(quickstart(page)).toBeVisible();
        await expect(quickstartSnippet(page)).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('lang', locale);
        // The theme the labels claim: a scan of the wrong token set would be a false pass.
        expect(await currentTheme(page)).toBe(theme);

        await expectNoA11yViolations(page, `onboarding step 2 unhealthy ${locale}/${theme}`);
      });
    }
  }
});

test.describe('the health panels never carry the one-time value (§8)', () => {
  test('a created token is revealed once and appears in neither panel nor storage', async ({
    page,
  }) => {
    await openStepTwo(page, 'en', 'onboarding-project', 2);

    await page.locator('[data-testid="connect-token-name"]').fill(TOKEN_NAME);
    await page.locator('[data-testid="connect-token-submit"]').click();

    // The value the reveal shows is the create answer's own one-time field.
    await expect(revealValue(page)).toHaveValue(FIXTURE_TOKEN_SECRET);
    // Exactly one element holds it, and that element is inside the reveal slot; nothing outside
    // holds it, and it reached neither storage, the cookie jar, the URL nor `history.state`.
    await expectSecretOnlyInReveal(page, FIXTURE_TOKEN_SECRET, 'the token reveal');

    // The two panels are the state §8 is most likely to leak into: the health panel lists the
    // credential that was just issued, and the quickstart names it. `innerHTML` covers attributes
    // (a `title`, an `aria-label`, a `data-*`) that a text-only check would miss.
    for (const [panelName, panel] of [
      ['the quickstart', quickstart(page)],
      ['the health panel', healthPanel(page)],
    ] as const) {
      await expect(panel).toBeVisible();
      const markup = await panel.innerHTML();
      expect(markup, `${panelName} rendered the one-time value`).not.toContain(
        FIXTURE_TOKEN_SECRET,
      );
    }
    // The snippet is metadata only, and this project's lists are empty, so it cannot name any
    // `egt_`/`evg_`-prefixed value at all — a stronger claim than "not this one secret".
    expect(await quickstartSnippet(page).innerText()).not.toMatch(/egt_|evg_/);

    // Named rather than implied by the footprint, because these are the two places §8 names.
    expect(
      await storageHits(page, FIXTURE_TOKEN_SECRET),
      'the one-time value reached browser storage',
    ).toEqual([]);

    // After Dismiss the value has no holder at all — while both panels stay mounted, so the empty
    // scan is not the trivial "the page went away" case. The create cannot add a row to the list
    // read that follows it (the corpus is static), so the panel still reports the seeded inventory
    // and the snippet still carries no credential.
    await page.locator('[data-testid="reveal-dismiss"]').click();
    await expectNoReveal(page);
    await expectSecretAbsent(page, FIXTURE_TOKEN_SECRET, 'after Dismiss');
    await expect(healthPanel(page)).toBeVisible();
    await expect(quickstart(page)).toBeVisible();
    expect(await healthPanel(page).innerHTML()).not.toContain(FIXTURE_TOKEN_SECRET);
    expect(await quickstart(page).innerHTML()).not.toContain(FIXTURE_TOKEN_SECRET);
  });
});

test.describe('a refused credential read renders the pattern, never a step (00 §9.2)', () => {
  const REFUSALS: readonly (readonly [CredentialListFailure, string])[] = [
    ['onboarding-forbidden', '403'],
    ['onboarding-project-not-found', '404'],
  ];

  for (const [failure, status] of REFUSALS) {
    test(`a ${status} on the token list renders its pattern`, async ({ page }) => {
      await page.goto(credentialListRefusalUrl('en', failure, CREATED_PROJECT));

      await expect(shell(page)).toBeVisible();
      // The refusal keeps its meaning: a 403 is forbidden, a 404 is not-found, and neither may be
      // softened into a step with an empty panel.
      const forbidden = page.locator('app-forbidden-pattern').first();
      const notFound = page.locator('app-not-found-pattern').first();
      if (failure === 'onboarding-forbidden') {
        await expect(forbidden).toBeVisible();
        await expect(page.locator('app-not-found-pattern')).toHaveCount(0);
      } else {
        await expect(notFound).toBeVisible();
        await expect(page.locator('app-forbidden-pattern')).toHaveCount(0);
      }

      // No stepper at all: a wizard that rendered one would be describing a project whose
      // credentials it could not read.
      await expect(page.locator('[data-testid="onboarding-stepper"]')).toHaveCount(0);
      await expectNoStepDone(page);
      await expect(healthPanel(page)).toHaveCount(0);
      await expect(quickstart(page)).toHaveCount(0);
      await expect(connectCiPage(page)).toHaveCount(0);
    });
  }
});
