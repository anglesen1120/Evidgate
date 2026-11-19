// The shell suite bypasses CSP for axe, so this file turns enforcement back on and walks every
// route of the app's own table: product, review-only and pattern routes, in both locale bundles.
// Each document and each lazily loaded surface must raise no `securitypolicyviolation` event and no
// CSP console error. `e2e/csp.spec.ts` asserts the policy text on the production build; this
// asserts that the app itself can run inside it.
import { expect, test, type Page } from '@playwright/test';

import defaultGrants from '../../../../fixtures/web-api/permissions-list/input.json';
import limitedGrants from '../../../../fixtures/web-api/permissions-list-limited/input.json';
import onboardingGrants from '../../../../fixtures/web-api/permissions-list-onboarding/input.json';
import adminGrants from '../../../../fixtures/web-api/permissions-list-settings-admin/input.json';
import policyGrants from '../../../../fixtures/web-api/permissions-list-settings-policy-readonly/input.json';
import secretsGrants from '../../../../fixtures/web-api/permissions-list-settings-secrets-write/input.json';
import noTabGrants from '../../../../fixtures/web-api/permissions-list-settings-no-tab/input.json';
import usageGrants from '../../../../fixtures/web-api/permissions-list-usage-read/input.json';
import runFixture from '../../../../fixtures/web-api/run-detail/input.json';
import suiteFixture from '../../../../fixtures/web-api/suite-revision/input.json';

import { seedQuery, type OnboardingSeed } from './onboarding-support';
import { fillParams, surfacesInRouteTable, type Surface } from './route-surfaces';
import { HEAD_ID } from './settings-policy.support';
import {
  ANONYMOUS_SESSION,
  LIMITED_GRANTS,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  SECRETS_LIST,
  SETTINGS_ADMIN_GRANTS,
  SETTINGS_NO_TAB_GRANTS,
  USAGE_READ_GRANTS,
  localePath,
  overviewPath,
  withVariants,
} from './support';

test.use({ bypassCSP: false });

interface ViolationRecord {
  readonly directive: string;
  readonly blockedURI: string;
  readonly sample: string;
}

interface ProbeWindow extends Window {
  __violations?: ViolationRecord[];
}

/** A corpus session that grants a known set of codes, and the query that selects it. */
interface GrantSet {
  readonly query: string;
  readonly codes: readonly string[];
}

/** Only the console messages a policy refusal produces. */
const CSP_CONSOLE = /content security policy|trusted ?types|trustedhtml|trustedscript/i;

/** The wizard's stepper: the one element every ready state of it renders. */
const WIZARD = '[data-testid="onboarding-stepper"]';

/** The components that replace their own history entry, so the address is not the one requested. */
const REPLACES_HISTORY: Readonly<Record<string, true>> = { CallbackPage: true };

/**
 * How each component proves it rendered. Every selector is one a sibling spec already relies on,
 * and the walk's own test fails when the route table grows a component this map does not name.
 */
const READY: Readonly<Record<string, string>> = {
  LoginPage: 'app-login-page',
  CallbackPage: 'app-login-page',
  ProjectOverviewPage: 'app-project-overview',
  RouteStubPage: 'app-route-stub',
  ForbiddenPage: 'app-forbidden-page',
  NoOrganizationPage: 'app-no-organization-page',
  ServiceUnavailablePage: 'app-service-unavailable-page',
  NotFoundPage: 'app-not-found-page',
  UiGalleryComponent: 'app-ui-gallery',
  MembersPage: 'app-members-page[data-members-phase="ready"]',
  AuditPage: 'app-audit-page[data-audit-phase="ready"]',
  SettingsTabPlaceholderPage: 'app-settings-tab-placeholder',
  // The list renders one row per reference, so the first row is the surface's unique ready target.
  SecretsPage: 'app-secrets-page app-secret-reference-list [data-secret-reference]:first-child',
  ApiTokensPage: 'app-api-tokens-page [data-api-token-row]:first-child',
  SettingsNoTabsPage: 'app-settings-no-tabs',
  PolicyPage: 'app-policy-page[data-policy-phase="ready"]',
  QuotaPanelComponent: 'app-quota-panel[data-quota-state="ready"]',
  RunListPage: 'app-run-list-page[data-run-list-status="ready"]',
  RunDetailPage: 'app-run-detail-page[data-run-detail-status="ready"]',
  SuiteCompositionComponent: 'app-suite-composition[data-suite-composition-status="ready"]',
  OnboardingShellComponent: WIZARD,
  SecurityEventsPage: 'app-security-events-page [data-security-apply]',
  CaseListPage: 'app-case-list-page [data-catalog-apply]',
  AuthoringPage: 'app-authoring-page [data-authoring-breadcrumb]',
  ImportExportPage: 'app-authoring-import-export-page [data-impex-import-tab]',
  AiSettingsPage: 'app-ai-settings-page [data-ai-save]',
  EditorPage: 'app-authoring-editor #case-title',
};

/** The fields of a catalogue document the walk reads; the corpus rows carry more than this. */
interface PermissionCatalogue {
  readonly body: {
    readonly items: readonly { readonly code: string; readonly tenant_assignable: boolean }[];
  };
}

