// The core-flow axe gate: every built FR-09-12 flow — sign-in, project switch, run list/detail and
// onboarding — scanned in both bundles and both token sets, through the shared assertion that the
// shell suite uses, so one exception list covers both suites. Failure triage and the evidence viewer
// have no screen yet (e2e/core/README.md), so no case pretends to cover them.
import { expect, test, type Page } from '@playwright/test';

import runDetail from '../../../../fixtures/web-api/run-detail/input.json';
import { runDetailPath } from '../shell/run-detail.support';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  ANONYMOUS_SESSION,
  currentTheme,
  LIMITED_GRANTS,
  localePath,
  LOCALES,
  overviewPath,
  ORG_ACME,
  PROJECT_CHECKOUT,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  waitForDialogAnimations,
  withVariant,
  withVariants,
  type ShellLocale,
  type ShellTheme,
} from '../shell/support';
import { LoginPage } from './pages/login.page';
import { OnboardingWizard } from './pages/onboarding-wizard.page';
import { RunDetailPage } from './pages/run-detail.page';
import { ScopeSwitcher } from './pages/scope-switcher.page';

/** The deep link an anonymous visitor attempts first; it becomes `/login` with a return URL. */
const DEEP_LINK = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`;
/** The grant set holding `run:read`; the list and detail reads below need it on the review routes. */
const RUN_READ_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';
const RUNS = '?fixture.variant.listRuns=runs';
const DETAIL_READY = '?fixture.variant.getRun=detail-ready';
/** The list corpus's first row, so the click opens a run the list really shows. */
const RUN_ID = runDetail.body.run_id;

// axeScan injects axe as an inline script, which Trusted Types refuses; CSP has its own specs.
test.use({ bypassCSP: true });

/** The review address of the run list; the detail it opens keeps the same per-operation answers. */
function listPath(locale: ShellLocale): string {
  return withVariants(
    localePath(locale, `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    [RUN_READ_GRANT, RUNS, DETAIL_READY],
  );
}

/**
 * The token set the sign-in page is scanned with. It is mounted outside the shell and has no
 * `ThemeService`, so its theme is the `<html>` attribute — the way a stylesheet resolves it.
 */
async function showTheme(page: Page, theme: ShellTheme): Promise<void> {
  await page.evaluate((choice) => {
    if (choice === 'light') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', choice);
  }, theme);
}

test.describe('sign-in is WCAG 2.2 AA clean', () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`the sign-in page, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.goto(withVariant(localePath(locale, DEEP_LINK), ANONYMOUS_SESSION));
        const login = new LoginPage(page);
        await login.expectReady();
        await showTheme(page, theme);
        await expect(page.locator('html')).toHaveAttribute('lang', locale);
        expect(await currentTheme(page)).toBe(theme === 'light' ? null : theme);

        await expectNoA11yViolations(page, `sign-in ${locale}/${theme}`);
      });
    }
  }
});

test.describe('the scope switcher is WCAG 2.2 AA clean while it is open', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`the project list, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.goto(withVariant(overviewPath(locale), LIMITED_GRANTS));
        await expect(page.locator('#main-content h1')).toBeVisible();
        expect(await currentTheme(page)).toBe(theme);

        // The open list is a portal over the page, so the scan has to see both: the trigger that
        // holds the value, and the listbox the visitor is choosing from.
        const scope = new ScopeSwitcher(page);
        await expect(scope.control(1)).toBeEnabled();
        await scope.control(1).click();
        await waitForDialogAnimations(page, 'hlm-select-content[data-state="open"]');
        await expect(scope.option(PROJECT_CHECKOUT)).toBeVisible();

        await expectNoA11yViolations(page, `project switch ${locale}/${theme}`);
      });
    }
  }
});

test.describe('the run list and the run it opens are WCAG 2.2 AA clean', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`the list and its detail, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        const detail = new RunDetailPage(page);
        await detail.openList(listPath(locale));
        await expect(detail.row(RUN_ID)).toBeVisible();
        expect(await currentTheme(page)).toBe(theme);

        await expectNoA11yViolations(page, `run list ${locale}/${theme}`);

        // The navigation keeps the list's query, so this is the same session and the same theme.
        await detail.openRun(RUN_ID);
        await expectNoA11yViolations(page, `run detail ${locale}/${theme}`);
      });

      test(`an incomplete evidence revision, ${locale} bundle, ${theme} tokens`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        const detail = new RunDetailPage(page);
        await detail.openDetail(
          runDetailPath({ locale, decision: 'evaluated-fail-missing-shard' }),
        );
        await expect(detail.evidencePanel.locator('[data-evidence-missing]')).toBeVisible();
        expect(await currentTheme(page)).toBe(theme);

        // Evidence is the default tab and the decision is the other one: both are scanned, because
        // the FAIL chip and its reason list only exist on the second.
        await expectNoA11yViolations(page, `run detail, missing shard evidence ${locale}/${theme}`);
        await detail.showDecision();
        await expectNoA11yViolations(page, `run detail, missing shard decision ${locale}/${theme}`);
      });
    }
  }
});

test.describe('the onboarding wizard is WCAG 2.2 AA clean', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`step 2 of the wizard, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        // Step 2 carries the shell, the stepper and a form; the other steps differ only in their
        // own page, and steps 2 and 5 already have their wide matrices in the shell suite.
        await seedStoredTheme(page, theme);
        const wizard = new OnboardingWizard(page);
        await wizard.open(locale, 'onboarding-project');
        await wizard.expectStep(2);
        expect(await currentTheme(page)).toBe(theme);

        await expectNoA11yViolations(page, `onboarding step 2 ${locale}/${theme}`);
      });
    }
  }
});
