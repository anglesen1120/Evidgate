import { expect, test, type Page } from '@playwright/test';
import audit from '../../../../fixtures/web-api/audit-list/input.json';
import type { FixtureCallRecord } from '../../src/app/core/dev-fixtures/fixture-request-log';
import { RUN_RECORDS } from './run-list.support';
import {
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  SETTINGS_AUDIT_ONLY_GRANTS,
  settingsTabPath,
  withVariants,
} from './support';

type Surface = 'runs' | 'audit';
const ROWS = { runs: RUN_RECORDS, audit: 'app-audit-table [data-row-id]' } as const;

function address(surface: Surface, retry = false): string {
  const path =
    surface === 'runs'
      ? localePath('en', `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`)
      : settingsTabPath('en', 'audit');
  const grant =
    surface === 'runs'
      ? '?fixture.variant.listPermissions=settings-policy-readonly'
      : SETTINGS_AUDIT_ONLY_GRANTS;
  return withVariants(path, [
    grant,
    `?fixture.variant.${surface === 'runs' ? 'listRuns' : 'listAudit'}=paging-33${retry ? '-retry' : ''}`,
  ]);
}

async function requests(page: Page, surface: Surface): Promise<URL[]> {
  const log: readonly FixtureCallRecord[] = await page.evaluate(
    () => window.__evidgateFixtureRequests ?? [],
  );
  return log
    .map((entry) => new URL(entry.url, 'http://fixtures.test'))
    .filter((url) => url.pathname.endsWith(`/${surface}`));
}

async function rowIds(page: Page, surface: Surface): Promise<string[]> {
  return page
    .locator(ROWS[surface])
    .evaluateAll((rows) => rows.map((row) => row.getAttribute('data-row-id') ?? ''));
}

const retainedTables = [
  {
    name: 'catalog',
    path: `/dev/catalog-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`,
    grant: 'settings-policy-readonly',
    rows: 'app-case-list-table [data-row-id]',
  },
  {
    name: 'security events',
    path: `/dev/settings-security-events/orgs/${ORG_ACME}`,
    grant: 'settings-admin',
    rows: 'app-security-events-table [data-row-id]',
  },
  {
    name: 'api tokens',
    // A bookmarked cursor offers a way back to the first page even without a next cursor.
    path: `/dev/settings-api-tokens/orgs/${ORG_ACME}?project=${PROJECT_STOREFRONT}&cursor=bookmarked`,
    grant: 'settings-api-tokens-read',
    rows: 'app-api-token-list [data-api-token-row]',
  },
] as const;

for (const locale of ['en', 'vi'] as const) {
  for (const surface of retainedTables) {
    for (const width of surface.name === 'catalog' ? [390, 1440] : [1440]) {
      test(`${surface.name} ${locale} at ${width}px: retained row stays in place during a delayed read`, async ({
        page,
      }) => {
        await page.setViewportSize({ width, height: width === 390 ? 844 : 1050 });
        await page.goto(
          withVariants(localePath(locale, surface.path), [
            `?fixture.variant.listPermissions=${surface.grant}`,
            '?fixture.latency=1000',
          ]),
        );
        const first = page.locator(surface.rows).first();
        await expect(first).toBeVisible({ timeout: 15000 });
        const position = () =>
          first.evaluate((el) => el.getBoundingClientRect().top + window.scrollY);
        const initialY = await position();
        const content = await first.textContent();
        const pager = page.locator('app-data-table-pager').first();
        if (surface.name === 'security events') {
          await pager.locator('[data-page="next"]').click();
        } else if (surface.name === 'api tokens') {
          await page.locator('[data-api-first]').click();
        } else {
          await pager.getByRole('combobox').click();
          await page.getByRole('option', { name: '50', exact: true }).click();
        }
        const feedback = page.locator('[data-table-progress]');
        await expect(feedback).toBeVisible();
        expect(await first.textContent()).toBe(content);
        expect(Math.abs((await position()) - initialY)).toBeLessThanOrEqual(1);
        if (surface.name === 'catalog')
          await expect(page.locator('[data-catalog-summary]')).toHaveText('');
        await expect(feedback).toHaveCount(0);
        if (surface.name === 'api tokens')
          await expect(page).toHaveURL((url) => !url.searchParams.has('cursor'));
      });
    }
  }
}