/** The client derives grants from the `tenant_assignable` rows alone, so the codes mirror that. */
function grantedCodes(catalogue: PermissionCatalogue): readonly string[] {
  return catalogue.body.items.filter((row) => row.tenant_assignable).map((row) => row.code);
}

/**
 * The corpus sessions a visit may pick from; every gated route has one that grants its codes, so
 * each surface is walked as a rendered page.
 */
const GRANTS: readonly GrantSet[] = [
  { query: '', codes: grantedCodes(defaultGrants) },
  { query: SETTINGS_ADMIN_GRANTS, codes: grantedCodes(adminGrants) },
  { query: LIMITED_GRANTS, codes: grantedCodes(limitedGrants) },
  {
    query: '?fixture.variant.listPermissions=settings-policy-readonly',
    codes: grantedCodes(policyGrants),
  },
  { query: '?fixture.variant.listPermissions=onboarding', codes: grantedCodes(onboardingGrants) },
  {
    query: '?fixture.variant.listPermissions=settings-secrets-write',
    codes: grantedCodes(secretsGrants),
  },
  { query: USAGE_READ_GRANTS, codes: grantedCodes(usageGrants) },
  {
    // A TypeScript specimen (dev-fixtures), not a corpus directory: the admin rows plus the
    // API token codes, so the dev-only token screen is walked as a rendered page.
    query: '?fixture.variant.listPermissions=settings-api-tokens-write',
    codes: [...grantedCodes(adminGrants), 'api_token:read', 'api_token:write', 'run:read'],
  },
];

const SURFACES: readonly Surface[] = surfacesInRouteTable();

/**
 * The Settings index renders only for a caller with `organization:read` and no tab, so it cannot
 * take any session that satisfies the entry guard: a broader one redirects to a tab first.
 */
const NO_TAB_SESSION: GrantSet = {
  query: SETTINGS_NO_TAB_GRANTS,
  codes: grantedCodes(noTabGrants),
};

/** The ids a route needs, from the corpus: the same rows the sibling specs open. */
const IDS: Readonly<Record<string, string>> = {
  orgId: ORG_ACME,
  projectId: PROJECT_STOREFRONT,
  runId: runFixture.body.run_id,
  revisionId: suiteFixture.body.id,
  caseId: 'a4430000-0000-4000-8000-000000000001',
};

/**
 * The corpus session that satisfies every guard on the way to a surface. A gated surface prefers a
 * session that names its codes (the plain catalogue is the one a signed-in visitor already holds),
 * and only falls back to the plain one when no other set covers it.
 */
function grantOf(permissions: readonly string[]): GrantSet | undefined {
  const covers = ({ codes }: GrantSet): boolean =>
    permissions.every((code) => codes.includes(code));
  const preferred = permissions.length === 0 ? GRANTS : GRANTS.filter(({ query }) => query !== '');
  return preferred.find(covers) ?? GRANTS.find(covers);
}

/** The session that reaches a surface: the index's own, every other surface's narrowest match. */
const WALK: readonly { surface: Surface; grant: GrantSet }[] = SURFACES.map((surface) => {
  const grant =
    surface.component === 'SettingsNoTabsPage' ? NO_TAB_SESSION : grantOf(surface.permissions);
  // A surface whose codes no corpus session holds could only be asserted as a refusal; it must
  // fail the walk rather than drop out of it.
  if (grant === undefined) throw new Error(`No corpus session grants ${surface.path}`);
  return { surface, grant };
});

/** The server state a surface needs beyond its grant to reach the ready contract. */
function dataVariants(surface: Surface): readonly string[] {
  switch (surface.component) {
    case 'LoginPage':
    case 'CallbackPage':
      return [ANONYMOUS_SESSION];
    case 'OnboardingShellComponent': {
      const seed: OnboardingSeed = surface.path.endsWith(':projectId')
        ? 'onboarding-project'
        : 'onboarding-empty';
      return [seedQuery(seed)];
    }
    case 'SecretsPage':
      return [SECRETS_LIST];
    case 'PolicyPage':
      return [`?revision=${HEAD_ID}`];
    case 'RunListPage':
      return ['?fixture.variant.listRuns=runs'];
    default:
      return [];
  }
}

/** Records violations from the first byte of the document, plus the console errors they emit. */
async function installProbe(page: Page): Promise<string[]> {
  const consoleErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error' && CSP_CONSOLE.test(message.text())) {
      consoleErrors.push(message.text());
    }
  });
  await page.addInitScript(() => {
    const probe = window as ProbeWindow;
    probe.__violations = [];
    document.addEventListener(
      'securitypolicyviolation',
      (event) => {
        probe.__violations?.push({
          directive: event.effectiveDirective,
          blockedURI: event.blockedURI,
          sample: event.sample,
        });
      },
      true,
    );
  });
  return consoleErrors;
}

async function violations(page: Page): Promise<ViolationRecord[]> {
  return page.evaluate(() => (window as ProbeWindow).__violations ?? []);
}

