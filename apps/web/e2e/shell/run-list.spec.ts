// 09.3.1-T1: real Chromium, fixture corpus, both locale bundles and theme token sets.
// Cursor fixtures are selected by listRuns' catalog cursor key, never an offset.
//
// The `runs` entry is phase G2, so this G1 build does not match the product address
// (`/orgs/:orgId/projects/:projectId/runs`) and the wildcard renders the not-found pattern. Only
// the dev-routes file-replacement seam mounts the real page, at
// `/dev/run-list/orgs/:orgId/projects/:projectId`, under the normal shell, auth, org-scope and
// project-scope gates plus the entry's own `run:read`. Nothing else is relaxed: the page-local
// RunListStore and the fixture-backed RunDataService are the real ones. The page reads `projectId`
// from the ancestor param maps and navigates its filter query relative to its own route, so the
// URL assertions below hold on this address unchanged.
import { expect, test, type Page } from '@playwright/test';
import runList from '../../../../fixtures/web-api/run-list/input.json';
import runList250 from '../../../../fixtures/web-api/run-list-250/input.json';
import runListEmpty from '../../../../fixtures/web-api/run-list-empty/input.json';
import runListPage1 from '../../../../fixtures/web-api/run-list-page-1/input.json';
import runListPage2 from '../../../../fixtures/web-api/run-list-page-2/input.json';
import runListUnavailable from '../../../../fixtures/web-api/run-list-unavailable/input.json';
import {
  expectNoA11yViolations,
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariant,
  withVariants,
  type ShellLocale,
} from './support';

const VARIANTS = {
  runs: '?fixture.variant.listRuns=runs',
  empty: '?fixture.variant.listRuns=empty',
  large: '?fixture.variant.listRuns=250-rows',
  pages: '?fixture.variant.listRuns=page-1',
  noMatch: '?fixture.variant.listRuns=nomatch-pages',
  forbidden: '?fixture.variant.listRuns=forbidden',
  unavailable: '?fixture.variant.listRuns=unavailable',
} as const;
const ROWS = 'app-run-list-table [data-run-row]';
/**
 * `?fixture.variant.listPermissions=settings-policy-readonly` — the corpus grant set that holds
 * `run:read` (with `project:read` and `organization:read`). The default `permissions-list` is the
 * G1 set and grants no run code, so without this variant the `run:read` gate on the review address
 * sends every case to `/forbidden`.
 */
const RUN_READ_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';
/** `/dev/run-list/orgs/:orgId/projects/:projectId` — the review address, without a query. */
function reviewPath(locale: ShellLocale) {
  return localePath(locale, `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`);
}
/**
 * The review address of the real run list, prepared for a case: the grant variant above, then the
 * case's own variants. The G2 product `runs` segment this build does not match (file comment).
 */
function path(locale: ShellLocale) {
  return withVariant(reviewPath(locale), RUN_READ_GRANT);
}
async function ready(page: Page) {
  await expect(page.locator('app-run-list-page')).toHaveAttribute('data-run-list-status', 'ready');
}

