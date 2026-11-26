// Step 2's connection-health panel and its CLI quickstart, in real Chromium against the FIXTURES
// build (`playwright.fixtures.config.ts`). Step 2 is optional: a project resumes at first-run
// step 3 whatever its credentials hold, and these cases reach the panel with Back.
//
// Four rules shape what these cases assert:
//
// 1. **Health is a derivation, never a step.** `deriveConnectionHealth` reads the token list
//    `OnboardingProgressService` already fetched; it feeds no done-condition. A project whose only
//    credentials are a revoked and an expired token therefore stays on step 3 with step 2 not
//    done, while the panel reports `unhealthy`. Both halves are asserted together: a health
//    state that quietly moved a step would pass a panel-only check and still break the wizard.
// 2. **The panel and the quickstart show server metadata only.** The last-use line renders the
//    `last_used_at` the server answered, in a `<time datetime>`; the quickstart names the token's
//    name/prefix and the project slug, and never a value that could act as a credential. A
//    one-time value is shown once, so the create case re-uses `secretFootprint` and also reads
//    both panels' `innerHTML` (an attribute would pass a text-only check), and it re-scans after
//    Dismiss, while the panels stay mounted, so the empty result is not the "page went away" case.
// 3. **No localized string identifies an element.** Every assertion keys on the DOM contract the
//    panel publishes (`data-health-state`, the `data-testid` hooks, `<time datetime>`, the native
//    `disabled` attribute), so the same spec passes on the `en` and the `vi` bundle.
// 4. **A refused credential read is scoped to the credential page.** The token list is not a
//    progress read: a 403/404 on it leaves the wizard at its resume step and shows the page's own
//    refresh failure, with no panel built from a list nobody read. Its retry button only re-reads
//    the list, so the fixture backend log must hold no write after it. Reaching that state needs
//    the `onboarding-forbidden` and `onboarding-project-not-found` variants of `listIngestTokens`.
import { expect, test, type Page } from '@playwright/test';

import {
  expectNoReveal,
  expectSecretAbsent,
  expectSecretOnlyInReveal,
  FIXTURE_TOKEN_SECRET,
  fixtureWrites,
  ONBOARDING_PROJECT_SLUG,
  openConnectStep,
  refreshFailed,
  revealValue,
  TOKEN_USED_LAST_USED_AT,
  TOKEN_USED_NAME,
  TOKEN_USED_PREFIX,
  tokenListReads,
} from './onboarding-connection-support';
import {
  CREATED_PROJECT,
  credentialListRefusalUrl,
  expectCurrentStep,
  expectNoStepInStorage,
  expectStepDone,
  nextButton,
  onboardingUrl,
  shell,
  stepperItems,
  STEP_NUMBERS,
  type CredentialListFailure,
  type OnboardingSeed,
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
 * panel and the quickstart — renders on. Every seed used here resumes at step 3, so Back is the
 * documented way to read the panel.
 *
 * The locale is a parameter because a bundle is a separate build: a case that scanned `/en/`
 * while asserting `html[lang]="vi"` would fail loudly, and one that relaxed that assertion would
 * scan a bundle nobody asked about.
 */
async function openStepTwo(page: Page, locale: ShellLocale, seed: OnboardingSeed): Promise<void> {
  await page.goto(onboardingUrl(locale, seed));
  await expect(shell(page)).toBeVisible();
  await openConnectStep(page);
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
    await openStepTwo(page, 'en', 'onboarding-token-used');

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

    // Credentials are optional and health is a derivation, so a revoked and an expired token move
    // no step: the wizard stands on first-run step 3 and step 2 is not marked done.
    await expectCurrentStep(page, 3);
    await expectStepDone(page, 2, false);
    await expectStepDone(page, 3, false);
    // Next follows `done[3]` (there is no run yet), not the health state.
    await expect(nextButton(page)).toBeDisabled();

    await openConnectStep(page);
    await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'unhealthy');
    await expect(credentialRows(page)).toHaveCount(2);
    // No token was ever used, so the line says "not recorded" instead of inventing a timestamp.
    await expect(lastUseLine(page).locator('time')).toHaveCount(0);
    // Step 2 is optional: Next is open on it whatever the inventory holds.
    await expect(nextButton(page)).toBeEnabled();
    // With no usable credential the snippet keeps its "create a token first" branch: it names no
    // token, because there is none to name.
    expect(await quickstartSnippet(page).innerText()).not.toContain(TOKEN_USED_NAME);
  });
});

test.describe('a reload re-derives the panel exactly as it re-derives the step (AC1, AC2)', () => {
  test('a reload on step 2 lands on step 3 and re-renders the panel', async ({ page }) => {
    await openStepTwo(page, 'en', 'onboarding-project');

    // The project has no credential at all: the panel still renders, reporting "no credentials"
    // rather than staying blank.
    await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'unhealthy');
    await expect(credentialRows(page)).toHaveCount(0);
    await expectNoStepInStorage(page, 'the first load');

    await page.reload();
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);
    await expectNoStepInStorage(page, 'the reload');
    await openConnectStep(page);
    await expect(healthPanel(page)).toHaveAttribute('data-health-state', 'unhealthy');
  });

  test('a reload of a credentialed project re-reads the last use from the server', async ({
    page,
  }) => {
    await openStepTwo(page, 'en', 'onboarding-token-used');
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

    await openConnectStep(page);
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
        await openStepTwo(page, locale, 'onboarding-unusable');

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
    await openStepTwo(page, 'en', 'onboarding-project');

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

test.describe('a refused credential read stays on the credential page (00 §9.2)', () => {
  const REFUSALS: readonly (readonly [CredentialListFailure, string])[] = [
    ['onboarding-forbidden', '403'],
    ['onboarding-project-not-found', '404'],
  ];

  for (const [failure, status] of REFUSALS) {
    test(`a ${status} on the token list is a scoped failure with a read-only retry`, async ({
      page,
    }) => {
      await page.goto(credentialListRefusalUrl('en', failure, CREATED_PROJECT));

      // The wizard is unaffected: the project reads succeeded, so it still resumes at step 3 and
      // shows its whole stepper, with step 1 done and nothing claimed past it.
      await expect(shell(page)).toBeVisible();
      await expectCurrentStep(page, 3);
      await expect(stepperItems(page)).toHaveCount(STEP_NUMBERS.length);
      await expectStepDone(page, 1, true);
      await expectStepDone(page, 2, false);
      await expectStepDone(page, 3, false);

      await openConnectStep(page);
      await expect(refreshFailed(page)).toBeVisible();
      // No panel is built from a list nobody read, and no row is invented.
      await expect(healthPanel(page)).toHaveCount(0);
      await expect(quickstart(page)).toHaveCount(0);
      await expect(credentialRows(page)).toHaveCount(0);
      await expect(page.locator('[data-testid="connect-token-row"]')).toHaveCount(0);
      expect(await fixtureWrites(page), 'the refused read sent a command').toEqual([]);

      // Retry re-reads the list, which refuses again: the failure stays scoped and nothing writes.
      // The read count proves the button did something; the write log proves it did only that.
      const readsBefore = await tokenListReads(page);
      expect(readsBefore, 'the page never read the token list').toBeGreaterThan(0);
      await refreshFailed(page).getByRole('button').click();
      await expect.poll(() => tokenListReads(page)).toBe(readsBefore + 1);
      await expect(refreshFailed(page)).toBeVisible();
      await expect(healthPanel(page)).toHaveCount(0);
      await expectCurrentStep(page, 2);
      expect(await fixtureWrites(page), 'the retry sent a command').toEqual([]);
    });
  }
});
