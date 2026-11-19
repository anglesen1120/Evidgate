import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
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
        await expect(root.locator('dd')).toHaveText(['2', '2', '—']);
        await expect(page.locator('#authoring-pending-help')).toContainText(labels.pending);
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
        const longTitle = await page.locator(`[data-scope="${IDS[3]}"] bdi`).boundingBox();
        if (!longTitle) throw new Error('The long Vietnamese title is not visible.');
        expect(longTitle.height).toBeGreaterThan(24);
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

  test(`tree loading, empty, unavailable, denied and missing states leave the case region alone (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariants(base, ['?fixture.variant.listAuthoringTree=loading']));
    await expect(page.locator(`${PAGE} app-loading-pattern`)).toBeVisible();
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
