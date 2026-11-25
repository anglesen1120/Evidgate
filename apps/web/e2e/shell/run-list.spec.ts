// The fixture route mounts the real project-scoped list behind the normal permission checks.
import { expect, test, type Page } from '@playwright/test';
import runList from '../../../../fixtures/web-api/run-list/input.json';
import runList250 from '../../../../fixtures/web-api/run-list-250/input.json';
import runListEmpty from '../../../../fixtures/web-api/run-list-empty/input.json';
import runListPage1 from '../../../../fixtures/web-api/run-list-page-1/input.json';
import runListPage2 from '../../../../fixtures/web-api/run-list-page-2/input.json';
import runListUnavailable from '../../../../fixtures/web-api/run-list-unavailable/input.json';
import type { FixtureCallRecord } from '../../src/app/core/dev-fixtures/fixture-request-log';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  FILTER_LABEL,
  chooseFilter,
  expectCenteredRunIdentity,
  expectSingleLineScope,
  openRunFilters,
  RUN_RECORDS,
} from './run-list.support';
import {
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
const ROWS = RUN_RECORDS;
/** The review route requires the fixture grant set that includes run:read. */
const RUN_READ_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';

/** `/dev/run-list/orgs/:orgId/projects/:projectId` — the review address, without a query. */
function reviewPath(locale: ShellLocale) {
  return localePath(locale, `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`);
}
/** Keep filter navigation on the real list's development review address. */
function path(locale: ShellLocale) {
  return withVariant(reviewPath(locale), RUN_READ_GRANT);
}
async function ready(page: Page) {
  await expect(page.locator('app-run-list-page')).toHaveAttribute('data-run-list-status', 'ready');
}

for (const locale of LOCALES) {
  test(`stored dimensions, decisions and not evaluated runs, isolated refs and scope labels (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(withVariants(path(locale), [VARIANTS.runs]));
    await ready(page);
    await expect(page.locator(ROWS)).toHaveCount(runList.body.items.length);
    await expect(page.locator('app-run-list-page h1')).toHaveCount(1);
    await expect(page.locator('app-run-list-table [role="columnheader"]')).toHaveCount(6);
    await expect(page.locator(`${ROWS} td`).first()).toHaveCSS('vertical-align', 'middle');
    await expectSingleLineScope(page);
    await expect(page.locator('app-outcome-pill')).toHaveCount(0);
    for (const row of runList.body.items) {
      const rendered = page.locator(`${ROWS}[data-row-id="${row.run_id}"]`);
      const evidenceLabels =
        locale === 'vi'
          ? { complete: 'Đủ bằng chứng', incomplete: 'Thiếu bằng chứng', unavailable: 'Chưa có' }
          : { complete: 'Complete', incomplete: 'Incomplete', unavailable: 'Unavailable' };
      const evidence = row.completeness;
      await expect(rendered.locator('[data-run-evidence] [role="img"]')).toHaveText(
        evidence === 'complete' || evidence === 'incomplete'
          ? evidenceLabels[evidence]
          : evidenceLabels.unavailable,
      );
      // An omitted decision is unavailable; only an explicit null means not evaluated.
      const decision = 'decision' in row ? row.decision : undefined;
      await expect(rendered.locator('[data-run-decision]')).toContainText(
        decision === undefined
          ? locale === 'vi'
            ? 'Không khả dụng'
            : 'Unavailable'
          : (decision ?? (locale === 'vi' ? 'Chờ đánh giá' : 'Pending')),
      );
      await expect(rendered.locator('time')).toHaveAttribute('datetime', row.created_at);
      await expect(rendered.locator('[data-run-unverified]')).toHaveCount(
        row.unverified_ref ? 1 : 0,
      );
      if (row.rerun_label)
        await expect(rendered.locator('[data-run-rerun]')).toContainText(row.rerun_label);
      if (row.reprocess_label)
        await expect(rendered.locator('[data-run-reprocess]')).toContainText(row.reprocess_label);
      // Count label wrappers separately from nested preview/screen-reader isolation.
      // Pin the shared identifier's own isolation as well as the surrounding scope.
      const scopeCount = Number(Boolean(row.rerun_label)) + Number(Boolean(row.reprocess_label));
      await expect(rendered.locator('.scope-cell > span > bdi')).toHaveCount(scopeCount);
      await expect(rendered.locator('.scope-cell app-data-table-value > .value > bdi')).toHaveCount(
        scopeCount,
      );
      if (row.ref.name?.includes('\u202e')) {
        await expect(rendered.locator('a bdi')).toHaveCount(1);
        await expect(rendered.locator('a bdi')).toContainText('⟨U+202E⟩');
        expect(await rendered.locator('a bdi').textContent()).not.toContain('\u202e');
      }
    }
    // Nothing is filtered here, so the pager range is the only count in the footer.
    await expect(page.locator('[data-run-loaded-count]')).toHaveCount(0);
    await expect(page.locator('app-data-table-pager .page-range')).toHaveText(
      new RegExp(`1–${runList.body.items.length}$`),
    );
    await expect(
      page.locator(
        '[name="completeness"], [name="outcome"], [name="pr_number"], [name="head_sha"]',
      ),
    ).toHaveCount(0);
  });

  test(`not evaluated filter round-trips through reload and clearing (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariants(path(locale), [VARIANTS.runs, '?decision=pending']));
    await ready(page);
    const expected = runList.body.items.filter((row) => row.decision === null);
    expect(expected.length).toBeGreaterThan(0);
    await expect(page.locator('[data-run-quick="pending"]')).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await openRunFilters(page);
    await expect(page.locator('#run-decision')).toContainText(FILTER_LABEL.pending[locale]);
    await page.keyboard.press('Escape');
    await expect(page.locator(ROWS)).toHaveCount(expected.length);
    await expect(page.locator(`${ROWS} [data-run-decision] [data-tone="pending"]`)).toHaveCount(
      expected.length,
    );
    await page.reload();
    await ready(page);
    await expect(page.locator('[data-run-quick="pending"]')).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await openRunFilters(page);
    await expect(page.locator('#run-decision')).toContainText(FILTER_LABEL.pending[locale]);
    await page.keyboard.press('Escape');
    await expect(page.locator(ROWS)).toHaveCount(expected.length);
    await page.locator('[data-filter-clear]').click();
    await expect(page.locator(ROWS)).toHaveCount(runList.body.items.length);
    expect(new URL(page.url()).searchParams.has('decision')).toBe(false);
  });

  test(`filter URL survives reload and back/forward (${locale})`, async ({ page }) => {
    await page.goto(withVariants(path(locale), [VARIANTS.runs]));
    await ready(page);
    await chooseFilter(page, '#run-state', FILTER_LABEL.running[locale]);
    await page.locator('[data-filter-apply]').click();
    await expect(page).toHaveURL(/state=running/);
    // The filter navigation is relative to the page's own route, so the review address survives it
    // and the reload below re-matches the same route.
    expect(new URL(page.url()).pathname).toBe(reviewPath(locale));
    await expect(page.locator(ROWS)).toHaveCount(
      runList.body.items.filter((row) => row.state === 'running').length,
    );
    await page.reload();
    await ready(page);
    await openRunFilters(page);
    await expect(page.locator('#run-state')).toContainText(FILTER_LABEL.running[locale]);
    await chooseFilter(page, '#run-state', FILTER_LABEL.complete[locale]);
    await chooseFilter(page, '#run-decision', FILTER_LABEL.fail[locale]);
    await chooseFilter(page, '#run-scope', FILTER_LABEL.rerun[locale]);
    await page.locator('[data-filter-apply]').click();
    await expect(page).toHaveURL(/decision=fail/);
    await expect(page.locator(ROWS)).toHaveCount(1);
    const requests: readonly FixtureCallRecord[] = await page.evaluate(
      () => window.__evidgateFixtureRequests ?? [],
    );
    const listRequest = requests
      .filter((request) => new URL(request.url, 'http://127.0.0.1').pathname.endsWith('/runs'))
      .at(-1);
    expect(listRequest).toBeDefined();
    const wireFilters = new URL(listRequest?.url ?? '', 'http://127.0.0.1').searchParams;
    expect(wireFilters.get('decision')).toBe('fail');
    expect(wireFilters.get('state')).toBe('complete');
    expect(wireFilters.has('rerun_scope')).toBe(false);
    await page.goBack();
    await ready(page);
    await openRunFilters(page);
    await expect(page.locator('#run-state')).toContainText(FILTER_LABEL.running[locale]);
    await expect(page.locator('#run-decision')).toContainText(FILTER_LABEL.any[locale]);
    await expect(page.locator('#run-scope')).toContainText(FILTER_LABEL.any[locale]);
    await page.goForward();
    await ready(page);
    await openRunFilters(page);
    await expect(page.locator('#run-state')).toContainText(FILTER_LABEL.complete[locale]);
    await expect(page.locator('#run-decision')).toContainText(FILTER_LABEL.fail[locale]);
    await expect(page.locator('#run-scope')).toContainText(FILTER_LABEL.rerun[locale]);
    await page.keyboard.press('Escape');
    await page.locator('[data-filter-clear]').click();
    await expect(page.locator(ROWS)).toHaveCount(runList.body.items.length);
    const query = new URL(page.url()).searchParams;
    expect(query.get('fixture.variant.listRuns')).toBe('runs');
    for (const key of ['state', 'decision', 'scope', 'ref']) expect(query.has(key)).toBe(false);
    await page.locator('#run-ref').fill('feature/việt');
    await page.locator('[data-filter-search]').click();
    await expect.poll(() => new URL(page.url()).searchParams.get('ref')).toBe('feature/việt');
    await page.reload();
    await ready(page);
    await expect(page.locator('#run-ref')).toHaveValue('feature/việt');
  });

  test(`empty and page-local Scope filtered-empty are distinct (${locale})`, async ({ page }) => {
    expect(runListEmpty.body.items).toHaveLength(0);
    await page.goto(withVariants(path(locale), [VARIANTS.empty]));
    await ready(page);
    await expect(page.locator('app-empty-pattern')).toBeVisible();
    const trulyEmpty = await page.locator('app-empty-pattern').textContent();
    await page.goto(withVariants(path(locale), [VARIANTS.noMatch, '?scope=rerun']));
    await ready(page);
    await expect(page.locator('app-empty-pattern')).toBeVisible();
    expect(await page.locator('app-empty-pattern').textContent()).not.toBe(trulyEmpty);
    await expect(page.locator('[data-run-loaded-count]')).toContainText('2');
    // Scope narrows these loaded rows, not the server dataset.
    await expect(page.locator('[data-run-loaded-count]')).toHaveAttribute(
      'data-run-loaded-filtered',
      'true',
    );
    await expect(page.locator(ROWS)).toHaveCount(0);
    await page.locator('[data-filter-clear]').click();
    await expect(page.locator(ROWS)).toHaveCount(2);
  });

  test(`250 runs use server cursor pages that reach the last record (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(withVariants(path(locale), [VARIANTS.large]));
    await ready(page);
    await expect(page.locator('app-run-list-table cdk-virtual-scroll-viewport')).toHaveCount(0);
    const expectedIds = runList250.body.items.map((row) => row.run_id);
    const ids = () =>
      page.locator(ROWS).evaluateAll((rows) => rows.map((row) => row.getAttribute('data-row-id')));
    await expect(page.locator(ROWS)).toHaveCount(20);
    expect(await ids()).toEqual(expectedIds.slice(0, 20));
    const pager = page.locator('app-data-table-pager');
    await pager.getByRole('combobox').click();
    await page.getByRole('option', { name: '100', exact: true }).click();
    await expect(page.locator(ROWS)).toHaveCount(100);
    const previous = pager.getByRole('button', {
      name: locale === 'vi' ? 'Trang trước' : 'Previous page',
      exact: true,
    });
    const next = pager.getByRole('button', {
      name: locale === 'vi' ? 'Trang sau' : 'Next page',
      exact: true,
    });
    await next.focus();
    await page.keyboard.press('Enter');
    await expect.poll(ids).toEqual(expectedIds.slice(100, 200));
    await expect(next).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.locator(ROWS)).toHaveCount(50);
    expect(await ids()).toEqual(expectedIds.slice(200));
    const last = runList250.body.items.at(-1);
    if (!last) throw new Error('The large fixture must contain a last record');
    const lastRecord = page.locator(`${ROWS}[data-row-id="${last.run_id}"]`);
    const view = lastRecord.getByRole('button', {
      name: `${locale === 'vi' ? 'Xem' : 'View'} ${last.run_id}`,
      exact: true,
    });
    await expect(lastRecord.locator('[data-run-row] a')).toHaveAttribute(
      'href',
      new RegExp(last.run_id),
    );
    const address = page.url();
    await view.click();
    await expect(page.getByRole('dialog')).toContainText(last.run_id);
    await page.keyboard.press('Escape');
    await expect(view).toBeFocused();
    await expect(lastRecord).toBeInViewport();
    expect(page.url()).toBe(address);
    expect(await ids()).toEqual(expectedIds.slice(200));
    await expect(next).toHaveAttribute('aria-disabled', 'true');
    await previous.click();
    await expect.poll(ids).toEqual(expectedIds.slice(100, 200));
  });

  for (const mode of ['runs', 'large'] as const) {
    test(`run identifiers and column facts remain contained across tablet widths (${locale}, ${mode})`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: 768, height: 800 });
      await page.goto(withVariants(path(locale), [VARIANTS[mode]]));
      await ready(page);
      const row = page.locator(ROWS).first();
      await expect(row).toBeVisible();
      await page.evaluate(() => document.fonts.ready);
      const host = page.locator('app-run-list-table app-data-table');
      for (const width of [320, 360, 390, 768, 800, 900, 1280, 1440]) {
        await page.setViewportSize({ width, height: 800 });
        // ResizeObserver measures the container; the two languages need not fit at the same width.
        await page.evaluate(
          () =>
            new Promise<void>((resolve) =>
              requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
            ),
        );
        const narrow = await host.locator('.eg-table-narrow').isVisible();
        if (width <= 390) expect(narrow).toBe(true);
        if (width >= 1280) expect(narrow).toBe(false);
        await expect(row).toHaveJSProperty('tagName', narrow ? 'LI' : 'TR');
        if (narrow || width >= 1280) {
          await expect(
            host.locator(
              ':is(app-decision-chip, app-evidence-badge) [hlmBadge].spartan-badge--clipped',
            ),
          ).toHaveCount(0);
        }
        expect(
          await host
            .locator('.scroll')
            .evaluate((element) => element.scrollWidth - element.clientWidth),
          `${width}px table has no hidden horizontal facts`,
        ).toBeLessThanOrEqual(1);
        if (narrow) {
          await expect(row.locator('dt')).toHaveCount(6);
          await expect(row.locator('dd')).toHaveCount(6);
          const fields = await row.locator('dd').evaluateAll((entries) =>
            entries.map((entry) => ({
              x: entry.getBoundingClientRect().x,
              overflow: entry.scrollWidth - entry.clientWidth,
            })),
          );
          expect(new Set(fields.map((field) => Math.round(field.x))).size).toBe(1);
          for (const field of fields) expect(field.overflow).toBeLessThanOrEqual(1);
        } else {
          await expectCenteredRunIdentity(row);
          await expect(host.getByRole('columnheader')).toHaveCount(6);
          await expect(row.locator('td')).toHaveCount(6);
        }
        const id = await row.getAttribute('data-row-id');
        if (!id) throw new Error('Missing run identity');
        const link = row.getByRole('link');
        await expect(link).toHaveAccessibleName(new RegExp(`\\(${id}\\)$`));
        expect(await link.getAttribute('href')).toContain(id);
        const overflowingFacts = await host
          .locator('[data-row-id] :is(td, dd)')
          .evaluateAll((facts) =>
            facts
              .filter((fact) => fact.scrollWidth > fact.clientWidth + 1)
              .map((fact) => fact.textContent?.trim()),
          );
        expect(overflowingFacts, `${width}px visible facts stay within their cells`).toEqual([]);
        expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
          width,
        );
      }
      await page.setViewportSize({ width: 767, height: 800 });
      await expect(row.locator('dt')).toHaveCount(6);
      expect(await row.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(
        true,
      );
    });
  }
}

test('cursor pages preserve server ordering and replace rather than append rows', async ({
  page,
}) => {
  await page.goto(withVariants(path('en'), [VARIANTS.pages]));
  await ready(page);
  const ids = () =>
    page
      .locator(ROWS)
      .evaluateAll((nodes) => nodes.map((node) => node.getAttribute('data-row-id')));
  await expect(page.locator(ROWS)).toHaveCount(runListPage1.body.items.length);
  expect(await ids()).toEqual(runListPage1.body.items.map((row) => row.run_id));
  const pager = page.locator('app-data-table-pager');
  await pager.getByRole('button', { name: 'Next page', exact: true }).click();
  await expect.poll(ids).toEqual(runListPage2.body.items.map((row) => row.run_id));
  await expect(pager.getByRole('button', { name: 'Next page', exact: true })).toHaveAttribute(
    'aria-disabled',
    'true',
  );
  await pager.getByRole('button', { name: 'Previous page', exact: true }).click();
  await expect.poll(ids).toEqual(runListPage1.body.items.map((row) => row.run_id));
  expect(new URL(page.url()).searchParams.has('offset')).toBe(false);
});

test('a server refusal renders the shared forbidden pattern as the only error surface', async ({
  page,
}) => {
  await page.goto(withVariants(path('en'), [VARIANTS.forbidden]));
  await expect(page.locator('app-run-list-page')).toHaveAttribute('data-run-list-status', 'error');
  await expect(page.locator('app-forbidden-pattern')).toBeVisible();
  await expect(page.locator(ROWS)).toHaveCount(0);
  // The shared pattern is the page's one error surface; the page adds no second banner.
  await expect(page.locator('app-run-list-page .banner.failure')).toHaveCount(0);
  await expect(page.locator('app-run-list-page [role="alert"]')).toHaveCount(0);
});

test('a 503 with Retry-After renders the shared retry pattern once, with its code', async ({
  page,
}) => {
  await page.clock.install();
  await page.goto(withVariants(path('en'), [VARIANTS.unavailable]));
  await expect(page.locator('app-run-list-page')).toHaveAttribute('data-run-list-status', 'error');
  await expect(page.locator('app-retry-after')).toBeVisible();
  // The error code and request id survive in the shared panel; the page renders no duplicate.
  await expect(page.locator('app-retry-after')).toContainText(runListUnavailable.body.error.code);
  await expect(page.locator('app-retry-after')).toContainText(
    runListUnavailable.body.error.request_id,
  );
  await expect(page.locator('app-run-list-page .banner.failure')).toHaveCount(0);
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
      await page.setViewportSize({ width: 390, height: 800 });
      await page.goto(withVariants(path(locale), [VARIANTS.runs]));
      await ready(page);
      await expect(page.locator('[data-filter-clear]')).toHaveCount(0);
      await page.locator('[data-run-quick="fail"]').click();
      await expect(page).toHaveURL(/decision=fail/);
      await expect(page.locator('[data-run-quick="fail"]')).toHaveAttribute('aria-pressed', 'true');
      await openRunFilters(page);
      await page.locator('#run-state').focus();
      await page.keyboard.press('Tab');
      await expect(page.locator('button[aria-controls="run-decision-hint"]')).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(page.locator('#run-decision')).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(page.locator('button[aria-controls="run-filter-hint"]')).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(page.locator('#run-scope')).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(page.locator('[data-filter-apply]')).toBeFocused();
      for (const selector of [
        '#run-state',
        '#run-decision',
        '#run-scope',
        '#run-ref',
        '[data-run-quick="all"]',
        '[data-filter-search]',
        '[data-filter-apply]',
        '[data-filter-clear]',
      ]) {
        const box = await page.locator(selector).boundingBox();
        expect(box?.height).toBeGreaterThanOrEqual(44);
      }
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      const table = page.locator('app-run-list-table .eg-table-narrow');
      expect(await table.evaluate((element) => getComputedStyle(element).minInlineSize)).toBe(
        '0px',
      );
      const firstRow = page.locator(ROWS).first();
      await expect(firstRow.locator('dt')).toHaveCount(6);
      expect(await firstRow.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(
        true,
      );
      expect(
        await firstRow
          .locator('dt')
          .first()
          .evaluate((element) => getComputedStyle(element).display),
      ).not.toBe('none');
      // The option list is a portaled overlay that exists only while the combobox is open; the
      // keyboard opens it and Tab dismisses it without committing.
      await page.locator('#run-decision').focus();
      await page.keyboard.press('ArrowDown');
      await expect(
        page.getByRole('option', { name: FILTER_LABEL.pending[locale], exact: true }),
      ).toBeVisible();
      await page.keyboard.press('Tab');
      await expect(page.locator('#run-decision')).toHaveAttribute('aria-expanded', 'false');
      await expectNoA11yViolations(page, `run list ${locale}/${theme}/390px`);
      await page.setViewportSize({ width: 1440, height: 900 });
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      await expectNoA11yViolations(page, `run list ${locale}/${theme}/1440px`);
    });
  }
}