for (const locale of LOCALES) {
  test(`stored dimensions, pending, isolated refs and scope labels (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariants(path(locale), [VARIANTS.runs]));
    await ready(page);
    await expect(page.locator(ROWS)).toHaveCount(runList.body.items.length);
    await expect(page.locator('app-run-list-page h1')).toHaveCount(1);
    await expect(page.locator('app-run-list-table [role="columnheader"]')).toHaveCount(7);
    await expect(page.locator(`${ROWS} [data-run-evidence] [data-tone="neutral"]`)).toHaveCount(
      runList.body.items.length,
    );
    await expect(page.locator(`${ROWS} [data-run-evidence] [data-tone="success"]`)).toHaveCount(0);
    await expect(page.locator('app-outcome-pill')).toHaveCount(0);
    for (const row of runList.body.items) {
      const rendered = page.locator(`${ROWS}[data-run-id="${row.run_id}"]`);
      await expect(rendered.locator('[data-run-decision]')).toContainText(
        row.decision ?? 'pending',
      );
      await expect(rendered.locator('time')).toHaveAttribute('datetime', row.created_at);
      await expect(rendered.locator('[data-run-unverified]')).toHaveCount(
        row.unverified_ref ? 1 : 0,
      );
      if (row.rerun_label)
        await expect(rendered.locator('[data-run-rerun]')).toContainText(row.rerun_label);
      if (row.reprocess_label)
        await expect(rendered.locator('[data-run-reprocess]')).toContainText(row.reprocess_label);
      if (row.ref.name?.includes('\u202e')) {
        await expect(rendered.locator('bdi')).toContainText('⟨U+202E⟩');
        expect(await rendered.locator('bdi').textContent()).not.toContain('\u202e');
      }
    }
    await expect(page.locator('[data-run-loaded-count]')).toContainText(
      String(runList.body.items.length),
    );
    await expect(
      page.locator(
        '[name="completeness"], [name="outcome"], [name="pr_number"], [name="head_sha"]',
      ),
    ).toHaveCount(0);
  });

  test(`filter URL survives reload and back/forward (${locale})`, async ({ page }) => {
    await page.goto(withVariants(path(locale), [VARIANTS.runs]));
    await ready(page);
    await page.locator('#run-state').selectOption('running');
    await page.locator('[data-run-apply]').click();
    await expect(page).toHaveURL(/state=running/);
    // The filter navigation is relative to the page's own route, so the review address survives it
    // and the reload below re-matches the same route.
    expect(new URL(page.url()).pathname).toBe(reviewPath(locale));
    await expect(page.locator(ROWS)).toHaveCount(
      runList.body.items.filter((row) => row.state === 'running').length,
    );
    await page.reload();
    await ready(page);
    await expect(page.locator('#run-state')).toHaveValue('running');
    await page.locator('#run-state').selectOption('complete');
    await page.locator('#run-decision').selectOption('FAIL');
    await page.locator('#run-scope').selectOption('rerun');
    await page.locator('[data-run-apply]').click();
    await expect(page).toHaveURL(/decision=FAIL/);
    await expect(page.locator(ROWS)).toHaveCount(1);
    await page.goBack();
    await ready(page);
    await expect(page.locator('#run-state')).toHaveValue('running');
    await expect(page.locator('#run-decision')).toHaveValue('');
    await expect(page.locator('#run-scope')).toHaveValue('');
    await page.goForward();
    await ready(page);
    await expect(page.locator('#run-state')).toHaveValue('complete');
    await expect(page.locator('#run-decision')).toHaveValue('FAIL');
    await expect(page.locator('#run-scope')).toHaveValue('rerun');
    await page.locator('[data-run-clear]').click();
    await expect(page.locator(ROWS)).toHaveCount(runList.body.items.length);
    const query = new URL(page.url()).searchParams;
    expect(query.get('fixture.variant.listRuns')).toBe('runs');
    for (const key of ['state', 'decision', 'scope', 'ref']) expect(query.has(key)).toBe(false);
    await page.locator('#run-ref').fill('feature/việt');
    await page.locator('[data-run-apply]').click();
    await expect.poll(() => new URL(page.url()).searchParams.get('ref')).toBe('feature/việt');
    await page.reload();
    await ready(page);
    await expect(page.locator('#run-ref')).toHaveValue('feature/việt');
  });

  test(`empty and loaded-only filtered-empty are distinct (${locale})`, async ({ page }) => {
    expect(runListEmpty.body.items).toHaveLength(0);
    await page.goto(withVariants(path(locale), [VARIANTS.empty]));
    await ready(page);
    await expect(page.locator('app-empty-pattern')).toBeVisible();
    const trulyEmpty = await page.locator('app-empty-pattern').textContent();
    await page.goto(withVariants(path(locale), [VARIANTS.noMatch, '?state=complete']));
    await ready(page);
    await expect(page.locator('app-empty-pattern')).toBeVisible();
    expect(await page.locator('app-empty-pattern').textContent()).not.toBe(trulyEmpty);
    await expect(page.locator('[data-run-loaded-count]')).toContainText('2');
    await expect(page.locator(ROWS)).toHaveCount(0);
    await page.locator('[data-run-clear]').click();
    await expect(page.locator(ROWS)).toHaveCount(2);
  });

  test(`250 runs use a bounded CDK window that reaches the last record (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariants(path(locale), [VARIANTS.large]));
    await ready(page);
    const viewport = page.locator('app-run-list-table cdk-virtual-scroll-viewport');
    await expect(viewport).toBeVisible();
    await expect(page.locator('app-run-list-table [role="table"]')).toHaveAttribute(
      'aria-rowcount',
      '251',
    );
    await expect.poll(() => page.locator(ROWS).count()).toBeGreaterThan(0);
    expect(await page.locator(ROWS).count()).toBeLessThan(100);
    await viewport.focus();
    await expect(viewport).toBeFocused();
    await viewport.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    const last = runList250.body.items[runList250.body.items.length - 1]!;
    await expect(page.locator(`${ROWS}[data-run-id="${last.run_id}"]`)).toBeVisible();
    expect(await page.locator(ROWS).count()).toBeLessThan(100);
  });
}

