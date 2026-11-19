import { expect, test, type Page } from '@playwright/test';
import type {} from '../../src/app/core/dev-fixtures/fixture-request-log';
import { axeScan, axeViolations, expectNoA11yViolations } from '../a11y/axe-scan';
import {
  LOCALES,
  THEMES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  localePath,
  seedStoredTheme,
  waitForDialogAnimations,
  withVariants,
  type ShellLocale,
} from './support';

const PROJECT_PATH = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`;
const SURFACES = [
  {
    name: 'authoring cases',
    path: `${PROJECT_PATH}/authoring?limit=200`,
    operation: 'listManualCases',
    endpoint: `/v1/projects/${PROJECT_STOREFRONT}/authoring/manual-cases`,
    notice: '[data-case-browse-capped]',
    more: '[data-case-load-more]',
    count: '[data-case-loaded]',
    pageSize: 200,
    copy: {
      en: [
        'Case list is at its load limit',
        'Narrow the filters or choose another scope in the hierarchy to reach cases beyond the 10,000 loaded here.',
      ],
      vi: [
        'Danh sách ca kiểm thử đã đạt giới hạn tải',
        'Hãy thu hẹp bộ lọc hoặc chọn phạm vi khác trong phân cấp để xem các ca kiểm thử nằm ngoài 10.000 ca đã tải tại đây.',
      ],
    },
  },
  {
    name: 'catalog cases',
    path: `/dev/catalog-list${PROJECT_PATH}?limit=200`,
    operation: 'listCases',
    endpoint: `/v1/projects/${PROJECT_STOREFRONT}/cases`,
    notice: '[data-catalog-browse-capped]',
    more: '[data-catalog-load-more]',
    count: '[data-catalog-loaded]',
    pageSize: 200,
    copy: {
      en: [
        'Case list is at its load limit',
        'Narrow the status or framework filters to reach cases beyond the 10,000 loaded here. Search only checks loaded results.',
      ],
      vi: [
        'Danh sách ca đã đạt giới hạn tải',
        'Thu hẹp bộ lọc trạng thái hoặc framework để xem các ca ngoài 10.000 ca đã tải ở đây. Tìm kiếm chỉ kiểm tra kết quả đã tải.',
      ],
    },
  },
  {
    name: 'authoring hierarchy',
    path: `${PROJECT_PATH}/authoring`,
    operation: 'listAuthoringTree',
    endpoint: `/v1/projects/${PROJECT_STOREFRONT}/authoring/tree`,
    notice: '[data-tree-browse-capped]',
    more: '[data-more="roots"]',
    count: 'app-authoring-tree-pane [data-node-id]',
    pageSize: 50,
    copy: {
      en: [
        'Hierarchy list is at its load limit',
        'Showing the first 10,000 nodes in this list. Open a known node link to inspect a scope outside these results.',
      ],
      vi: [
        'Danh sách phân cấp đã đạt giới hạn tải',
        'Đang hiển thị 10.000 nút đầu tiên trong danh sách này. Mở liên kết nút đã biết để xem phạm vi ngoài các kết quả này.',
      ],
    },
  },
] as const;
type Surface = (typeof SURFACES)[number];

async function reads(page: Page, endpoint: string): Promise<string[]> {
  return page.evaluate(
    (path) =>
      (window.__evidgateFixtureRequests ?? [])
        .filter(
          (request) =>
            request.method === 'GET' && new URL(request.url, location.origin).pathname === path,
        )
        .map((request) => request.url),
    endpoint,
  );
}

async function expectLoaded(page: Page, surface: Surface, count: number): Promise<void> {
  if (surface.operation === 'listAuthoringTree') {
    await expect(page.locator(surface.count)).toHaveCount(count);
  } else {
    await expect(page.locator(surface.count)).toContainText(
      new Intl.NumberFormat((await page.locator('html').getAttribute('lang')) ?? 'en').format(
        count,
      ),
    );
  }
}

async function reachCap(page: Page, surface: Surface, locale: ShellLocale): Promise<void> {
  await page.goto(
    withVariants(localePath(locale, surface.path), [
      surface.operation === 'listCases'
        ? '?fixture.variant.listPermissions=settings-policy-readonly'
        : '?fixture.variant.listPermissions=authoring-editor-read',
      `?fixture.variant.${surface.operation}=browse-cap`,
    ]),
  );
  await expect(page.locator('html')).toHaveAttribute('lang', locale);
  await expectLoaded(page, surface, surface.pageSize);
  await expect(page.locator(surface.notice)).toHaveCount(0);
  for (let loaded = surface.pageSize; loaded < 10_000; loaded += surface.pageSize) {
    const more = page.locator(surface.more);
    await expect(more).toBeEnabled();
    await more.focus();
    await more.press('Enter');
    await expectLoaded(page, surface, loaded + surface.pageSize);
  }
  await expect(page.locator(surface.notice)).toBeVisible();
  await expect(page.locator(surface.more)).toHaveCount(0);
  const requests = await reads(page, surface.endpoint);
  expect(requests).toHaveLength(10_000 / surface.pageSize);
  expect(requests.map((url) => new URL(url, page.url()).searchParams.get('cursor'))).toEqual(
    Array.from({ length: 10_000 / surface.pageSize }, (_, index) =>
      index === 0 ? null : `browse-cap-${surface.operation}-${index * surface.pageSize}`,
    ),
  );
}

for (const surface of SURFACES) {
  for (const locale of LOCALES) {
    test(`${surface.name} capped notice covers both themes and widths (${locale})`, async ({
      page,
    }) => {
      if (surface.operation === 'listAuthoringTree') test.setTimeout(240_000);
      await page.setViewportSize({ width: 1280, height: 900 });
      await seedStoredTheme(page, 'light');
      await reachCap(page, surface, locale);
      for (const width of [360, 1280]) {
        await page.setViewportSize({ width, height: 900 });
        for (const theme of THEMES) {
          await test.step(`${locale}, ${theme}, ${width}`, async () => {
            if ((await page.locator('html').getAttribute('data-theme')) !== theme) {
              await page.locator('button.theme-toggle').click();
            }
            await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
            await waitForDialogAnimations(page, 'html');
            const notice = page.locator(surface.notice);
            await expect(notice).toBeVisible();
            for (const text of surface.copy[locale]) await expect(notice).toContainText(text);
            expect(
              await page.evaluate(
                () => document.documentElement.scrollWidth <= document.documentElement.clientWidth,
              ),
            ).toBe(true);
            if (surface.operation === 'listAuthoringTree') {
              const results = await axeScan(page, surface.notice);
              expect(axeViolations(results)).toEqual([]);
              expect(
                [...results.passes, ...results.incomplete].some(
                  (rule) => rule.id === 'color-contrast',
                ),
              ).toBe(true);
            } else {
              await expectNoA11yViolations(page, `${surface.name} cap ${locale} ${theme} ${width}`);
            }
          });
        }
      }
      expect(await reads(page, surface.endpoint)).toHaveLength(10_000 / surface.pageSize);
    });
  }
}
