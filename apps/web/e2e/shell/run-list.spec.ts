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
import type { FixtureCallRecord } from '../../src/app/core/dev-fixtures/fixture-request-log';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { openRunFilters, RUN_RECORDS } from './run-list.support';
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
/**
 * `?fixture.variant.listPermissions=settings-policy-readonly` — the corpus grant set that holds
 * `run:read` (with `project:read` and `organization:read`). The default `permissions-list` is the
 * G1 set and grants no run code, so without this variant the `run:read` gate on the review address
 * sends every case to `/forbidden`.
 */
const RUN_READ_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';
/** The filters are Helm comboboxes now: the trigger shows the selected label, not a value. */
const FILTER_LABEL = {
  any: { en: 'All', vi: 'Tất cả' },
  running: { en: 'Running', vi: 'Đang chạy' },
  complete: { en: 'Complete', vi: 'Hoàn tất' },
  pending: { en: 'Not evaluated', vi: 'Chưa đánh giá' },
  fail: { en: 'FAIL', vi: 'FAIL' },
  rerun: { en: 'Rerun', vi: 'Chạy lại' },
} satisfies Record<string, Record<ShellLocale, string>>;

/** A combobox commits on an option click; it exposes no form value to read back. */
async function chooseFilter(page: Page, trigger: string, label: string): Promise<void> {
  await page.locator(trigger).click();
  await page.getByRole('option', { name: label, exact: true }).click();
}

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
  test(`stored dimensions, decisions and not evaluated runs, isolated refs and scope labels (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(withVariants(path(locale), [VARIANTS.runs]));
    await ready(page);
    await expect(page.locator(ROWS)).toHaveCount(runList.body.items.length);
    await expect(page.locator('app-run-list-page h1')).toHaveCount(1);
    await expect(page.locator('app-run-list-table [role="columnheader"]')).toHaveCount(7);
    await expect(page.locator(`${ROWS} [data-run-evidence] .status-pill--neutral`)).toHaveCount(
      runList.body.items.length,
    );
    await expect(page.locator(`${ROWS} [data-run-evidence] .status-pill--success`)).toHaveCount(0);
    await expect(page.locator('app-outcome-pill')).toHaveCount(0);
    for (const row of runList.body.items) {
      const rendered = page.locator(`${ROWS}[data-row-id="${row.run_id}"]`);
      // An omitted decision is unavailable; only an explicit null means not evaluated.
      const decision = 'decision' in row ? row.decision : undefined;
      await expect(rendered.locator('[data-run-decision]')).toContainText(
        decision === undefined
          ? locale === 'vi'
            ? 'Không khả dụng'
            : 'Unavailable'
          : (decision ?? (locale === 'vi' ? 'Chưa đánh giá' : 'Not evaluated')),
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
        await expect(rendered.locator('.ref-cell > bdi')).toHaveCount(1);
        await expect(rendered.locator('.ref-cell > bdi')).toContainText('⟨U+202E⟩');
        expect(await rendered.locator('.ref-cell > bdi').textContent()).not.toContain('\u202e');
        await expect(rendered.locator('.ref-cell app-data-table-value > .value > bdi')).toHaveCount(
          1,
        );
      }
    }
    await expect(page.locator('[data-run-loaded-count]')).toContainText(
      String(runList.body.items.length),
    );
    // Nothing is filtered here, so the count is the neutral one, not "filtered from".
    await expect(page.locator('[data-run-loaded-count]')).toHaveAttribute(
      'data-run-loaded-filtered',
      'false',
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
    await expect(page.locator('#run-decision')).toContainText(FILTER_LABEL.pending[locale]);
    await expect(page.locator(ROWS)).toHaveCount(expected.length);
    await expect(page.locator(`${ROWS} [data-run-decision] [data-tone="pending"]`)).toHaveCount(
      expected.length,
    );
    await page.reload();
    await ready(page);
    await expect(page.locator('#run-decision')).toContainText(FILTER_LABEL.pending[locale]);
    await expect(page.locator(ROWS)).toHaveCount(expected.length);
    await page.locator('[data-run-clear]').click();
    await expect(page.locator(ROWS)).toHaveCount(runList.body.items.length);
    expect(new URL(page.url()).searchParams.has('decision')).toBe(false);
  });

  test(`filter URL survives reload and back/forward (${locale})`, async ({ page }) => {
    await page.goto(withVariants(path(locale), [VARIANTS.runs]));
    await ready(page);
    await chooseFilter(page, '#run-state', FILTER_LABEL.running[locale]);
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
    await expect(page.locator('#run-state')).toContainText(FILTER_LABEL.running[locale]);
    await chooseFilter(page, '#run-state', FILTER_LABEL.complete[locale]);
    await chooseFilter(page, '#run-decision', FILTER_LABEL.fail[locale]);
    await chooseFilter(page, '#run-scope', FILTER_LABEL.rerun[locale]);
    await page.locator('[data-run-apply]').click();
    await expect(page).toHaveURL(/decision=FAIL/);
    await expect(page.locator(ROWS)).toHaveCount(1);
    const requests: readonly FixtureCallRecord[] = await page.evaluate(
      () => window.__evidgateFixtureRequests ?? [],
    );
    const listRequest = requests
      .filter((request) => new URL(request.url, 'http://127.0.0.1').pathname.endsWith('/runs'))
      .at(-1);
    expect(listRequest).toBeDefined();
    expect(new URL(listRequest?.url ?? '', 'http://127.0.0.1').searchParams.get('decision')).toBe(
      'fail',
    );
    await page.goBack();
    await ready(page);
    await expect(page.locator('#run-state')).toContainText(FILTER_LABEL.running[locale]);
    await expect(page.locator('#run-decision')).toContainText(FILTER_LABEL.any[locale]);
    await expect(page.locator('#run-scope')).toContainText(FILTER_LABEL.any[locale]);
    await page.goForward();
    await ready(page);
    await expect(page.locator('#run-state')).toContainText(FILTER_LABEL.complete[locale]);
    await expect(page.locator('#run-decision')).toContainText(FILTER_LABEL.fail[locale]);
    await expect(page.locator('#run-scope')).toContainText(FILTER_LABEL.rerun[locale]);
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
    // A filtered view says "filtered from" — the neutral count is for an unfiltered list only.
    await expect(page.locator('[data-run-loaded-count]')).toHaveAttribute(
      'data-run-loaded-filtered',
      'true',
    );
    await expect(page.locator(ROWS)).toHaveCount(0);
    await page.locator('[data-run-clear]').click();
    await expect(page.locator(ROWS)).toHaveCount(2);
  });

  test(`250 runs use bounded loaded-record pages that reach the last record (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(withVariants(path(locale), [VARIANTS.large]));
    await ready(page);
    await expect(page.locator('app-run-list-table cdk-virtual-scroll-viewport')).toHaveCount(0);
    await expect(page.locator('app-run-list-table [role="table"]')).toHaveAttribute(
      'aria-rowcount',
      '251',
    );
    await expect(page.locator(ROWS)).toHaveCount(200);
    const expectedIds = runList250.body.items.map((row) => row.run_id);
    const ids = () =>
      page.locator(ROWS).evaluateAll((rows) => rows.map((row) => row.getAttribute('data-row-id')));
    expect(await ids()).toEqual(expectedIds.slice(0, 200));
    const pager = page.locator('app-run-list-table nav');
    await expect(pager.getByRole('button')).toHaveCount(2);
    const next = pager.getByRole('button').last();
    await next.focus();
    await expect(next).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.locator(ROWS)).toHaveCount(50);
    expect(await ids()).toEqual(expectedIds.slice(200));
    await expect(pager.getByRole('button').first()).toBeFocused();
    const last = runList250.body.items[runList250.body.items.length - 1]!;
    const lastRecord = page.locator(`${ROWS}[data-row-id="${last.run_id}"]`);
    await lastRecord.locator('a').focus();
    await expect(lastRecord).toBeInViewport();
    await expect(next).toBeDisabled();
    await pager.getByRole('button').first().focus();
    await page.keyboard.press('Enter');
    await expect(page.locator(ROWS)).toHaveCount(200);
    await expect(next).toBeFocused();
    expect(await ids()).toEqual(expectedIds.slice(0, 200));
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
      for (const width of [768, 800, 900, 1280, 1440]) {
        await page.setViewportSize({ width, height: 800 });
        const host = page.locator('app-run-list-table app-data-table');
        await host.evaluate(
          () =>
            new Promise<void>((resolve) => {
              requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
            }),
        );
        const narrow = (await host.locator('.eg-table-narrow').count()) > 0;
        expect(
          narrow,
          `${width}px uses ${width < 1280 ? 'readable records' : 'desktop columns'}`,
        ).toBe(width < 1280);
        if (width >= 1280) {
          await expect(
            host.locator('app-decision-chip [hlmBadge].spartan-badge--clipped'),
          ).toHaveCount(0);
        }
        expect(
          await host
            .locator('.scroll')
            .evaluate((element) => element.scrollWidth - element.clientWidth),
          `${width}px table has no hidden horizontal facts`,
        ).toBeLessThanOrEqual(1);
        await expect(row).toHaveJSProperty('tagName', narrow ? 'LI' : 'TR');
        if (narrow) {
          await expect(row.locator('dt')).toHaveCount(7);
          await expect(row.locator('dd')).toHaveCount(7);
          const fields = await row.locator('dd').evaluateAll((entries) =>
            entries.map((entry) => ({
              x: entry.getBoundingClientRect().x,
              overflow: entry.scrollWidth - entry.clientWidth,
            })),
          );
          expect(new Set(fields.map((field) => Math.round(field.x))).size).toBe(1);
          for (const field of fields) expect(field.overflow).toBeLessThanOrEqual(1);
          await expect(row.locator('[data-run-row] a')).toHaveText(
            (await row.getAttribute('data-row-id')) ?? '',
          );
        } else {
          const geometry = await row.evaluate((element) => {
            const cell = element.querySelector<HTMLElement>('.identifier');
            const id = cell?.querySelector('a');
            const header = element.closest('[role="table"]')?.querySelector('thead tr');
            if (!cell || !id || !header) throw new Error('Run table geometry is unavailable');
            const tableCell = cell.closest('td');
            if (!tableCell) throw new Error('The identifier has no containing table cell');
            const range = document.createRange();
            range.selectNodeContents(id);
            const text = range.getBoundingClientRect();
            const bounds = cell.getBoundingClientRect();
            const cellBounds = tableCell.getBoundingClientRect();
            const headings = [...header.children].map((entry) => entry.getBoundingClientRect());
            return {
              lines: range.getClientRects().length,
              fits:
                text.left >= bounds.left - 1 &&
                text.right <= bounds.right + 1 &&
                text.left >= cellBounds.left - 1 &&
                text.right <= cellBounds.right + 1,
              alignment: [...element.children].map((entry, index) => {
                const body = entry.getBoundingClientRect();
                const heading = headings[index];
                if (!heading) throw new Error('Run column heading is missing');
                return Math.max(Math.abs(body.x - heading.x), Math.abs(body.width - heading.width));
              }),
            };
          });
          expect(geometry.lines, `${width}px identifier line count`).toBe(1);
          expect(geometry.fits, `${width}px identifier fits inside its cell`).toBe(true);
          expect(geometry.alignment).toHaveLength(7);
          for (const difference of geometry.alignment) expect(difference).toBeLessThanOrEqual(1);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
          width,
        );
      }
      await page.setViewportSize({ width: 767, height: 800 });
      await expect(row.locator('dt')).toHaveCount(7);
      expect(await row.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(
        true,
      );
    });
  }
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
      .evaluateAll((nodes) => nodes.map((node) => node.getAttribute('data-row-id'))),
  ).toEqual(allRows.map((row) => row.run_id));
  await expect(page.locator('[data-run-load-more]')).toHaveCount(0);
  // Unfiltered, the note is a plain count, never the "filtered from" phrasing.
  await expect(page.locator('[data-run-loaded-count]')).toContainText(
    `Loaded ${allRows.length} runs`,
  );
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
      await openRunFilters(page);
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
      const table = page.locator('app-run-list-table .eg-table-narrow');
      expect(await table.evaluate((element) => getComputedStyle(element).minInlineSize)).toBe(
        '0px',
      );
      const firstRow = page.locator(ROWS).first();
      await expect(firstRow.locator('dt')).toHaveCount(7);
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
