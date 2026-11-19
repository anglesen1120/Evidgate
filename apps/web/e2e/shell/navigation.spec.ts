// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC4; CR-D-2 navigation rule): what the sidebar shows and
// which lazy chunk a deep link pulls — both observed in real Chromium against the fixtures build.
//
// The grant sets are the corpus's. `scripts/check-no-fixture-code.mjs` skips the protocol members
// of a corpus document (`headers`, `operation_id`, `variant`, `code`, `resource`, `action`), so a
// catalogue row may name any real permission even though the production bundle carries the
// navigation registry's own permission literals. `permissions-list-limited` leaves
// `organization:read` out on purpose, so the limited session has a G1 route (`settings`) that
// refuses. Two states are reachable:
//
//   - DEFAULT (`permissions-list`, 16 catalogue codes): `authoring:read`, `authoring:generate`,
//     `authoring:write`, `audit:read` and the non-G1 codes → the design group's three entry rows
//     plus the audit log;
//   - LIMITED (`permissions-list-limited`): `audit:read`, `authoring:read`, `membership:read`,
//     `project:read`, `role:read` → `authoring` plus the rows those five codes gate.
//
// Both are asserted exactly (a set, not "contains"), so a permission filter that starts leaking an
// ungranted row fails here as loudly as one that hides a granted row.
import { expect, test, type Browser } from '@playwright/test';

import {
  fixturesBaseUrl,
  LIMITED_GRANTS,
  localePath,
  LOCALES,
  navSegments,
  ORG_ACME,
  PROJECT_STOREFRONT,
  withVariant,
} from './support';

