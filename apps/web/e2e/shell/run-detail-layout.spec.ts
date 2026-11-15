import { expect, test, type Locator, type Page } from '@playwright/test';
import { LOCALES } from './support';
import { runDetailPath } from './run-detail.support';

async function ready(page: Page): Promise<void> {
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
  await page.evaluate(() => document.fonts.ready);
}

async function textLineCounts(elements: Locator): Promise<number[]> {
  return elements.evaluateAll((values) =>
    values.map((value) => {
      const tops = new Set<number>();
      const walker = document.createTreeWalker(value, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        if (!node.textContent?.trim()) continue;
        const range = document.createRange();
        range.selectNodeContents(node);
        for (const rect of range.getClientRects()) {
          if (rect.width > 0 && rect.height > 0) tops.add(Math.round(rect.top));
        }
      }
      return tops.size;
    }),
  );
}

async function ordinaryGeometry(table: Locator) {
  const metrics = await table.evaluate((root) => {
    const headers = [...root.querySelectorAll<HTMLElement>('[role="columnheader"]')];
    const scroll = root.querySelector<HTMLElement>('.scroll')!;
    return {
      stacked: root.querySelector('li[data-row-id]') !== null,
      width: scroll.clientWidth,
      horizontalOverflow: scroll.scrollWidth - scroll.clientWidth,
      verticalOverflow: scroll.scrollHeight - scroll.clientHeight,
      isolation: getComputedStyle(root).isolation,
      columns: headers.map((header) => ({
        width: header.getBoundingClientRect().width,
        position: getComputedStyle(header).position,
      })),
    };
  });
  const lines = await textLineCounts(table.locator('[role="columnheader"]'));
  return {
    ...metrics,
    columns: metrics.columns.map((column, index) => ({ ...column, lines: lines[index] ?? 0 })),
  };
}

for (const locale of LOCALES) {
  test(`producer budgets use page flow without compressed numeric headers (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(runDetailPath({ locale, run: 'many-producers' }));
    await ready(page);
    const table = page.locator('app-producer-status-table app-data-table');
    for (const width of [1440, 1280, 1024]) {
      await page.setViewportSize({ width, height: 900 });
      await expect(table.locator('[data-row-id]')).toHaveCount(50);
      await expect(table.locator('.eg-table-long, .eg-table-sticky')).toHaveCount(0);
      await expect
        .poll(async () => (await ordinaryGeometry(table)).horizontalOverflow)
        .toBeLessThanOrEqual(1);
      const metrics = await ordinaryGeometry(table);
      expect(metrics.verticalOverflow).toBeLessThanOrEqual(1);
      expect(metrics.isolation).toBe('isolate');
      if (metrics.stacked) {
        await expect(table.locator('li[data-row-id] dt')).toHaveCount(300);
      } else {
        expect(metrics.columns).toHaveLength(6);
        expect(metrics.columns[0]!.width / metrics.width).toBeLessThan(0.3);
        expect(metrics.columns[5]!.width).toBeGreaterThanOrEqual(metrics.columns[3]!.width);
        for (const column of metrics.columns) {
          expect(column.lines).toBeLessThanOrEqual(2);
          expect(column.position).not.toBe('sticky');
        }
      }
    }
  });

  test(`release identifiers receive useful width beside the intrinsic component key (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(runDetailPath({ locale, run: 'releases' }));
    await ready(page);
    const table = page.locator('app-release-panel app-data-table');
    for (const width of [1440, 1280, 1024]) {
      await page.setViewportSize({ width, height: 900 });
      await expect(table.locator('[data-release-digest]')).toBeVisible();
      await expect
        .poll(async () => (await ordinaryGeometry(table)).horizontalOverflow)
        .toBeLessThanOrEqual(1);
      const metrics = await ordinaryGeometry(table);
      expect(metrics.verticalOverflow).toBeLessThanOrEqual(1);
      if (!metrics.stacked) {
        expect(metrics.columns).toHaveLength(3);
        expect(metrics.columns[0]!.width / metrics.width).toBeLessThan(0.3);
        for (const column of metrics.columns.slice(1))
          expect(column.width / metrics.width).toBeGreaterThan(0.2);
        await expect(table.locator('.identifier.compact')).toHaveCount(2);
        expect(await textLineCounts(table.locator('.identifier code'))).toEqual([1, 1]);
      } else {
        await expect(table.locator('li[data-row-id] dt')).toHaveCount(3);
      }
    }
  });

  test(`compact tabs and revision controls fit narrow layouts and 200 percent text (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(runDetailPath({ locale }));
    await ready(page);
    const revision = page.locator('[data-evidence-revision-select]');
    const selectorWidth = await revision.evaluate(
      (element) => element.getBoundingClientRect().width,
    );
    const panelWidth = await page
      .locator('app-evidence-panel')
      .evaluate((element) => element.clientWidth);
    expect(selectorWidth).toBeLessThan(panelWidth * 0.75);
    const tabs = page.locator('app-run-detail-page [role="tablist"]').first().getByRole('tab');
    await expect(tabs).toHaveCount(2);
    const decision = tabs.nth(1);
    await expect(decision).toHaveText(locale === 'vi' ? 'Quyết định' : 'Live decision');
    await expect(page.locator('app-decision-view #decision-title')).toHaveText(
      (await decision.getAttribute('aria-label'))!,
    );
    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      for (const fontSize of ['100%', '200%']) {
        await page.evaluate((size) => {
          document.documentElement.style.fontSize = size;
        }, fontSize);
        for (const tab of await tabs.all()) {
          expect((await tab.boundingBox())?.height).toBeGreaterThanOrEqual(44);
        }
        expect(await textLineCounts(tabs)).toEqual([1, 1]);
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth),
        ).toBeLessThanOrEqual(1);
      }
    }
  });
}
