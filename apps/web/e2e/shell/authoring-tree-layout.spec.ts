import { expect, test, type Locator } from '@playwright/test';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
} from './support';

async function expectCasesToFit(rows: Locator): Promise<void> {
  const surface = rows.locator('.spartan-table-container');
  await expect(surface.locator('[data-case-view]').first()).toBeVisible();
  await expect
    .poll(() =>
      surface.evaluate((el) => {
        const { left, right } = el.getBoundingClientRect();
        return (
          el.scrollWidth <= el.clientWidth + 1 &&
          [...el.querySelectorAll('[data-case-identifiers], [data-case-view]')].every((cell) => {
            const bounds = cell.getBoundingClientRect();
            return (
              bounds.width > 0 &&
              bounds.left >= Math.max(0, left) - 1 &&
              bounds.right <= Math.min(innerWidth, right) + 1
            );
          })
        );
      }),
    )
    .toBe(true);
}

async function expectReadableDesktopRows(rows: Locator): Promise<void> {
  const selection = rows.getByRole('columnheader').first();
  await expect(selection).toHaveAccessibleName(/Selection|Chọn|Lựa chọn/i);
  await expect(selection).toHaveCSS('font-size', '0px');
  await expect(rows.locator('[data-case-key]').first()).toHaveText('SHOP-601');
  const broken = await rows.evaluate((table) => {
    const issues: string[] = [];
    const selector =
      'th:not(.cdk-column-select), [data-case-key], [data-case-view], .coverage-count';
    for (const el of table.querySelectorAll<HTMLElement>(selector)) {
      const range = document.createRange();
      range.selectNodeContents(el);
      const box = range.getBoundingClientRect();
      const cell = el.getBoundingClientRect();
      const line = parseFloat(getComputedStyle(el).lineHeight);
      if (box.height > line + 2 || box.right > cell.right + 1 || box.left < cell.left - 1) {
        issues.push(el.innerText);
      }
    }
    for (const group of table.querySelectorAll('.coverage-summary, .case-key-line')) {
      const centers = [...group.children].map((child) => {
        const r = child.getBoundingClientRect();
        return r.top + r.height / 2;
      });
      if (Math.max(...centers) - Math.min(...centers) > 1) issues.push(group.textContent ?? '');
    }
    return issues;
  });
  expect(broken, 'clipped or wrapped case headers, keys, controls or coverage').toEqual([]);
}

