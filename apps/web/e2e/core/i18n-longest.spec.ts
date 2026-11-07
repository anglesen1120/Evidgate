// The narrowest supported viewport is where a long translation breaks a layout first, so every
// built core-flow page is measured at 320px in `vi`, in both themes, against the longest strings
// the catalogue holds for that page. Overflow is reported, never excluded: a clipped string is a
// defect in the page, and the failure names the string, the element and the measured widths.
import { expect, test, type Page } from '@playwright/test';

import { runDetailPath } from '../shell/run-detail.support';
import {
  expectCurrentStep,
  type OnboardingSeed,
  type StepNumber,
} from '../shell/onboarding-support';
import {
  ANONYMOUS_SESSION,
  currentTheme,
  LIMITED_GRANTS,
  localePath,
  overviewPath,
  PROJECT_CHECKOUT,
  seedStoredTheme,
  THEMES,
  withVariant,
  type ShellLocale,
  type ShellTheme,
} from '../shell/support';
import {
  coreRunListPath,
  loadViStrings,
  longestOnPage,
  pageGeometry,
  pageText,
  scanLongestStrings,
  type ClippingFinding,
} from './i18n-longest.support';
import { LoginPage } from './pages/login.page';
import { OnboardingWizard } from './pages/onboarding-wizard.page';
import { RunDetailPage } from './pages/run-detail.page';
import { ScopeSwitcher } from './pages/scope-switcher.page';

const LOCALE: ShellLocale = 'vi';
const VIEWPORT = { width: 320, height: 800 };

/** A page must paint at least this many tracked strings, or the probe match proved nothing. */
const MIN_TRACKED = 1;

/** The wizard seeds of one step each, in wizard order. */
const ONBOARDING: readonly { step: StepNumber; seed: OnboardingSeed }[] = [
  { step: 1, seed: 'onboarding-empty' },
  { step: 2, seed: 'onboarding-project' },
  { step: 3, seed: 'onboarding-connected' },
  { step: 4, seed: 'onboarding-run-complete' },
  { step: 5, seed: 'onboarding-evidence' },
];

interface CoreFlow {
  readonly name: string;
  open(page: Page): Promise<void>;
}

const FLOWS: readonly CoreFlow[] = [
  {
    name: 'project overview',
    async open(page) {
      await page.goto(withVariant(overviewPath(LOCALE), LIMITED_GRANTS));
      await expect(page.locator('#main-content h1')).toBeVisible();
    },
  },
  {
    name: 'project overview with the scope switcher open',
    async open(page) {
      // The switcher sits in the drawer, which is off-canvas and inert at this width, and the
      // header's compact copy is only a placeholder: the drawer has to be open to reach it.
      await page.goto(withVariant(overviewPath(LOCALE), LIMITED_GRANTS));
      await expect(page.locator('#main-content h1')).toBeVisible();
      const toggle = page.locator('button[aria-controls="shell-navigation"]');
      await toggle.click();
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      const scope = new ScopeSwitcher(page);
      await expect(scope.control(1)).toBeEnabled();
      await scope.control(1).click();
      // The list only scales in once open, so waiting for its first option also waits out the
      // entrance: measuring earlier would read the animation instead of the layout.
      await expect(scope.option(PROJECT_CHECKOUT)).toBeVisible();
    },
  },
  ...ONBOARDING.map(({ step, seed }): CoreFlow => ({
    name: `onboarding step ${step}`,
    async open(page) {
      const wizard = new OnboardingWizard(page);
      await wizard.open(LOCALE, seed);
      await expectCurrentStep(page, step);
      await expect(page.locator('#onboarding-step-title')).toBeVisible();
      // The two steps whose body loads on its own, so the text probed below has arrived.
      if (step === 2) {
        await expect(page.locator('[data-testid="connect-ci-page"]')).toBeVisible();
      }
      if (step === 5) {
        await expect(page.locator('[data-testid="enable-collection-confirm"]')).toBeVisible();
      }
    },
  })),
  {
    name: 'run list',
    async open(page) {
      const runs = new RunDetailPage(page);
      await runs.openList(coreRunListPath(LOCALE));
      await expect(runs.rows.first()).toBeVisible();
    },
  },
  {
    name: 'run detail with a missing shard',
    async open(page) {
      const detail = new RunDetailPage(page);
      await detail.openDetail(
        runDetailPath({ locale: LOCALE, decision: 'evaluated-fail-missing-shard' }),
      );
      await expect(detail.evidencePanel.locator('[data-evidence-missing]').first()).toBeVisible();
    },
  },
];

