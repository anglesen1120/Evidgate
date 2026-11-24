import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import type {} from '../../src/app/core/dev-fixtures/fixture-request-log';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
} from './support';

const PAGE = 'app-authoring-page';
const TREE = 'app-authoring-tree-pane';
const IDS = [101, 102, 103, 104, 105, 106, 107].map(
  (suffix) => `018f0000-0000-7000-8000-000000000${suffix}`,
);
const ROOT = IDS[0] ?? '';
const LEAF = IDS[6] ?? '';
const ACCOUNT = '018f0000-0000-7000-8000-000000000108';

async function tabTo(page: Page, target: Locator): Promise<void> {
  for (let step = 0; step < 120; step++) {
    if (await target.evaluate((element) => element === document.activeElement)) return;
    await page.keyboard.press('Tab');
  }
  await expect(target).toBeFocused();
}

for (const locale of LOCALES) {
  const path = localePath(
    locale,
    `/dev/authoring-tree/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`,
  );
  const base = withVariants(path, ['?fixture.variant.listPermissions=settings-policy-readonly']);
  const labels =
    locale === 'vi'
      ? {
          title: 'Cây chức năng',
          all: 'Các nút gốc của dự án',
          moreRoots: 'Tải thêm nút gốc',
          moreChildren: 'Tải thêm nút con',
          pending: 'Chưa có số thay đổi đang chờ',
        }
      : {
          title: 'Feature tree',
          all: 'Project roots',
          moreRoots: 'Load more root nodes',
          moreChildren: 'Load more child nodes',
          pending: 'Pending change count unavailable',
        };

  test(`tree native Tab and arrow model never selects on focus (${locale})`, async ({ page }) => {
    await page.goto(base);
    const root = page.locator(`[data-disclosure="${ROOT}"]`);
    const scope = page.locator(`[data-scope="${ROOT}"]`);
    const account = page.locator(`[data-scope="${ACCOUNT}"]`);
    await tabTo(page, root);
    await expect(root).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('Tab');
    await expect(scope).toBeFocused();
    expect(new URL(page.url()).searchParams.has('node_id')).toBe(false);
    await root.focus();
    await page.keyboard.press('ArrowRight');
    await expect(root).toHaveAttribute('aria-expanded', 'true');
    await expect(page.locator(`[data-disclosure="${IDS[1]}"]`)).toBeVisible();
    await expect(root).toBeFocused();
    await page.keyboard.press('ArrowRight');
    const child = page.locator(`[data-disclosure="${IDS[1]}"]`);
    await expect(child).toBeFocused();
    await page.keyboard.press('ArrowLeft');
    await expect(root).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(child).toBeFocused();
    await page.keyboard.press('End');
    await expect(account).toBeFocused();
    await page.keyboard.press('Home');
    await expect(root).toBeFocused();
    await page.keyboard.press('t');
    await expect(account).toBeFocused();
    await page.keyboard.press('t');
    await expect(root).toBeFocused();
    await page.keyboard.press('Space');
    await expect(root).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('Enter');
    await expect(root).toHaveAttribute('aria-expanded', 'true');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(new RegExp(`node_id=${ROOT}`));
    await expect(scope).toHaveAttribute('aria-current', 'location');
    await expect(page.locator('[role="tree"], [role="treeitem"]')).toHaveCount(0);
  });

  test(`tree restores root-first paths and node URL across reload/back/forward (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariants(base, [`?node_id=${LEAF}&cursor=old-case-cursor`]));
    const crumbs = page.locator('[data-authoring-breadcrumb] li');
    await expect(crumbs).toHaveCount(8);
    await expect(crumbs.first()).toHaveText(labels.all);
    await expect(crumbs.nth(1)).toContainText('Thanh toán');
    await expect(crumbs.last()).toContainText('Tổng kết kiểm thử');
    await expect(page.locator(`[data-disclosure="${ROOT}"]`)).toHaveAttribute(
      'aria-expanded',
      'false',
    );
    await expect(page.locator(`[data-node-id="${LEAF}"]`)).toHaveCount(0);
    await crumbs.nth(1).getByRole('button').click();
    await expect(page).toHaveURL(new RegExp(`node_id=${ROOT}`));
    expect(new URL(page.url()).searchParams.has('cursor')).toBe(false);
    await expect(crumbs).toHaveCount(2);
    await page.goBack();
    await expect(page).toHaveURL(new RegExp(`node_id=${LEAF}`));
    await expect(crumbs).toHaveCount(8);
    await page.goForward();
    await expect(crumbs).toHaveCount(2);
    await page.reload();
    await expect(crumbs).toHaveCount(2);
    await expect(crumbs.last()).toContainText('Thanh toán');
    await crumbs.first().getByRole('button').click();
    await expect(crumbs).toHaveCount(1);
    expect(new URL(page.url()).searchParams.has('node_id')).toBe(false);
  });

  for (const theme of THEMES)
    for (const width of [360, 1280]) {
      test(`tree counts, deep wrapped titles, scoped paging and axe (${locale}, ${theme}, ${width})`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base);
        await expect(page.locator(`${PAGE} h1`)).toHaveText(labels.title);
        const root = page.locator(`[data-node-id="${ROOT}"]`);
        await expect(root.locator('dl')).toHaveCount(0);
        const details = root.locator('[data-node-details]');
        await root.locator('[data-scope]').focus();
        await page.keyboard.press('Tab');
        await expect(details).toBeFocused();
        await page.keyboard.press('Enter');
        const dialog = page.getByRole('dialog');
        await expect(dialog.getByRole('heading')).toBeFocused();
        await expect(dialog.locator('dd').nth(0)).toHaveText('2');
        await expect(dialog.locator('dd').nth(1)).toHaveText('2');
        await expect(dialog.locator('dd').nth(2)).toContainText(labels.pending);
        await expect(dialog.locator('app-copyable-identifier code')).toHaveText(ROOT);
        await page.keyboard.press('Escape');
        await expect(dialog).toHaveCount(0);
        await expect(details).toBeFocused();
        const disclosure = await root.locator('[data-disclosure]').boundingBox();
        if (!disclosure) throw new Error('The root disclosure is not visible.');
        expect(Math.min(disclosure.width, disclosure.height)).toBeGreaterThanOrEqual(24);
        await page.getByRole('button', { name: labels.moreRoots, exact: true }).click();
        await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(3);
        for (let level = 0; level < 6; level++) {
          await page.locator(`[data-disclosure="${IDS[level]}"]`).click();
          await expect(page.locator(`[data-node-id="${IDS[level + 1]}"]`)).toBeVisible();
        }
        await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(9);
        await page.getByRole('button', { name: labels.moreChildren, exact: true }).click();
        await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(10);
        const depth4 = await page.locator(`[data-node-id="${IDS[3]}"]`).boundingBox();
        const depth7 = await page.locator(`[data-node-id="${LEAF}"]`).boundingBox();
        if (!depth4 || !depth7) throw new Error('The deep hierarchy is not visible.');
        expect(Math.abs(depth4.x - depth7.x)).toBeLessThanOrEqual(1);
        const deepRow = page.locator(`[data-node-id="${IDS[3]}"]`);
        const scope = deepRow.locator('[data-scope]');
        const title = scope.locator('bdi');
        const fullTitle = await title.innerText();
        const titleBox = await title.boundingBox();
        const scopeBox = await scope.boundingBox();
        const info = deepRow.locator('[data-node-details]');
        const infoBox = await info.boundingBox();
        if (!scopeBox || !titleBox || !infoBox) throw new Error('Missing deep node controls.');
        expect(scopeBox.height).toBeGreaterThanOrEqual(24);
        expect(titleBox.width).toBeGreaterThanOrEqual(64);
        expect(titleBox.height).toBeGreaterThan(24);
        expect(titleBox.height).toBeLessThanOrEqual(
          2 * (await title.evaluate((el) => parseFloat(getComputedStyle(el).lineHeight))) + 1,
        );
        expect(Math.min(infoBox.width, infoBox.height)).toBeGreaterThanOrEqual(24);
        await info.click();
        await expect(dialog.getByText(fullTitle, { exact: true })).toBeVisible();
        await page.keyboard.press('Escape');
        await expect(info).toBeFocused();
        await expect(page.locator('[data-authoring-cases]')).toBeVisible();
        expect(await page.locator('[data-authoring-cases]').getAttribute('aria-busy')).toBeNull();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
        await expectNoA11yViolations(page, PAGE);
      });
    }

  test(`tree returns focus before collapsing a focused descendant (${locale})`, async ({
    page,
  }) => {
    await page.goto(base);
    const root = page.locator(`[data-disclosure="${ROOT}"]`);
    await root.click();
    const child = page.locator(`[data-disclosure="${IDS[1]}"]`);
    await child.focus();
    // A dispatched activation does not move focus first, unlike a pointer click.
    await root.dispatchEvent('click');
    await expect(root).toBeFocused();
    await expect(root).toHaveAttribute('aria-expanded', 'false');
    await expect(child).toHaveCount(0);
  });

  test(`empty project offers import without table controls (${locale})`, async ({ page }) => {
    await page.goto(
      withVariants(base, [
        '?fixture.variant.listAuthoringTree=empty&fixture.variant.listManualCases=empty',
      ]),
    );
    const blank = page.locator('[data-authoring-project-empty]');
    await expect(blank).toBeVisible();
    await expect(page.locator(PAGE + ' app-empty-pattern')).toHaveCount(1);
    await expect(
      page.locator('[data-case-filters], [data-case-loaded], #authoring-case-page-size'),
    ).toHaveCount(0);
    await blank.getByRole('button').click();
    await expect(page).toHaveURL(/\/authoring\/import-export(?:[?#]|$)/);
  });

  test(`tree loading, empty, unavailable, denied and missing states leave the case region alone (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariants(base, ['?fixture.variant.listAuthoringTree=loading']));
    await expect(page.locator(`${PAGE} app-loading-pattern`).first()).toBeVisible();
    await expect(page.locator('[data-authoring-cases]')).toBeVisible();
    await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(2);
    for (const [variant, selector] of [
      ['empty', 'app-empty-pattern'],
      ['error', 'app-error-pattern'],
      ['forbidden', 'app-forbidden-pattern'],
      ['not-found', 'app-not-found-pattern'],
      ['rate-limited', 'app-retry-after'],
    ]) {
      const selected = variant === 'forbidden' || variant === 'not-found' ? `&node_id=${LEAF}` : '';
      await page.goto(
        withVariants(base, [`?fixture.variant.listAuthoringTree=${variant}${selected}`]),
      );
      await expect(page.locator(`${PAGE} ${selector}`)).toBeVisible();
      await expect(page.locator('[data-authoring-cases]')).toBeVisible();
      await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(0);
      if (variant === 'forbidden' || variant === 'not-found') {
        await expect(page.locator(PAGE)).not.toContainText(LEAF);
        await expect(page.locator('[data-authoring-breadcrumb]')).toHaveCount(0);
      }
      await expectNoA11yViolations(page, PAGE);
    }
  });

  test(`tree stale refresh preserves usable roots and provides explicit retry (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariants(base, ['?fixture.variant.listAuthoringTree=stale']));
    await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(2);
    await page.locator('[data-authoring-refresh]').click();
    await expect(page.locator('[data-authoring-stale]')).toBeVisible();
    await expect(page.locator(`${PAGE} app-error-pattern`)).toBeVisible();
    await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(2);
    await page.locator(`${PAGE} app-error-pattern button`).click();
    await expect(page.locator('[data-authoring-stale]')).toBeVisible();
    await expect(page.locator('[data-authoring-cases]')).toBeVisible();
    await expectNoA11yViolations(page, PAGE);
  });

  test(`child loading, empty and retryable error stay inside the expanded branch (${locale})`, async ({
    page,
  }) => {
    for (const [variant, selector] of [
      ['slow-children', 'app-loading-pattern'],
      ['child-empty', 'app-empty-pattern'],
      ['child-error', 'app-error-pattern'],
    ]) {
      await page.goto(withVariants(base, [`?fixture.variant.listAuthoringTree=${variant}`]));
      await page.locator(`[data-disclosure="${ROOT}"]`).click();
      await expect(page.locator(`${TREE} ${selector}`)).toBeVisible();
      await expect(page.locator(`[data-scope="${ACCOUNT}"]`)).toBeEnabled();
      await expect(page.locator('[data-authoring-cases]')).toBeVisible();
      if (variant === 'child-error') {
        await page.locator(`${TREE} app-error-pattern button`).click();
        await expect(page.locator(`${TREE} app-error-pattern`)).toBeVisible();
      }
    }
  });
}

test.describe('authoring tree coarse pointer targets', () => {
  test.use({ hasTouch: true });
  test('keeps scope and disclosure targets at least 44px on touch devices', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 900 });
    await page.goto(
      localePath('vi', `/dev/authoring-tree/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    );
    for (const selector of [`[data-disclosure="${ROOT}"]`, `[data-scope="${ROOT}"]`]) {
      const box = await page.locator(selector).boundingBox();
      if (!box) throw new Error('The node control is not visible.');
      expect(box.width).toBeGreaterThanOrEqual(44);
      expect(box.height).toBeGreaterThanOrEqual(44);
    }
  });
});

