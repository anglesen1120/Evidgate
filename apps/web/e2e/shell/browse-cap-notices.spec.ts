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
    title: {
      en: 'Case list is at its load limit',
      vi: 'Danh sách ca kiểm thử đã đạt giới hạn tải',
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
    title: {
      en: 'Case list is at its load limit',
      vi: 'Danh sách ca đã đạt giới hạn tải',
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
    title: {
      en: 'Hierarchy list is at its load limit',
      vi: 'Danh sách phân cấp đã đạt giới hạn tải',
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
  await expectLoaded(page, surface, SEEDED_CAP);
  await expect(page.locator(surface.notice)).toBeVisible();
  await expect(page.locator(surface.more)).toHaveCount(0);
  const requests = await reads(page, surface.endpoint);
  expect(requests).toHaveLength(1);
  expect(requests.map((url) => new URL(url, page.url()).searchParams.get('cursor'))).toEqual([
    null,
  ]);
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
