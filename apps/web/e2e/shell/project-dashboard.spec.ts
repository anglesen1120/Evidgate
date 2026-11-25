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
        await expect(page.locator('[data-dashboard-kpis] dl')).toHaveCount(1);
        const register = page.locator('[data-dashboard-recent]');
        await expect(register.getByText('main', { exact: true }).first()).toBeVisible();
        await expect(register.locator('app-copyable-identifier')).toHaveCount(0);
        if (width === 1440)
          await expect(register.getByRole('columnheader', { name: /UTC/ })).toBeVisible();
        await expect(register.locator('time[datetime]')).toHaveCount(5);
        await expect(page.locator('[data-dashboard-kpis]').getByRole('term')).toHaveCount(5);
        await expect(page.locator('[data-dashboard-kpis]').getByRole('definition')).toHaveCount(5);
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
        ).toBe(true);
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
  await expect(day).toHaveAccessibleDescription(/PASS: \d+.*FAIL: \d+.*INCONCLUSIVE: \d+/);
  const [bars, readout, legend] = await Promise.all([
    chart.getByRole('toolbar').boundingBox(),
    values.boundingBox(),
    chart.getByRole('list').boundingBox(),
  ]);
  if (!bars || !readout || !legend) throw new Error('Expected visible chart and readout.');
  expect(legend.y + legend.height).toBeLessThanOrEqual(bars.y + 1);
  expect(bars.y + bars.height).toBeLessThanOrEqual(readout.y + 1);
  expect(readout.x).toBeGreaterThanOrEqual(bars.x - 1);
  expect(readout.x + readout.width).toBeLessThanOrEqual(bars.x + bars.width + 1);
  await values.hover();
  await expect(values).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(values).toBeHidden();
}

test('the focused date has an exact keyboard-accessible row at phone width', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto(overviewPath('vi') + FIXTURE);
  const chart = page.locator('app-decision-trend');
  const day = chart.getByRole('toolbar').getByRole('button').nth(1);
  const tip = chart.getByRole('tooltip');
  await day.focus();
  await expect(tip).toBeVisible();
  const date = await day.locator('time').innerText();
  const total = (await day.innerText()).replace(date, '').trim();
  await expect(tip).toContainText(date);
  await expect(tip).toContainText(`Tổng: ${total}`);
  await page.keyboard.press('Escape');
  await expect(tip).toBeHidden();
  await expect(day).toBeFocused();
  const disclosure = chart.locator('summary');
  await disclosure.focus();
  await page.keyboard.press('Enter');
  const row = chart
    .getByRole('row')
    .filter({ has: page.getByRole('rowheader', { name: date, exact: true }) });
  await expect(row).toBeVisible();
  await expect(row.getByRole('cell').last()).toHaveText(total);
  await expectNoA11yViolations(page, 'project dashboard exact dates at 390px');
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
  await expect(dialog.locator('app-copy-value-button button')).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveAccessibleName(/\S/);
  await waitForDialogAnimations(page, '[data-dashboard-run-dialog]');
  await expectNoA11yViolations(page, 'project run inspection');
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(view).toBeFocused();
});

test('500 sampled dates stay legible and keyboard-reachable at 390px', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 1000 });
  await page.goto(overviewPath('en') + FIXTURE + '&fixture.variant.listRuns=dashboard-dense');
  const chart = page.locator('app-decision-trend');
  const dates = chart.getByRole('toolbar').getByRole('button');
  await expect(dates).toHaveCount(500);
  const first = dates.first();
  const last = dates.last();
  await first.focus();
  await page.keyboard.press('End');
  await expect(last).toBeFocused();
  await expect(last).toBeInViewport();
  const lastDate = await last.locator('time').innerText();
  await expect(chart.getByRole('tooltip')).toContainText(lastDate);
  await page.keyboard.press('ArrowUp');
  await expect(dates.nth(498)).toBeFocused();
  await page.keyboard.press('Home');
  await expect(first).toBeFocused();
  await expect(page.locator('[data-dashboard-attention]')).toContainText(
    'Not available (latest runs not loaded)',
  );
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.locator('[data-dashboard-trend] summary').click();
  await expect(page.locator('[data-dashboard-trend] table')).toBeVisible();
  await expect(chart.getByRole('rowheader')).toHaveCount(500);
  await expect(chart.getByRole('rowheader', { name: lastDate, exact: true })).toBeVisible();
});

test('unequal daily counts share a horizontal baseline and match the table totals', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(overviewPath('en') + FIXTURE + '&fixture.variant.listRuns=dashboard-unequal');
  const chart = page.locator('app-decision-trend');
  const days = chart.getByRole('toolbar').getByRole('button');
  await expect(days).toHaveCount(2);
  const [three, one] = await days.evaluateAll((buttons) =>
    buttons.map((button) => {
      const segments = Array.from(button.querySelectorAll('[data-segment]'), (segment) =>
        segment.getBoundingClientRect(),
      );
      return {
        left: Math.min(...segments.map((segment) => segment.left)),
        width: segments.reduce((sum, segment) => sum + segment.width, 0),
      };
    }),
  );
  if (!three || !one) throw new Error('The unequal-count fixture must render two dates.');
  expect(Math.abs(three.left - one.left)).toBeLessThan(1);
  expect(one.width).toBeGreaterThan(0);
  expect(Math.abs(three.width / one.width - 3)).toBeLessThan(0.05);
  await chart.locator('summary').click();
  const [larger, smaller] = await chart
    .getByRole('row')
    .filter({ has: page.getByRole('rowheader') })
    .all();
  if (!larger || !smaller) throw new Error('Missing exact rows for the unequal-count dates.');
  await expect(larger.getByRole('cell').last()).toHaveText('3');
  await expect(smaller.getByRole('cell').last()).toHaveText('1');
});