for (const locale of LOCALES) {
  const base = withVariants(
    localePath(locale, `/dev/authoring-tree/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    ['?fixture.variant.listPermissions=settings-policy-readonly'],
  );
  for (const theme of THEMES) {
    for (const width of [1440, 1280, 900, 390]) {
      for (const empty of [false, true]) {
        test(`authoring layout acceptance (${locale}, ${theme}, ${width}, ${empty ? 'empty' : 'populated'})`, async ({
          page,
        }, testInfo) => {
          await seedStoredTheme(page, theme);
          await page.setViewportSize({ width, height: 1000 });
          await page.goto(
            empty
              ? withVariants(base, [
                  '?fixture.variant.listAuthoringTree=empty&fixture.variant.listManualCases=empty',
                ])
              : base,
          );
          const authoring = page.locator('app-authoring-page');
          await expect(authoring).toBeVisible();
          if (empty) {
            await expect(page.locator('[data-authoring-project-empty]')).toBeVisible();
            await expect(authoring.locator('app-empty-pattern')).toHaveCount(1);
            await expect(page.locator('[data-case-filters], .pagination')).toHaveCount(0);
            await expect(page.locator('[data-authoring-project-empty] button')).toHaveText(
              locale === 'vi' ? 'Nhập và xuất dữ liệu' : 'Import and export',
            );
          } else {
            await expect(
              page
                .locator('app-authoring-case-rows [data-row-id], app-authoring-case-rows dl')
                .first(),
            ).toBeVisible();
            await expect(page.locator('.pagination > app-data-table-pager')).toBeVisible();
            await expectCasesToFit(authoring.locator('app-authoring-case-rows'));
            if (width >= 1280)
              await expectReadableDesktopRows(authoring.locator('app-authoring-case-rows'));
            const metrics = await page.locator('[data-authoring-metrics]').innerText();
            expect.soft(metrics).toContain('2');
            if (width === 1440) {
              const layout = await page.locator('.filter-fields').evaluate((grid) => ({
                columns: getComputedStyle(grid).gridTemplateColumns.split(' ').length,
                labels: [...grid.querySelectorAll('label')].map((label) => ({
                  text: label.textContent,
                  height: label.getBoundingClientRect().height,
                  line: Number.parseFloat(getComputedStyle(label).lineHeight),
                })),
                controls: [...grid.querySelectorAll('input,button[role="combobox"]')].map(
                  (field) => {
                    const { width, height, top } = field.getBoundingClientRect();
                    return { width, height, top };
                  },
                ),
              }));
              expect.soft(layout.columns).toBe(5);
              expect.soft(layout.controls).toHaveLength(5);
              expect(await page.evaluate(() => matchMedia('(pointer: fine)').matches)).toBe(true);
              for (const dimension of ['width', 'height', 'top'] as const) {
                const values = layout.controls.map((control) => control[dimension]);
                expect
                  .soft(Math.max(...values) - Math.min(...values), dimension)
                  .toBeLessThanOrEqual(1);
              }
              for (const label of layout.labels)
                expect.soft(label.height, label.text ?? '').toBeLessThanOrEqual(label.line + 1);
              const pager = await page.locator('.pagination').evaluate((row) => {
                const elements = [
                  row.querySelector('app-data-table-pager [role="status"]'),
                  row.querySelector('app-data-table-pager label'),
                  row.querySelector('app-data-table-pager hlm-select-trigger button'),
                ];
                return elements.map((element) => {
                  if (!element) throw new Error('Missing pager control');
                  const r = element.getBoundingClientRect();
                  return r.top + r.height / 2;
                });
              });
              expect.soft(Math.max(...pager) - Math.min(...pager)).toBeLessThanOrEqual(2);
            }
            await page.locator('[data-case-filters] > summary').click();
            await expect(page.locator('#authoring-case-search')).toBeHidden();
            await page.locator('[data-case-filters] > summary').click();
            await expect(page.locator('#authoring-case-search')).toBeVisible();
          }
          expect
            .soft(await page.evaluate(() => document.documentElement.scrollWidth), 'page width')
            .toBeLessThanOrEqual(width);
          const screenshot = testInfo.outputPath(
            `authoring-${locale}-${theme}-${width}-${empty ? 'empty' : 'populated'}.png`,
          );
          await page.screenshot({ path: screenshot, fullPage: true });
          await testInfo.attach('layout', { path: screenshot, contentType: 'image/png' });
        });
      }
    }
  }

  test(`bulk cases keep identifiers and actions inside the pane (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(withVariants(base, ['?fixture.variant.listManualCases=bulk&limit=100']));
    await page.locator('app-authoring-case-table app-data-table-pager [data-page="next"]').click();
    await expect(
      page.locator('app-authoring-case-table app-data-table-pager [role="status"]'),
    ).toContainText('2');
    await expect(page.locator('app-authoring-case-rows [data-row-id]')).toHaveCount(100);
    const rows = page.locator('app-authoring-case-rows');
    await expectCasesToFit(rows);
    await page.setViewportSize({ width: 1280, height: 1000 });
    await expectCasesToFit(rows);
  });

  test(`filtered miss on an otherwise empty project is not the project empty state (${locale})`, async ({
    page,
  }) => {
    await page.goto(
      withVariants(base, [
        '?fixture.variant.listAuthoringTree=empty&fixture.variant.listManualCases=empty&q=unmatched',
      ]),
    );
    await expect(page.locator('app-authoring-case-table app-empty-pattern')).toBeVisible();
    await expect(page.locator('[data-authoring-project-empty]')).toHaveCount(0);
    await expect(page.locator('[data-case-filters]')).toBeVisible();
    await expect(page.locator('app-authoring-case-table app-empty-pattern button')).toBeVisible();
  });

  for (const operation of ['listAuthoringTree', 'listManualCases']) {
    test(`failed ${operation} with the other collection empty is not project empty (${locale})`, async ({
      page,
    }) => {
      const variants = new URLSearchParams({
        'fixture.variant.listAuthoringTree': operation === 'listAuthoringTree' ? 'error' : 'empty',
        'fixture.variant.listManualCases': operation === 'listManualCases' ? 'error' : 'empty',
      });
      await page.goto(withVariants(base, [`?${variants}`]));
      await expect(page.locator('app-authoring-page app-error-pattern').first()).toBeVisible();
      await expect(page.locator('[data-authoring-project-empty]')).toHaveCount(0);
      await expect(page.locator('[data-case-filters]')).toBeVisible();
    });
  }
}
