import { expect, test, type Page } from '@playwright/test';
import audit from '../../../../fixtures/web-api/audit-list/input.json';
import type { FixtureCallRecord } from '../../src/app/core/dev-fixtures/fixture-request-log';
import { openRunFilters, RUN_RECORDS } from './run-list.support';
import {
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
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

for (const surface of ['runs', 'audit'] as const) {
  test(`${surface}: twenty then thirteen records, Previous and View preserve the committed page`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(address(surface));
    const rows = page.locator(ROWS[surface]);
    const pager = page.locator('app-data-table-pager');
    const previous = pager.getByRole('button', { name: 'Previous records', exact: true });
    const next = pager.getByRole('button', { name: 'Next records', exact: true });
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
    const next = page.getByRole('button', { name: 'Next records', exact: true });
    await next.dblclick();
    const table = page.locator('app-data-table');
    await expect(table.getByRole('table')).toHaveAttribute('aria-busy', 'true');
    const bar = table.locator('[data-table-progress]');
    await expect(bar).toBeVisible();
    expect(
      await bar.evaluate((element) => getComputedStyle(element, '::after').animationName),
    ).toBe('none');
    expect(await rowIds(page, surface)).toEqual(first);
    await expect(table.locator('app-loading-pattern').getByRole('status')).toHaveText('Loading…');
    await expect(page.locator(ROWS[surface])).toHaveCount(13);
    await expect(bar).toHaveCount(0);
    expect(await requests(page, surface)).toHaveLength(2);
  });

  test(`${surface}: changing page size resets the cursor and supports fifty and one hundred`, async ({
    page,
  }) => {
    await page.goto(address(surface));
    const rows = page.locator(ROWS[surface]);
    const pager = page.locator('app-data-table-pager');
    await expect(rows).toHaveCount(20);
    await pager.getByRole('button', { name: 'Next records', exact: true }).click();
    await expect(rows).toHaveCount(13);
    for (const size of [50, 100, 20]) {
      await pager.getByRole('combobox', { name: 'Rows per page' }).click();
      await page.getByRole('option', { name: String(size), exact: true }).click();
      await expect(rows).toHaveCount(size === 20 ? 20 : 33);
      const request = (await requests(page, surface)).at(-1);
      expect(request?.searchParams.has('cursor')).toBe(false);
      expect(request?.searchParams.get('limit')).toBe(String(size));
      await expect(
        pager.getByRole('button', { name: 'Previous records', exact: true }),
      ).toHaveAttribute('aria-disabled', 'true');
    }
  });

  test(`${surface}: applying and clearing a filter reset the cursor`, async ({ page }) => {
    await page.goto(address(surface));
    const rows = page.locator(ROWS[surface]);
    const pager = page.locator('app-data-table-pager');
    await expect(rows).toHaveCount(20);
    await pager.getByRole('button', { name: 'Next records', exact: true }).click();
    await expect(rows).toHaveCount(13);
    if (surface === 'runs') {
      await openRunFilters(page);
      await page.locator('#run-ref').fill('feature/batch-000');
    } else {
      const seed = audit.body.items[0];
      if (!seed) throw new Error('An audit filter needs a seed action');
      await page.locator('#audit-action').fill(seed.action);
    }
    const before = (await requests(page, surface)).length;
    await page.locator(`[data-${surface === 'runs' ? 'run' : 'audit'}-apply]`).click();
    await expect.poll(async () => (await requests(page, surface)).length).toBe(before + 1);
    await expect(rows).toHaveCount(surface === 'runs' ? 1 : 6);
    const filtered = (await requests(page, surface)).at(-1);
    expect(filtered?.searchParams.has('cursor')).toBe(false);
    expect(filtered?.searchParams.get(surface === 'runs' ? 'ref_name' : 'action')).toBeTruthy();
    await expect(
      pager.getByRole('button', { name: 'Previous records', exact: true }),
    ).toHaveAttribute('aria-disabled', 'true');
    await page.locator(`[data-${surface === 'runs' ? 'run' : 'audit'}-clear]`).click();
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
    await pager.getByRole('button', { name: 'Next records', exact: true }).click();
    const alert = page.locator(`[data-${surface === 'runs' ? 'run' : 'audit'}-paging-error]`);
    await expect(alert).toBeVisible();
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
