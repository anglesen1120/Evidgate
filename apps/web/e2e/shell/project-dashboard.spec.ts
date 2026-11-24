import { mkdir } from 'node:fs/promises';
import { test, expect, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { LOCALES, THEMES, overviewPath, seedStoredTheme, waitForDialogAnimations } from './support';

const FIXTURE = '?fixture.variant.listPermissions=dashboard';
const WIDGETS =
  '[data-dashboard-kpis], [data-dashboard-trend], [data-dashboard-recent], [data-dashboard-attention], [data-dashboard-authoring], [data-dashboard-ci], [data-dashboard-usage], [data-dashboard-activity]';

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const width of [1440, 900, 390]) {
      test(`project dashboard ${locale} ${theme} ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 1000 });
        await seedStoredTheme(page, theme);
        await page.goto(overviewPath(locale) + FIXTURE);
        await expect(page.locator('app-project-overview')).toBeVisible();
        await expect(page.locator('app-project-overview [aria-busy="true"]')).toHaveCount(0);
        await expect(page.locator(WIDGETS)).toHaveCount(8);
        await expect(page.locator('[data-dashboard-run-view]')).toHaveCount(5);
        const wrapped = await page.locator('[data-dashboard-run-view]').evaluateAll((buttons) =>
          buttons.some((button) => {
            const walker = document.createTreeWalker(button, NodeFilter.SHOW_TEXT);
            let node: Node | null;
            while ((node = walker.nextNode())) {
              if (!node.textContent?.trim()) continue;
              const range = document.createRange();
              range.selectNodeContents(node);
              if (range.getClientRects().length > 1) return true;
            }
            return false;
          }),
        );
        expect(wrapped).toBe(false);
        await expect(page.locator('app-project-overview h1')).toHaveCount(1);
        await expect(page.locator('app-project-overview h1')).toHaveText(
          locale === 'vi' ? 'Tổng quan dự án' : 'Project overview',
        );
        await expect(page.locator('app-project-overview app-page-header hlm-icon')).toBeVisible();
        await expect(page.locator('[data-dashboard-incidents]')).toContainText(
          locale === 'vi' ? 'Chưa khả dụng' : 'Not available yet',
        );
        await expect(
          page.locator(
            '[data-dashboard-incidents] button, [data-dashboard-incidents] app-error-pattern',
          ),
        ).toHaveCount(0);
        await expect(page.locator('[data-dashboard-kpis] app-dashboard-kpi hlm-icon')).toHaveCount(
          5,
        );
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
        ).toBe(true);
        expect(await page.locator('.trend-stack').evaluateAll(fullHeightStacksFit)).toBe(true);
        await expectNoA11yViolations(page, `project dashboard ${locale} ${theme} ${width}`);
        await mkdir('/tmp/s674-shots', { recursive: true });
        await page.screenshot({
          path: `/tmp/s674-shots/project-${width}-${theme}-${locale}.png`,
          fullPage: true,
        });
        await expectDayReadout(page, locale);
      });
    }
  }
}

async function expectDayReadout(page: Page, locale: string): Promise<void> {
  const chart = page.locator('app-decision-trend');
  const day = chart.getByRole('toolbar').getByRole('button').first();
  const values = chart.getByRole('tooltip');
  await day.hover();
  await expect(values).toContainText(locale === 'vi' ? 'Chưa đánh giá:' : 'Not evaluated:');
  await expect(values).toContainText(locale === 'vi' ? 'Tổng:' : 'Total:');
  await expect(day).toHaveAccessibleName(/PASS: \d+.*FAIL: \d+.*INCONCLUSIVE: \d+/);
  const [bar, tip, card, legend, dates] = await Promise.all([
    day.locator('.trend-segments').boundingBox(),
    values.boundingBox(),
    page.locator('[data-dashboard-trend]').boundingBox(),
    chart.getByRole('list').boundingBox(),
    chart
      .locator('.trend-column > span')
      .evaluateAll((labels) => labels.map((label) => label.getBoundingClientRect().top)),
  ]);
  if (!bar || !tip || !card || !legend) throw new Error('Expected visible chart geometry.');
  expect(dates.length).toBeGreaterThan(0);
  expect(tip.y + tip.height).toBeLessThanOrEqual(legend.y);
  expect(tip.y + tip.height).toBeLessThanOrEqual(Math.min(...dates));
  expect(tip.x).toBeGreaterThanOrEqual(card.x);
  expect(tip.x + tip.width).toBeLessThanOrEqual(card.x + card.width);
  expect(tip.x + tip.width).toBeGreaterThan(bar.x);
  expect(tip.x).toBeLessThan(bar.x + bar.width);
  expect(Math.abs(bar.y - tip.y - tip.height)).toBeLessThanOrEqual(2);
  await values.hover();
  await expect(values).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(values).toBeHidden();
}

test('a focused readout moves beside the bar when scrolling leaves no room above', async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto(overviewPath('vi') + FIXTURE);
  const chart = page.locator('app-decision-trend');
  const day = chart.getByRole('button').nth(1);
  const tip = chart.getByRole('tooltip');
  await day.focus();
  await expect(tip).toBeVisible();
  await day.evaluate((button) => button.scrollIntoView({ block: 'start', inline: 'nearest' }));
  await expect(async () => {
    const [bar, panel, card] = await Promise.all([
      day.locator('.trend-segments').boundingBox(),
      tip.boundingBox(),
      page.locator('[data-dashboard-trend]').boundingBox(),
    ]);
    if (!bar || !panel || !card) throw new Error('Expected visible chart geometry.');
    expect(panel.y).toBeLessThan(bar.y + bar.height);
    expect(panel.y + panel.height).toBeGreaterThan(bar.y);
    expect(panel.y + panel.height).toBeLessThanOrEqual(bar.y + bar.height + 1);
    expect(
      Math.min(Math.abs(panel.x + panel.width - bar.x), Math.abs(bar.x + bar.width - panel.x)),
    ).toBeLessThanOrEqual(1);
    expect(panel.x).toBeGreaterThanOrEqual(card.x);
    expect(panel.x + panel.width).toBeLessThanOrEqual(card.x + card.width);
  }).toPass();
  await page.keyboard.press('Escape');
  await expect(tip).toBeHidden();
  await expect(day).toBeFocused();
});

test('one failed source leaves other dashboard facts visible and retries only locally', async ({
  page,
}) => {
  await page.goto(
    overviewPath('en') + FIXTURE + '&fixture.variant.listProjectWaivers=dashboard-error',
  );
  await expect(page.locator('[data-dashboard-attention] app-error-pattern')).toBeVisible();
  await expect(page.locator('[data-dashboard-run-view]')).toHaveCount(5);
  await page.locator('[data-dashboard-attention] app-error-pattern button').click();
  await expect(page.locator('[data-dashboard-attention] app-error-pattern')).toBeVisible();
  await expect(page.locator('[data-dashboard-run-view]')).toHaveCount(5);
  await expect(page.locator('[data-dashboard-usage] meter')).toHaveCount(1);
});

test('successful empty runs remain empty rather than a green decision', async ({ page }) => {
  await page.goto(
    overviewPath('en') +
      FIXTURE +
      '&fixture.variant.listRuns=dashboard-empty&fixture.variant.getUsageSummary=dashboard-empty&fixture.variant.listAudit=dashboard-empty',
  );
  await expect(page.locator('[data-dashboard-recent] app-empty-pattern')).toBeVisible();
  await expect(page.locator('[data-dashboard-trend] app-empty-pattern')).toBeVisible();
  await expect(page.locator('[data-dashboard-kpis]')).toContainText('No data');
  await expect(page.locator('[data-dashboard-run-view]')).toHaveCount(0);
  for (const [widget, path] of [
    ['recent', 'M5 3h14v18H5z M10 8l5 4-5 4z'],
    ['trend', 'M4 20V4M4 20h16M8 16v-4M12 16V8M16 16v-6'],
    ['usage', 'M4 20V4M4 20h16M8 16v-4M12 16V8M16 16v-6'],
    ['activity', 'M7 4h10v16H7zM10 8h4M10 12h4M10 16h4'],
  ] as const) {
    const icon = page.locator(`[data-dashboard-${widget}] app-empty-pattern hlm-icon`);
    await expect(icon).toBeVisible();
    await expect(icon).toHaveAttribute('aria-hidden', 'true');
    await expect(icon.locator('path')).toHaveAttribute('d', path);
  }
  await expectNoA11yViolations(page, 'empty project dashboard');
});

test('restricted widgets are hidden instead of rendering forbidden cards', async ({ page }) => {
  await page.goto(overviewPath('en') + '?fixture.variant.listPermissions=dashboard-limited');
  await expect(page.locator('[data-dashboard-ci]')).toBeVisible();
  await expect(
    page.locator(
      '[data-dashboard-kpis], [data-dashboard-usage], [data-dashboard-authoring], [data-dashboard-activity], [data-dashboard-attention]',
    ),
  ).toHaveCount(0);
  await expect(page.locator('app-project-overview app-error-pattern')).toHaveCount(0);
});

test('run inspection is a named read-only dialog with Escape and focus return', async ({
  page,
}) => {
  await page.goto(overviewPath('en') + FIXTURE);
  const view = page.locator('[data-dashboard-run-view]').first();
  await view.click();
  const dialog = page.locator('[data-dashboard-run-dialog]');
  await expect(dialog).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveAccessibleName(/\S/);
  await waitForDialogAnimations(page, '[data-dashboard-run-dialog]');
  await expectNoA11yViolations(page, 'project run inspection');
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(view).toBeFocused();
});

test('500 sampled dates stay legible and keyboard-scrollable at 390px', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 1000 });
  await page.goto(overviewPath('en') + FIXTURE + '&fixture.variant.listRuns=dashboard-dense');
  const region = page.locator('[data-dashboard-trend] .trend-bars');
  await expect(region.locator('.trend-column')).toHaveCount(500);
  await expect(region).toHaveAttribute('role', 'region');
  await expect(region).toHaveAttribute('tabindex', '0');
  await expect(region).toHaveAccessibleName(/\S/);
  const geometry = await region.evaluate((element) => ({
    scrollable: element.scrollWidth > element.clientWidth,
    minimumWidth: Math.min(
      ...Array.from(
        element.querySelectorAll('.trend-column'),
        (column) => column.getBoundingClientRect().width,
      ),
    ),
    positiveOnly: Array.from(element.querySelectorAll('[data-segment]')).every(
      (segment) => segment.getBoundingClientRect().height > 0,
    ),
  }));
  expect(geometry.scrollable).toBe(true);
  expect(geometry.minimumWidth).toBeGreaterThanOrEqual(64);
  expect(await region.locator('.trend-stack').evaluateAll(fullHeightStacksFit)).toBe(true);
  expect(geometry.positiveOnly).toBe(true);
  await region.focus();
  await expect(region).toBeFocused();
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => region.evaluate((element) => element.scrollLeft)).toBeGreaterThan(0);
  await expect(page.locator('[data-dashboard-attention]')).toContainText(
    'Not available (latest runs not loaded)',
  );
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.locator('[data-dashboard-trend] summary').click();
  await expect(page.locator('[data-dashboard-trend] table')).toBeVisible();
});

test('decision bars share a bottom baseline and scale heights to run counts', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(overviewPath('en') + FIXTURE + '&fixture.variant.listRuns=dashboard-unequal');
  const columns = page.locator('[data-dashboard-trend] .trend-column');
  await expect(columns).toHaveCount(2);
  const geometry = await columns.evaluateAll((elements) =>
    elements.map((element) => {
      const segments = Array.from(element.querySelectorAll('[data-segment]'), (segment) =>
        segment.getBoundingClientRect(),
      );
      return {
        top: Math.min(...segments.map((segment) => segment.top)),
        bottom: Math.max(...segments.map((segment) => segment.bottom)),
        height: segments.reduce((sum, segment) => sum + segment.height, 0),
      };
    }),
  );
  const [three, one] = geometry;
  expect(three).toBeDefined();
  expect(one).toBeDefined();
  if (!three || !one) throw new Error('The unequal-count fixture must render two dates.');
  expect(Math.abs(three.bottom - one.bottom)).toBeLessThan(1);
  expect(three.height).toBeGreaterThan(100);
  expect(Math.abs(three.height / one.height - 3)).toBeLessThan(0.05);
  expect(one.top).toBeGreaterThan(three.top);
  await expect(page.locator('[data-dashboard-trend] .trend-total')).toHaveText(['3', '1']);
});

function fullHeightStacksFit(stacks: Element[]): boolean {
  return stacks.every((stack) => {
    const bounds = stack.getBoundingClientRect();
    const segments = Array.from(stack.querySelectorAll('[data-segment]'), (segment) =>
      segment.getBoundingClientRect(),
    );
    // Both fixtures fill every date to the maximum total; gaps must not shrink their evidence.
    return (
      segments.every(
        (segment) => segment.top >= bounds.top - 0.5 && segment.bottom <= bounds.bottom + 0.5,
      ) &&
      Math.abs(segments.reduce((height, segment) => height + segment.height, 0) - bounds.height) <=
        1
    );
  });
}