for (const locale of ['en', 'vi'] as const) {
  for (const theme of ['light', 'dark'] as const) {
    for (const width of [390, 768, 1440]) {
      test(`runs ${locale} ${theme} at ${width}px: Previous feedback stays beside the pager without shifting controls`, async ({
        page,
      }, testInfo) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 900 });
        const path = address('runs').replace('/en/', `/${locale}/`);
        await page.goto(`${path}&fixture.latency=1000`);
        const rows = page.locator(ROWS.runs);
        await expect(rows).toHaveCount(20);
        const pager = page.locator('app-data-table-pager');
        await pager.locator('[data-page="next"]').click();
        await expect(rows).toHaveCount(13);
        const previous = pager.locator('[data-page="previous"]');
        await previous.scrollIntoViewIfNeeded();
        const before = await previous.boundingBox();
        await previous.click();
        const feedback = pager.getByRole('progressbar');
        await expect(feedback).toBeInViewport();
        await expect(feedback).toHaveAccessibleName(locale === 'en' ? 'Loading…' : 'Đang tải…');
        const after = await previous.boundingBox();
        expect(before).not.toBeNull();
        expect(after).not.toBeNull();
        expect(Math.abs(after!.x - before!.x)).toBeLessThanOrEqual(1);
        expect(Math.abs(after!.y - before!.y)).toBeLessThanOrEqual(1);
        await page.screenshot({ path: testInfo.outputPath('previous-loading.png') });
        await expect(rows).toHaveCount(20);
        await expect(feedback).toHaveCount(0);
      });
    }
  }
}