/** `/orgs/:orgId/projects/:projectId/authoring` — reachable under BOTH corpus grant sets. */
const PROJECT_AUTHORING_PATH = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/authoring`;

/** The default corpus session: the design group's three rows, AI settings and the audit log. */
const DEFAULT_SESSION_SEGMENTS = [
  'ai-settings',
  'audit',
  'authoring',
  'generation',
  'pending-changes',
];

/** The trimmed corpus session: `authoring` survives, `overview`/`ai-settings`/`members` arrive. */
const LIMITED_SESSION_SEGMENTS = ['ai-settings', 'audit', 'authoring', 'members', 'overview'];

/**
 * The URL segment of every row of a phase the build has not reached (G2–G4,
 * `ACTIVE_PHASE` = G1). None may ever appear: an unreached phase is hidden, never disabled.
 */
const LATER_PHASE_SEGMENTS = [
  'scripts',
  'catalog',
  'mapping',
  'runs',
  'failures',
  'repairs',
  'knowledge',
  'availability',
  'studio',
  'suites',
  'quality',
  'releases',
  'policies',
  'deploy',
  'secrets',
  'security',
  'support',
  'agents',
  'integrations',
  'tokens',
];

test.describe('sidebar visibility (AC4)', () => {
  test('the default catalogue session shows exactly the rows its codes grant', async ({ page }) => {
    await page.goto(localePath('en', PROJECT_AUTHORING_PATH));
    await expect(page.locator('app-shell-nav nav a').first()).toBeVisible();

    const visible = await navSegments(page);
    expect(visible).toEqual(DEFAULT_SESSION_SEGMENTS);
    expect(
      visible.filter((segment) => LATER_PHASE_SEGMENTS.includes(segment)),
      'no later-phase row is ever rendered',
    ).toEqual([]);
    // The sidebar is a landmark with its own name, and each row is a link to its own route.
    await expect(page.getByRole('navigation')).toHaveCount(1);
    await expect(
      page.locator(`app-shell-nav nav a[href$="/projects/${PROJECT_STOREFRONT}/authoring"]`),
    ).toHaveCount(1);
  });

  test('the limited grant set hides the rows it does not grant and shows the ones it does', async ({
    page,
  }) => {
    await page.goto(withVariant(localePath('en', PROJECT_AUTHORING_PATH), LIMITED_GRANTS));
    await expect(page.locator('app-shell-nav nav a').first()).toBeVisible();

    const visible = await navSegments(page);
    expect(visible).toEqual(LIMITED_SESSION_SEGMENTS);
    // The rows the default session granted and this one does not are GONE, not disabled.
    expect(visible).not.toContain('generation');
    expect(visible).not.toContain('pending-changes');
    // And the rows only these five codes gate are now present.
    expect(visible).toContain('overview');
    expect(visible).toContain('ai-settings');
    expect(visible).toContain('members');
    expect(visible.filter((segment) => LATER_PHASE_SEGMENTS.includes(segment))).toEqual([]);
  });
});

/**
 * Every `.js` file the browser actually fetched for one deep link, in its own context so nothing
 * is served from a previous load's memory or HTTP cache.
 */
async function chunkSetFor(browser: Browser, url: string): Promise<string[]> {
  const context = await browser.newContext({ baseURL: fixturesBaseUrl() });
  const page = await context.newPage();
  const requested = new Set<string>();
  page.on('response', (response) => {
    const path = new URL(response.url()).pathname;
    if (path.endsWith('.js')) requested.add(path);
  });
  try {
    await page.goto(url);
    await expect(page.locator('#main-content h1')).toBeVisible();
    await page.waitForLoadState('networkidle');
    return [...requested].sort();
  } finally {
    await context.close();
  }
}

/** The chunks of `set` that none of `others` fetched: that route's own lazy chunk(s). */
function onlyIn(set: readonly string[], others: readonly (readonly string[])[]): string[] {
  return set.filter((chunk) => others.every((other) => !other.includes(chunk)));
}

test.describe('lazy route boundaries (AC4)', () => {
  test('a deep link loads its own lazy chunk and no sibling area chunk', async ({ browser }) => {
    const overview = withVariant(
      localePath('en', `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`),
      LIMITED_GRANTS,
    );
    const authoring = withVariant(localePath('en', PROJECT_AUTHORING_PATH), LIMITED_GRANTS);
    // `audit` is org-scope and lives in the `settings` area chunk, so it is the third distinct
    // feature boundary reachable with a corpus grant (`audit:read`).
    const audit = withVariant(localePath('en', `/orgs/${ORG_ACME}/audit`), LIMITED_GRANTS);

    const [overviewChunks, authoringChunks, auditChunks] = await Promise.all([
      chunkSetFor(browser, overview),
      chunkSetFor(browser, authoring),
      chunkSetFor(browser, audit),
    ]);

    // The shared baseline: `main`, `polyfills` and the modules `index.html` preloads.
    const shared = overviewChunks.filter(
      (chunk) => authoringChunks.includes(chunk) && auditChunks.includes(chunk),
    );
    const overviewOnly = onlyIn(overviewChunks, [authoringChunks, auditChunks]);
    const authoringOnly = onlyIn(authoringChunks, [overviewChunks, auditChunks]);
    const auditOnly = onlyIn(auditChunks, [overviewChunks, authoringChunks]);

    // Each route loaded at least one chunk nobody else loaded: its own feature chunk exists and
    // the router really did defer it.
    expect(overviewOnly.length, `overview-only chunks: ${overviewOnly.join(', ')}`).toBeGreaterThan(
      0,
    );
    expect(
      authoringOnly.length,
      `authoring-only chunks: ${authoringOnly.join(', ')}`,
    ).toBeGreaterThan(0);
    expect(auditOnly.length, `audit-only chunks: ${auditOnly.join(', ')}`).toBeGreaterThan(0);

    // Those three assertions are also the disjointness claim this case makes, in the form that can
    // fail: all three sets are non-empty, so no route holds another area's WHOLE chunk set, and a
    // route that pulled a sibling's area in would empty that sibling's set. A single chunk shared by
    // two areas is deliberately NOT asserted away — the bundler legally emits a shared chunk for
    // code two lazy routes both import, so that assertion would fail on a legitimate build; the
    // count bound below is what catches the coarse regression instead.

    // The shell and its framework are shared by definition, so the baseline must be non-empty —
    // a baseline of nothing would make the `*Only` assertions above trivially satisfiable.
    expect(shared.length).toBeGreaterThan(0);

    // A deep link loads the shared baseline plus its own area, not the whole app. The bound is
    // derived from the three runs themselves (F20 removed the previous `shared.length + 4`
    // arithmetic, an incidental count that had to be raised by hand): each route must have fetched
    // a PROPER SUBSET of the chunks the three addresses together can serve. Eager bundling or a
    // preload-everything regression makes all three sets equal — the union — and fails here.
    const routes = [
      ['overview', overviewChunks],
      ['authoring', authoringChunks],
      ['audit', auditChunks],
    ] as const;
    const servedByAll = [...new Set(routes.flatMap(([, chunks]) => chunks))];
    for (const [name, chunks] of routes) {
      expect(chunks.length, `${name} fetched chunks: ${chunks.join(', ')}`).toBeLessThan(
        servedByAll.length,
      );
    }
  });
});

test.describe('locale bundles', () => {
  for (const locale of LOCALES) {
    test(`the ${locale} bundle serves the shell from its own base href`, async ({ page }) => {
      await page.goto(withVariant(localePath(locale, PROJECT_AUTHORING_PATH), LIMITED_GRANTS));
      await expect(page.locator('html')).toHaveAttribute('lang', locale);
      await expect(page.locator('app-shell')).toHaveCount(1);
      await expect(page.locator('app-shell-nav nav a').first()).toBeVisible();
      // Every route link stays inside the locale bundle: a link that left it would change locale,
      // not just page.
      const hrefs = await page
        .locator('app-shell-nav nav a')
        .evaluateAll((links) => links.map((link) => link.getAttribute('href') ?? ''));
      expect(hrefs.length).toBeGreaterThan(0);
      expect(hrefs.filter((href) => !href.startsWith(`/${locale}/`))).toEqual([]);
    });
  }
});