/** The signed-out core flow: the only page an anonymous visitor can reach. */
const SIGN_IN: CoreFlow = {
  name: 'sign-in',
  async open(page) {
    await page.goto(withVariant(localePath(LOCALE, '/login'), ANONYMOUS_SESSION));
    await new LoginPage(page).expectReady();
  },
};

function describeFindings(
  context: string,
  route: string,
  findings: readonly ClippingFinding[],
): string {
  if (findings.length === 0) {
    return `${context}: no tracked longest vi string is clipped`;
  }
  const lines = findings.map(
    (finding) =>
      `${context} ${route} ${finding.kind} ${finding.stringId} "${finding.text}" at ` +
      `${finding.selector}: scrollWidth=${finding.scrollWidth} ` +
      `clientWidth=${finding.clientWidth} left=${finding.left} right=${finding.right} ` +
      `viewport=${finding.viewport}`,
  );
  return `${findings.length} longest vi string(s) do not fit on ${context}:\n${lines.join('\n')}`;
}

async function expectLongestStringsFit(
  page: Page,
  flow: CoreFlow,
  theme: ShellTheme,
): Promise<void> {
  await seedStoredTheme(page, theme);
  await page.emulateMedia({ colorScheme: theme });
  await flow.open(page);
  if ((await currentTheme(page)) === null) {
    // The sign-in page renders outside the shell and has no theme service, so its token set is
    // the `<html>` attribute; light is the set with no attribute at all.
    await page.evaluate((choice) => {
      if (choice === 'light') {
        document.documentElement.removeAttribute('data-theme');
      } else {
        document.documentElement.setAttribute('data-theme', choice);
      }
    }, theme);
  }
  await expect(page.locator('html')).toHaveAttribute('lang', LOCALE);

  const context = `${flow.name} (${LOCALE}, ${theme})`;
  const applied = await currentTheme(page);
  expect(applied === null ? 'light' : applied, `${context} resolves the requested tokens`).toBe(
    theme,
  );
  const geometry = await pageGeometry(page);
  expect(geometry.document, `document reflow, ${context}`).toBeLessThanOrEqual(geometry.viewport);
  expect(geometry.body, `body reflow, ${context}`).toBeLessThanOrEqual(geometry.viewport);

  const tracked = longestOnPage(loadViStrings(), await pageText(page));
  console.log(JSON.stringify({ context, tracked: tracked.map((entry) => entry.id) }));
  const route = new URL(page.url()).pathname;
  const scanned = await scanLongestStrings(page, tracked);
  expect(
    scanned.matched.length,
    `${context} paints at least ${MIN_TRACKED} of the tracked longest vi strings`,
  ).toBeGreaterThanOrEqual(MIN_TRACKED);
  expect(scanned.findings, describeFindings(context, route, scanned.findings)).toEqual([]);
}

test.describe('longest vi strings at the narrowest viewport, signed in', () => {
  test.use({ viewport: VIEWPORT });

  for (const theme of THEMES) {
    for (const flow of FLOWS) {
      test(`${flow.name}: no overflow or clipping (${theme})`, async ({ page }) => {
        await expectLongestStringsFit(page, flow, theme);
      });
    }
  }
});

test.describe('longest vi strings at the narrowest viewport, signed out', () => {
  test.use({ storageState: { cookies: [], origins: [] }, viewport: VIEWPORT });

  for (const theme of THEMES) {
    test(`sign-in: no overflow or clipping (${theme})`, async ({ page }) => {
      await expectLongestStringsFit(page, SIGN_IN, theme);
    });
  }
});

test.describe('the probe list itself', () => {
  test('the vi catalogue yields the core-flow strings, longest first', () => {
    const strings = loadViStrings();
    expect(strings.length, 'core-flow translations are tracked').toBeGreaterThan(300);
    const longest = strings[0]?.length ?? 0;
    expect(longest, 'the longest tracked string is a sentence').toBeGreaterThan(100);
    const lengths = strings.map((entry) => entry.length);
    expect([...lengths].sort((left, right) => right - left)).toEqual(lengths);
    expect(
      strings.filter((entry) => entry.id.startsWith('settings.')),
      'pages outside the core flows are never probed',
    ).toEqual([]);
  });
});
