import { expect, test, type Page } from '@playwright/test';
import { chooseScope, expectScopeValue, scopeControl } from './scope-controls';
import readyFixture from '../../../../fixtures/web-api/run-detail/input.json';
import {
  expectCurrentStep,
  onboardingUrl,
  type OnboardingSeed,
  type StepNumber,
} from './onboarding-support';
import { axeScan, axeViolations } from '../a11y/axe-scan';
import {
  ANONYMOUS_SESSION,
  currentTheme,
  LIMITED_GRANTS,
  localePath,
  LOCALES,
  ORG_ACME,
  overviewPath,
  PROJECT_CHECKOUT,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariant,
  withVariants,
  type ShellLocale,
} from './support';

const RULES = ['color-contrast', 'target-size'];
const WIDTHS = [1280, 320] as const;
const RUN_GRANTS = '?fixture.variant.listPermissions=settings-policy-readonly';
const ONBOARDING: readonly { step: StepNumber; seed: OnboardingSeed }[] = [
  { step: 1, seed: 'onboarding-empty' },
  { step: 2, seed: 'onboarding-project' },
  { step: 3, seed: 'onboarding-connected' },
  { step: 4, seed: 'onboarding-run-complete' },
  { step: 5, seed: 'onboarding-evidence' },
];

interface Flow {
  name: string;
  open: (page: Page, locale: ShellLocale) => Promise<void>;
}

const FLOWS: readonly Flow[] = [
  {
    name: 'login',
    async open(page, locale) {
      await page.goto(withVariant(localePath(locale, '/login'), ANONYMOUS_SESSION));
      await expect(page.locator('app-login-page main h1')).toBeVisible();
    },
  },
  {
    name: 'project switch',
    async open(page, locale) {
      await page.goto(withVariant(overviewPath(locale), LIMITED_GRANTS));
      const project = scopeControl(page, 1);
      await expectScopeValue(project, PROJECT_STOREFRONT);
      if ((await page.locator('#shell-navigation').getAttribute('inert')) !== null) {
        await page.locator('.nav-toggle').click();
      }
      await chooseScope(page, project, PROJECT_CHECKOUT);
      await expect(page).toHaveURL(new RegExp(`/projects/${PROJECT_CHECKOUT}/overview`));
      await expectScopeValue(project, PROJECT_CHECKOUT);
      await expect(page.locator('#main-content h1')).toBeVisible();
    },
  },
  ...ONBOARDING.map(({ step, seed }): Flow => ({
    name: `onboarding step ${step}`,
    async open(page, locale) {
      await page.goto(onboardingUrl(locale, seed));
      await expectCurrentStep(page, step);
      await expect(page.locator('#onboarding-step-title')).toBeVisible();
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
    async open(page, locale) {
      await page.goto(
        withVariants(
          localePath(locale, `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
          [RUN_GRANTS, '?fixture.variant.listRuns=runs'],
        ),
      );
      await expect(page.locator('app-run-list-page')).toHaveAttribute(
        'data-run-list-status',
        'ready',
      );
      await expect(page.locator('[data-run-row]').first()).toBeVisible();
    },
  },
  {
    name: 'run detail',
    async open(page, locale) {
      await page.goto(
        withVariants(
          localePath(
            locale,
            `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${readyFixture.body.run_id}`,
          ),
          [RUN_GRANTS, '?fixture.variant.getRun=detail-ready'],
        ),
      );
      await expect(page.locator('app-run-detail-page')).toHaveAttribute(
        'data-run-detail-status',
        'ready',
      );
      await expect(page.locator('[data-attempt-row]').first()).toBeVisible();
    },
  },
  {
    name: 'shared UI gallery',
    async open(page, locale) {
      await page.goto(localePath(locale, '/dev/ui-gallery'));
      await expect(page.locator('h1').first()).toBeVisible();
    },
  },
];

async function scan(page: Page, context: string): Promise<void> {
  // Scan the whole document, including shell chrome and off-screen content; no failing regions
  // are excluded. The shared helper enables the WCAG 2.2 AA tag, which includes target-size.
  const results = await axeScan(page);
  const findings = axeViolations(results).filter((rule) => RULES.includes(rule.id));
  const outcomes = [...results.passes, ...results.violations, ...results.incomplete];
  const counts = Object.fromEntries(
    RULES.map((id) => [id, findings.find((rule) => rule.id === id)?.targets.length ?? 0]),
  );
  const incomplete = results.incomplete
    .filter((rule) => RULES.includes(rule.id))
    .map((rule) => ({ id: rule.id, targets: rule.nodes.map((node) => node.target) }));
  console.log(JSON.stringify({ context, violations: counts, findings, incomplete }));
  for (const id of RULES) {
    expect(
      outcomes.some((rule) => rule.id === id),
      `${id} evaluated on ${context}`,
    ).toBe(true);
  }
  expect.soft(findings, `axe contrast/target violations on ${context}`).toEqual([]);

  const geometry = await page.evaluate(() => ({
    viewport: document.documentElement.clientWidth,
    document: document.documentElement.scrollWidth,
    body: document.body.scrollWidth,
  }));
  console.log(JSON.stringify({ context, reflow: geometry }));
  expect
    .soft(geometry.document, `document reflows without horizontal scrolling on ${context}`)
    .toBeLessThanOrEqual(geometry.viewport);
  expect
    .soft(geometry.body, `body reflows without horizontal scrolling on ${context}`)
    .toBeLessThanOrEqual(geometry.viewport);
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const width of WIDTHS) {
      for (const flow of FLOWS) {
        test(`${flow.name}: ${locale}/${theme}/${width} contrast, targets and reflow`, async ({
          page,
        }) => {
          await page.setViewportSize({ width, height: 900 });
          await page.emulateMedia({ colorScheme: theme });
          await seedStoredTheme(page, theme);
          await flow.open(page, locale);
          // Login and gallery have no shell ThemeService; choose the same resolved CSS theme.
          await page.evaluate((choice) => {
            document.documentElement.setAttribute('data-theme', choice);
          }, theme);
          await expect(page.locator('html')).toHaveAttribute('lang', locale);
          expect(await currentTheme(page)).toBe(theme);
          await scan(page, `${flow.name}/${locale}/${theme}/${width}`);
        });
      }
    }
  }
}