for (const surface of ['runs', 'audit'] as const) {
  test(`${surface}: twenty then thirteen records, Previous and View preserve the committed page`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(address(surface));
    const rows = page.locator(ROWS[surface]);
    const pager = page.locator('app-data-table-pager');
    const previous = pager.getByRole('button', { name: 'Previous page', exact: true });
    const next = pager.getByRole('button', { name: 'Next page', exact: true });
    await expect(rows).toHaveCount(20);
    const first = await rowIds(page, surface);
    expect(await requests(page, surface)).toHaveLength(1);
    await expect(previous).toHaveAttribute('aria-disabled', 'true');
    await expect(pager.getByRole('combobox', { name: 'Rows per page' })).toContainText('20');
    await next.click();
    await expect(rows).toHaveCount(13);
    const second = await rowIds(page, surface);
    expect(second.some((id) => first.includes(id))).toBe(false);
    await expect(next).toHaveAttribute('aria-disabled', 'true');
    const served = await requests(page, surface);
    expect(served).toHaveLength(2);
    expect(served[1]?.searchParams.get('cursor')).toBeTruthy();
    expect(served[1]?.searchParams.get('limit')).toBe('20');
    const url = page.url();
    const view = rows
      .first()
      .locator(surface === 'runs' ? 'button[aria-haspopup="dialog"]' : '[data-audit-view]');
    await view.click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(view).toBeFocused();
    expect(page.url()).toBe(url);
    expect(await rowIds(page, surface)).toEqual(second);
    expect(await requests(page, surface)).toHaveLength(2);
    await previous.click();
    await expect.poll(() => rowIds(page, surface)).toEqual(first);
    const restored = (await requests(page, surface)).at(-1);
    expect(restored?.searchParams.has('cursor')).toBe(false);
    expect(restored?.searchParams.has('offset')).toBe(false);
    await expect(previous).toHaveAttribute('aria-disabled', 'true');
  });

  test(`${surface}: pending page retains records and uses a static progress bar with reduced motion`, async ({
    page,
  }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`${address(surface)}&fixture.latency=1000`);
    await expect(page.locator(ROWS[surface])).toHaveCount(20);
    const first = await rowIds(page, surface);
    const next = page.getByRole('button', { name: 'Next page', exact: true });
    const controlPositions = () =>
      page.locator('app-data-table-pager nav').evaluate((nav) => {
        const origin = nav.getBoundingClientRect();
        return [...nav.querySelectorAll('button')].map((button) => {
          const { left, top } = button.getBoundingClientRect();
          return [left - origin.left, top - origin.top];
        });
      });
    const idlePositions = await controlPositions();
    await next.dblclick();
    const table = page.locator('app-data-table');
    await expect(table.getByRole('table')).toHaveAttribute('aria-busy', 'true');
    const bar = table.locator('[data-table-progress]');
    const feedback = page
      .locator('app-data-table-pager')
      .getByRole('progressbar', { name: 'Loading…', exact: true });
    await expect(bar).toBeVisible();
    await expect(feedback).toBeInViewport();
    expect(await controlPositions()).toEqual(idlePositions);
    await expect(page.getByRole('progressbar')).toHaveCount(1);
    await expect(bar).toHaveAttribute('aria-hidden', 'true');
    expect(
      await bar.evaluate((element) => getComputedStyle(element, '::after').animationName),
    ).toBe('none');
    expect(
      await feedback
        .locator('.page-spinner')
        .evaluate((element) => getComputedStyle(element).animationName),
    ).toBe('none');
    expect(await rowIds(page, surface)).toEqual(first);
    await expect(table.locator('app-loading-pattern').getByRole('status')).toHaveText('');
    await expect(page.locator(ROWS[surface])).toHaveCount(13);
    await expect(bar).toHaveCount(0);
    expect(await requests(page, surface)).toHaveLength(2);

    const second = await rowIds(page, surface);
    const previous = page.getByRole('button', { name: 'Previous page', exact: true });
    await previous.click();
    await expect(table.getByRole('table')).toHaveAttribute('aria-busy', 'true');
    await expect(bar).toBeVisible();
    await expect(feedback).toBeInViewport();
    expect(await rowIds(page, surface)).toEqual(second);
    await expect.poll(() => rowIds(page, surface)).toEqual(first);
    await expect(bar).toHaveCount(0);
    await expect(feedback).toHaveCount(0);

    await page.getByRole('combobox', { name: 'Rows per page' }).click();
    await page.getByRole('option', { name: '50', exact: true }).click();
    await expect(table.getByRole('table')).toHaveAttribute('aria-busy', 'true');
    await expect(bar).toBeVisible();
    await expect(feedback).toBeInViewport();
    expect(await rowIds(page, surface)).toEqual(first);
    await expect(page.locator(ROWS[surface])).toHaveCount(33);
    await expect(bar).toHaveCount(0);
    await expect(feedback).toHaveCount(0);
  });

  test(`${surface}: changing page size resets the cursor and supports fifty and one hundred`, async ({
    page,
  }) => {
    await page.goto(address(surface));
    const rows = page.locator(ROWS[surface]);
    const pager = page.locator('app-data-table-pager');
    await expect(rows).toHaveCount(20);
    await pager.getByRole('button', { name: 'Next page', exact: true }).click();
    await expect(rows).toHaveCount(13);
    for (const size of [50, 100, 20]) {
      await pager.getByRole('combobox', { name: 'Rows per page' }).click();
      await page.getByRole('option', { name: String(size), exact: true }).click();
      await expect(rows).toHaveCount(size === 20 ? 20 : 33);
      const request = (await requests(page, surface)).at(-1);
      expect(request?.searchParams.has('cursor')).toBe(false);
      expect(request?.searchParams.get('limit')).toBe(String(size));
      await expect(
        pager.getByRole('button', { name: 'Previous page', exact: true }),
      ).toHaveAttribute('aria-disabled', 'true');
    }
  });

  test(`${surface}: applying and clearing a filter reset the cursor`, async ({ page }) => {
    await page.goto(address(surface));
    const rows = page.locator(ROWS[surface]);
    const pager = page.locator('app-data-table-pager');
    await expect(rows).toHaveCount(20);
    await pager.getByRole('button', { name: 'Next page', exact: true }).click();
    await expect(rows).toHaveCount(13);
    if (surface === 'runs') {
      await page.locator('#run-ref').fill('feature/batch-000');
    } else {
      const seed = audit.body.items[0];
      if (!seed) throw new Error('An audit filter needs a seed action');
      await page.locator('#audit-action').fill(seed.action);
    }
    const before = (await requests(page, surface)).length;
    await page.locator(`[data-filter-search]`).click();
    await expect.poll(async () => (await requests(page, surface)).length).toBe(before + 1);
    const filteredCount = surface === 'runs' ? 1 : 6;
    await expect(rows).toHaveCount(filteredCount);
    const filtered = (await requests(page, surface)).at(-1);
    expect(filtered?.searchParams.has('cursor')).toBe(false);
    expect(filtered?.searchParams.get(surface === 'runs' ? 'ref_name' : 'action')).toBeTruthy();
    await expect(pager.getByText(`Showing 1–${filteredCount}`, { exact: true })).toBeVisible();
    await expect(pager.getByRole('navigation')).toHaveCount(0);
    await expect(pager.locator('button, [role="combobox"]')).toHaveCount(0);
    await page.locator(`[data-filter-clear]`).click();
    await expect(rows).toHaveCount(20);
    await expect.poll(async () => (await requests(page, surface)).length).toBe(before + 2);
    const cleared = (await requests(page, surface)).at(-1);
    expect(cleared?.searchParams.has('cursor')).toBe(false);
    expect(cleared?.searchParams.has(surface === 'runs' ? 'ref_name' : 'action')).toBe(false);
  });

  test(`${surface}: a later-page failure preserves successful rows and retries the failed cursor`, async ({
    page,
  }) => {
    await page.goto(address(surface, true));
    const rows = page.locator(ROWS[surface]);
    const pager = page.locator('app-data-table-pager');
    await expect(rows).toHaveCount(20);
    const first = await rowIds(page, surface);
    await pager.getByRole('button', { name: 'Next page', exact: true }).click();
    const alert = page.locator(`[data-${surface === 'runs' ? 'run' : 'audit'}-paging-error]`);
    await expect(alert).toBeVisible();
    await expect(pager.getByRole('progressbar')).toHaveCount(0);
    await expect(pager.locator('nav')).toHaveAttribute('aria-busy', 'false');
    await expect(alert).toContainText('dependency_busy');
    expect(await rowIds(page, surface)).toEqual(first);
    await expect(pager.getByRole('status')).toHaveText('Page 1');
    const failed = (await requests(page, surface)).at(-1);
    expect(failed?.searchParams.get('cursor')).toBeTruthy();
    await alert.getByRole('button').click();
    await expect(rows).toHaveCount(13);
    await expect(alert).toHaveCount(0);
    const retried = (await requests(page, surface)).at(-1);
    expect(retried?.search).toBe(failed?.search);
    await expect(pager.getByRole('status')).toHaveText('Page 2');
    expect((await rowIds(page, surface)).some((id) => first.includes(id))).toBe(false);
  });
}