test('cursor pages preserve server ordering and append without offset pagination', async ({
  page,
}) => {
  await page.goto(withVariants(path('en'), [VARIANTS.pages]));
  await ready(page);
  // Top-up may already have followed the cursor because the first fixture is deliberately short.
  const allRows = [...runListPage1.body.items, ...runListPage2.body.items];
  await expect(page.locator(ROWS)).toHaveCount(allRows.length);
  expect(
    await page
      .locator(ROWS)
      .evaluateAll((nodes) => nodes.map((node) => node.getAttribute('data-run-id'))),
  ).toEqual(allRows.map((row) => row.run_id));
  await expect(page.locator('[data-run-load-more]')).toHaveCount(0);
  expect(new URL(page.url()).searchParams.has('offset')).toBe(false);
});

test('a server refusal renders the shared forbidden pattern and verbatim code', async ({
  page,
}) => {
  await page.goto(withVariants(path('en'), [VARIANTS.forbidden]));
  await expect(page.locator('app-run-list-page')).toHaveAttribute('data-run-list-status', 'error');
  await expect(page.locator('app-forbidden-pattern')).toBeVisible();
  await expect(page.locator(ROWS)).toHaveCount(0);
  await expect(page.locator('app-run-list-page [role="alert"] code')).not.toBeEmpty();
});

test('a 503 preserves its message and Retry-After before enabling an explicit retry', async ({
  page,
}) => {
  await page.clock.install();
  await page.goto(withVariants(path('en'), [VARIANTS.unavailable]));
  await expect(page.locator('app-run-list-page')).toHaveAttribute('data-run-list-status', 'error');
  await expect(page.locator('app-run-list-page')).toContainText(
    runListUnavailable.body.error.message,
  );
  await expect(page.locator('app-retry-after')).toBeVisible();
  const retry = page.locator('app-retry-after button');
  await expect(retry).toBeDisabled();
  await page.clock.fastForward(5_000);
  await expect(retry).toBeEnabled();
  await expect(page.locator(ROWS)).toHaveCount(0);
});

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test(`WCAG, keyboard and mobile containment (${locale}, ${theme})`, async ({ page }) => {
      await seedStoredTheme(page, theme);
      await page.setViewportSize({ width: 360, height: 800 });
      await page.goto(withVariants(path(locale), [VARIANTS.runs]));
      await ready(page);
      await page.locator('#run-state').focus();
      await page.keyboard.press('Tab');
      await expect(page.locator('#run-decision')).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(page.locator('#run-scope')).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(page.locator('#run-ref')).toBeFocused();
      for (const selector of [
        '#run-state',
        '#run-decision',
        '#run-scope',
        '#run-ref',
        '[data-run-apply]',
        '[data-run-clear]',
      ]) {
        const box = await page.locator(selector).boundingBox();
        expect(box?.height).toBeGreaterThanOrEqual(44);
      }
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      await expectNoA11yViolations(page, `run list ${locale}/${theme}/360px`);
    });
  }
}