const CASE_TABLE = 'app-authoring-case-table';
const CASE_ROWS = 'app-authoring-case-rows';
const CHECKLIST = '018f0000-0000-7000-8000-000000000201';

async function lastCaseQuery(page: Page): Promise<Record<string, string>> {
  return page.evaluate(() => {
    const request = window.__evidgateFixtureRequests
      ?.filter((entry) => entry.url.includes('/authoring/manual-cases'))
      .at(-1);
    return request ? Object.fromEntries(new URL(request.url, location.origin).searchParams) : {};
  });
}

for (const locale of LOCALES) {
  const base = withVariants(
    localePath(locale, `/dev/authoring-tree/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    ['?fixture.variant.listPermissions=settings-policy-readonly'],
  );

  test(`case table sends every supported filter and restores URL history (${locale})`, async ({
    page,
  }) => {
    await page.goto(
      withVariants(base, [
        `?node_id=${ROOT}&item_id=${CHECKLIST}&state=draft&priority=high&covered=true&q=${encodeURIComponent('địa')}&limit=100`,
      ]),
    );
    await expect(page.locator(`${CASE_ROWS} [data-row-id]`)).toHaveCount(1);
    expect(await lastCaseQuery(page)).toEqual({
      node_id: ROOT,
      item_id: CHECKLIST,
      state: 'draft',
      priority: 'high',
      covered: 'true',
      q: 'địa',
      limit: '100',
    });
    const search = page.locator('#authoring-case-search');
    await search.fill('no matching case');
    await page.locator('[data-case-apply]').click();
    await expect(page.locator(`${CASE_TABLE} app-empty-pattern`)).toBeVisible();
    expect(new URL(page.url()).searchParams.has('cursor')).toBe(false);
    await page.goBack();
    await expect(search).toHaveValue('địa');
    await expect(page.locator(`${CASE_ROWS} [data-row-id]`)).toHaveCount(1);
    await page.goForward();
    await expect(search).toHaveValue('no matching case');
    await expect(page.locator(`${CASE_TABLE} app-empty-pattern`)).toBeVisible();
    await page.locator(`${CASE_TABLE} app-empty-pattern button`).click();
    await expect(search).toHaveValue('');
    expect(new URL(page.url()).searchParams.get('node_id')).toBe(ROOT);
    await expect(page.locator(`${CASE_ROWS} [data-row-id]`)).toHaveCount(2);
  });

  test(`case table mixes visible selection, resets it for filters, and refuses unsupported scope (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(base);
    const all = page.locator('[data-select-visible] [role="checkbox"]');
    const rowBoxes = page.locator('[data-case-select] [role="checkbox"]');
    await expect(rowBoxes).toHaveCount(2);
    await tabTo(page, all);
    await page.keyboard.press('Space');
    await expect(all).toHaveAttribute('aria-checked', 'true');
    await rowBoxes.first().click();
    await expect(all).toHaveAttribute('aria-checked', 'mixed');
    await expect(page.locator('[data-selection-summary]')).toContainText(/1.*2/);
    await expect(page.locator('[data-export-selection]')).toHaveAttribute('aria-disabled', 'true');
    await expect(page.locator('#authoring-export-help')).toBeVisible();
    await page.locator('[data-case-filter-help] summary').click();
    await expect(page.locator('#authoring-case-source')).toBeDisabled();
    await expect(page.locator('#authoring-case-owner')).toBeDisabled();
    await expect(page.locator('#authoring-source-help')).toBeVisible();
    await expect(page.locator('#authoring-owner-help')).toBeVisible();
    await page.locator('#authoring-case-search').fill('Áp dụng');
    await page.locator('[data-case-apply]').click();
    await expect(rowBoxes).toHaveCount(1);
    await expect(all).toHaveAttribute('aria-checked', 'false');
    expect(await lastCaseQuery(page)).not.toHaveProperty('source');
    expect(await lastCaseQuery(page)).not.toHaveProperty('owner');
    await expectNoA11yViolations(page, PAGE);
  });

  test(`case table pages opaquely and resets cursor and selection on filtering (${locale})`, async ({
    page,
  }) => {
    await page.goto(base);
    await expect(page.locator(`${CASE_ROWS} [data-row-id]`)).toHaveCount(2);
    await page.locator(`${CASE_TABLE} app-data-table-pager [data-page="next"]`).click();
    await expect(page.locator(`${CASE_ROWS} [data-row-id]`)).toHaveCount(1);
    await expect(page).toHaveURL(/cursor=authoring-cases-2/);
    expect((await lastCaseQuery(page))['cursor']).toBe('authoring-cases-2');
    await page.locator('#authoring-case-search').fill('Địa chỉ');
    await page.locator('[data-case-apply]').click();
    await expect(page.locator(`${CASE_ROWS} [data-row-id]`)).toHaveCount(2);
    expect(new URL(page.url()).searchParams.has('cursor')).toBe(false);
    expect(await lastCaseQuery(page)).not.toHaveProperty('cursor');
  });

  test(`case table independently renders loading, empty, forbidden and Retry-After (${locale})`, async ({
    page,
  }) => {
    for (const [variant, selector] of [
      ['loading', 'app-loading-pattern'],
      ['empty', 'app-empty-pattern'],
      ['error', 'app-error-pattern'],
      ['forbidden', 'app-forbidden-pattern'],
      ['unavailable', 'app-retry-after'],
    ]) {
      await page.goto(withVariants(base, [`?fixture.variant.listManualCases=${variant}`]));
      await expect(page.locator(`${CASE_TABLE} ${selector}`)).toBeVisible();
      await expect(page.locator(`${TREE} [data-node-id]`)).toHaveCount(2);
      if (variant === 'forbidden')
        await expect(page.locator(CASE_TABLE)).not.toContainText('SHOP-601');
      if (variant === 'unavailable')
        await expect(page.locator(`${CASE_TABLE} app-retry-after button`)).toBeDisabled();
    }
  });

  for (const theme of THEMES)
    for (const width of [360, 900, 1280, 1440]) {
      test(`case table long titles reflow with keyboard inspection and axe (${locale}, ${theme}, ${width})`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base);
        await expect(page.locator(CASE_ROWS)).toContainText('Tuỳ chọn giao hàng');
        const fieldGrid = page.locator('[data-case-filters] .filter-fields');
        const gridLayout = await fieldGrid.evaluate((grid) => {
          const pane = grid.closest('app-authoring-case-table');
          if (!pane) throw new Error('Missing case pane');
          return {
            widthInEm: pane.clientWidth / parseFloat(getComputedStyle(pane).fontSize),
            columns: getComputedStyle(grid).gridTemplateColumns.split(' ').length,
          };
        });
        expect(gridLayout.columns).toBe(
          gridLayout.widthInEm >= 52 ? 5 : gridLayout.widthInEm >= 32 ? 3 : 1,
        );
        const widths = await fieldGrid
          .locator('input, button[role="combobox"]')
          .evaluateAll((fields) => fields.map((field) => field.getBoundingClientRect().width));
        expect(widths).toHaveLength(5);
        expect(Math.max(...widths) - Math.min(...widths)).toBeLessThanOrEqual(1);
        const identifiers = page.locator(`${CASE_ROWS} [data-case-identifiers]`).first();
        await identifiers.click();
        const dialog = page.getByRole('dialog');
        await expect(dialog).toHaveAccessibleName(/SHOP-601/);
        await expect(dialog).toContainText('018f0000-0000-7000-8000-000000000777');
        await page.keyboard.press('Escape');
        await expect(dialog).toHaveCount(0);
        await expect(identifiers).toBeFocused();
        if (width === 1440) {
          await expect(page.locator(`${CASE_ROWS} [data-row-id]`).first()).toBeInViewport();
        }
        if (width === 900) {
          const treeScroll = page.locator('[aria-labelledby="authoring-hierarchy-title"]');
          expect(await treeScroll.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
        }
        await expect(page.locator('#authoring-case-pending-help')).toBeVisible();
        if (width < 768) {
          await expect(page.locator('[data-case-select]')).toHaveCount(0);
          await expect(page.locator('[data-selection-notice]')).toBeVisible();
          await expect(page.locator(`${CASE_ROWS} dl`)).not.toHaveCount(0);
        }
        await page.locator('#authoring-case-search').focus();
        await page.keyboard.press('Tab');
        await expect(page.locator('[data-case-apply]')).toBeFocused();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
        await expectNoA11yViolations(page, PAGE);
      });
    }
}