/** Lets the settled surface paint and queue its events before the probe is read. */
async function assertClean(page: Page, errors: readonly string[], path: string): Promise<void> {
  await page.evaluate(async () => {
    await new Promise((done) => requestAnimationFrame(done));
    await new Promise((done) => requestAnimationFrame(done));
    await new Promise((done) => setTimeout(done, 0));
  });
  expect(await violations(page), `CSP events on ${path}`).toEqual([]);
  expect(errors, `CSP console errors on ${path}`).toEqual([]);
}

/** The project address of the wizard, the one a resumed step can hang off. */
function resumedWizard(): Surface {
  const surface = SURFACES.find(
    ({ component, path }) =>
      component === 'OnboardingShellComponent' && path.endsWith(':projectId'),
  );
  if (surface === undefined) throw new Error('The route table no longer mounts the wizard');
  return surface;
}

/** The resumed wizard states on top of the two addresses the walk itself visits. */
const RESUMED_SEEDS = [
  'onboarding-token',
  'onboarding-run-complete',
  'onboarding-evidence',
] as const;

test('the walk covers the route table and names every surface', () => {
  expect(SURFACES.length).toBeGreaterThanOrEqual(20);
  expect(new Set(SURFACES.map(({ path }) => path)).size).toBe(SURFACES.length);
  for (const surface of SURFACES) {
    expect(READY[surface.component], `${surface.path} renders ${surface.component}`).toBeDefined();
    expect(fillParams(surface.path, IDS), `${surface.path} has no corpus id`).not.toMatch(/:/);
  }
  // The Settings index is a surface of its own when the session holds no tab, and it is the one
  // surface whose session cannot be derived from its guards.
  const noTabs = WALK.filter(({ surface }) => surface.component === 'SettingsNoTabsPage');
  expect(noTabs.map(({ grant }) => grant.query)).toEqual([SETTINGS_NO_TAB_GRANTS]);
});

for (const locale of LOCALES) {
  for (const { surface, grant } of WALK) {
    test(`the policy allows ${surface.path} (${locale})`, async ({ page }) => {
      const errors = await installProbe(page);
      const path = withVariants(localePath(locale, fillParams(surface.path, IDS)), [
        // The wizard seed carries its own grant toggle, so a second one would be ambiguous.
        ...(surface.component === 'OnboardingShellComponent' ? [] : [grant.query]),
        ...dataVariants(surface),
      ]);
      const response = await page.goto(path);
      expect(
        response?.headers()['content-security-policy'] ?? '',
        `the document of ${path} carries the policy`,
      ).toContain("require-trusted-types-for 'script'");

      const ready = READY[surface.component];
      if (ready === undefined) throw new Error(`No readiness contract for ${surface.component}`);
      await expect(page.locator(ready), path).toBeVisible();
      if (REPLACES_HISTORY[surface.component] !== true) {
        expect(new URL(page.url()).pathname, `${path} stayed on its own address`).toBe(
          localePath(locale, fillParams(surface.path, IDS)),
        );
      }
      await assertClean(page, errors, path);
    });
  }

  // The wizard is one component with five server-derived steps, so the resumed states need a seed.
  for (const seed of RESUMED_SEEDS) {
    test(`the policy allows the resumed wizard ${seed} (${locale})`, async ({ page }) => {
      const errors = await installProbe(page);
      const path = withVariants(localePath(locale, fillParams(resumedWizard().path, IDS)), [
        seedQuery(seed),
      ]);
      await page.goto(path);
      await expect(page.locator(WIZARD), path).toBeVisible();
      await assertClean(page, errors, path);
    });
  }

  test(`a sidebar navigation stays inside the policy (${locale})`, async ({ page }) => {
    const errors = await installProbe(page);
    await page.goto(overviewPath(locale));
    await expect(page.locator('#main-content h1')).toBeVisible();
    // A client-side navigation loads another lazy chunk in the document already running, which no
    // fresh page load reproduces.
    const target = page.locator('app-shell-nav nav a:not([aria-current="page"])').first();
    const href = await target.getAttribute('href');
    expect(href).not.toBeNull();
    await target.click();
    await expect.poll(() => new URL(page.url()).pathname).toBe(href);
    await expect(page.locator('#main-content h1')).toBeVisible();
    await assertClean(page, errors, `the sidebar navigation of ${locale}`);
  });

  test(`the probe observes an enforced refusal (${locale})`, async ({ page }) => {
    const errors = await installProbe(page);
    await page.goto(overviewPath(locale));
    await expect(page.locator('#main-content h1')).toBeVisible();
    await assertClean(page, errors, 'the positive control');
    // Negative control: the policy is enforced, not merely sent. A raw string written into a
    // script sink must be refused by Trusted Types and reach the probe; with `bypassCSP` on the
    // write would succeed silently and this step would fail.
    const refused = await page.evaluate(() => {
      try {
        document.createElement('script').textContent = 'void 0';
        return false;
      } catch {
        return true;
      }
    });
    expect(refused, 'Trusted Types refuses a raw script write').toBe(true);
    await expect
      .poll(async () => (await violations(page)).map(({ directive }) => directive))
      .toContain('require-trusted-types-for');
  });
}
