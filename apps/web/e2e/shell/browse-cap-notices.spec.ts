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
const CASE_PAGE_SIZE = 50;
const SURFACES = [
  {
    name: 'authoring cases',
    path: `${PROJECT_PATH}/authoring?limit=${CASE_PAGE_SIZE}`,
    operation: 'listManualCases',
    endpoint: `/v1/projects/${PROJECT_STOREFRONT}/authoring/manual-cases`,
    notice: '[data-case-browse-capped]',
    pager: 'app-authoring-case-table .pagination > app-data-table-pager',
    rows: 'app-authoring-case-rows [data-row-id]',
    title: {
      en: 'Case list is at its load limit',
      vi: 'Danh sách ca kiểm thử đã đạt giới hạn tải',
    },
    description: {
      en: (figure: string) =>
        `Narrow the filters or choose another scope in the hierarchy to reach cases beyond the ${figure} loaded here.`,
      vi: (figure: string) =>
        `Hãy thu hẹp bộ lọc hoặc chọn phạm vi khác trong phân cấp để xem các ca kiểm thử nằm ngoài ${figure} ca đã tải tại đây.`,
    },
  },
  {
    name: 'catalog cases',
    path: `/dev/catalog-list${PROJECT_PATH}?limit=${CASE_PAGE_SIZE}`,
    operation: 'listCases',
    endpoint: `/v1/projects/${PROJECT_STOREFRONT}/cases`,
    notice: '[data-catalog-browse-capped]',
    pager: '[data-catalog-pagination]',
    rows: 'app-case-list-table [data-row-id]',
    title: {
      en: 'Case list is at its load limit',
      vi: 'Danh sách ca đã đạt giới hạn tải',
    },
    description: {
      en: (figure: string) =>
        `Narrow the status or framework filters to reach cases beyond the ${figure} loaded here. Search only checks loaded results.`,
      vi: (figure: string) =>
        `Thu hẹp bộ lọc trạng thái hoặc framework để xem các ca ngoài ${figure} ca đã tải ở đây. Tìm kiếm chỉ kiểm tra kết quả đã tải.`,
    },
  },
  {
    name: 'authoring hierarchy',
    path: `${PROJECT_PATH}/authoring`,
    operation: 'listAuthoringTree',
    endpoint: `/v1/projects/${PROJECT_STOREFRONT}/authoring/tree`,
    notice: '[data-tree-browse-capped]',
    more: '[data-more="roots"]',
    rows: 'app-authoring-tree-pane [data-node-id]',
    title: {
      en: 'Hierarchy list is at its load limit',
      vi: 'Danh sách phân cấp đã đạt giới hạn tải',
    },
    description: {
      en: (figure: string) =>
        `Showing the first ${figure} nodes in this list. Open a known node link to inspect a scope outside these results.`,
      vi: (figure: string) =>
        `Đang hiển thị ${figure} nút đầu tiên trong danh sách này. Mở liên kết nút đã biết để xem phạm vi ngoài các kết quả này.`,
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

/** The cap the fixture build is seeded with, so the walk reaches it in one page per surface. */
const SEEDED_CAP = 25;

async function reachCap(page: Page, surface: Surface, locale: ShellLocale): Promise<void> {
  await page.goto(
    withVariants(localePath(locale, surface.path), [
      surface.operation === 'listCases'
        ? '?fixture.variant.listPermissions=settings-policy-readonly'
        : '?fixture.variant.listPermissions=authoring-editor-read',
      `?fixture.variant.${surface.operation}=browse-cap`,
      `?fixture.retainedRowsCap=${SEEDED_CAP}`,
    ]),
  );
  await expect(page.locator('html')).toHaveAttribute('lang', locale);
  await expect(page.locator(surface.rows)).toHaveCount(SEEDED_CAP);
  await expect(page.locator(surface.notice)).toBeVisible();
  if (surface.operation === 'listAuthoringTree') {
    await expect(page.locator(surface.more)).toHaveCount(0);
  } else {
    const pager = page.locator(surface.pager);
    await expect(pager.getByRole('status')).toHaveText(locale === 'en' ? 'Page 1' : 'Trang 1');
    await expect(pager.locator('[data-page="next"]')).toBeDisabled();
  }
  const requests = await reads(page, surface.endpoint);
  expect(requests).toHaveLength(1);
  const query = new URL(requests[0], page.url()).searchParams;
  expect(query.get('cursor')).toBeNull();
  if (surface.operation !== 'listAuthoringTree') {
    expect(query.get('limit')).toBe(String(CASE_PAGE_SIZE));
  }
}

for (const surface of SURFACES) {
  for (const locale of LOCALES) {
    test(`${surface.name} capped notice covers both themes and widths (${locale})`, async ({
      page,
    }) => {
      // The seeded cap keeps every surface inside one page, so the notice is reached in seconds.
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
            await expect(notice).toContainText(surface.title[locale]);
            await expect(notice).toContainText(
              surface.description[locale](new Intl.NumberFormat(locale).format(SEEDED_CAP)),
            );
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
      expect(await reads(page, surface.endpoint)).toHaveLength(1);
    });
  }
}