test('case table keeps a bounded visible page while browsing more than 200 cases', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1920, height: 1080 });
  const base = localePath(
    'en',
    `/dev/authoring-tree/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`,
  );
  await page.goto(withVariants(base, ['?fixture.variant.listManualCases=bulk&limit=100']));
  const pager = page.locator(`${CASE_TABLE} app-data-table-pager`);
  const rows = page.locator(`${CASE_ROWS} [data-row-id]`);
  await expect(rows).toHaveCount(100);
  await expect(pager.locator('[role="status"]')).toHaveText('Page 1');
  await pager.locator('[data-page="next"]').click();
  await expect(pager.locator('[role="status"]')).toHaveText('Page 2');
  await expect(rows).toHaveCount(100);
  await pager.locator('[data-page="next"]').click();
  await expect(pager.locator('[role="status"]')).toHaveText('Page 3');
  await expect(rows).toHaveCount(5);
  await expect(pager.locator('[data-page="next"]')).toHaveAttribute('aria-disabled', 'true');
  await pager.locator('[data-page="previous"]').click();
  await expect(pager.locator('[role="status"]')).toHaveText('Page 2');
  await expect(rows).toHaveCount(100);
  await expect(page.locator(`${CASE_ROWS} cdk-virtual-scroll-viewport`)).toHaveCount(0);
});
