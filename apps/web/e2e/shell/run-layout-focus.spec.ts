import { expect, test, type Page } from '@playwright/test';
import { runDetailPath } from './run-detail.support';
import { openRunFilters } from './run-list.support';
import {
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
} from './support';

// Exercise both sides of the shared table's 768 px available-width breakpoint,
// including the neighbouring viewport widths that exposed the old boundary gap.
const WIDTHS = [320, 766, 767, 768, 1280, 1440] as const;
const STACKED_MAX = 767;
const GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';

function listPath(variant: 'runs' | '250-rows'): string {
  return withVariants(
    localePath('en', `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    [GRANT, `?fixture.variant.listRuns=${variant}`],
  );
}

function detailPath(revision = 3, revisions: 'ok' | 'empty' | 'unavailable' = 'ok'): string {
  return runDetailPath({
    decision: 'evaluated-fail-missing-shard',
    attempts: 'ok',
    revisions,
    evidenceRevision: revision,
  });
}

async function navigatePin(page: Page, revision: number): Promise<void> {
  await page.evaluate((value) => {
    const url = new URL(window.location.href);
    url.searchParams.set('evidence_revision', String(value));
    window.history.pushState({}, '', url);
    window.dispatchEvent(new PopStateEvent('popstate'));
  }, revision);
  await expect
    .poll(() => new URL(page.url()).searchParams.get('evidence_revision'))
    .toBe(String(revision));
}

interface Box {
  x: number;
  width: number;
  height: number;
}

interface TableGeometry {
  narrow: boolean;
  header: Box | null;
  headerCells: Box[];
  cells: Box[];
  labels: string[];
  labelDisplays: string[];
  row: Box;
  rowOverflow: number;
  fieldOverflow: number[];
  rowCount: number;
  documentWidth: number;
}

/** Native table cells and labelled records retain the same column facts. */
async function tableGeometry(page: Page, selector = 'app-run-list-table'): Promise<TableGeometry> {
  const host = page.locator(selector);
  await page.evaluate(() => document.fonts.ready);
  await host.evaluate(
    () =>
      new Promise<void>((resolve) => {
        requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
      }),
  );
  const narrow = (await host.locator('.eg-table-narrow').count()) > 0;
  await expect(host.locator(narrow ? 'li[data-row-id]' : 'tr[data-row-id]').first()).toBeVisible();
  return host.evaluate((root, narrow): TableGeometry => {
    const row = root.querySelector(narrow ? 'li[data-row-id]' : 'tr[data-row-id]');
    if (!(row instanceof HTMLElement)) throw new Error('A served table record is required');
    const box = (element: Element): Box => {
      const rect = element.getBoundingClientRect();
      return { x: rect.x, width: rect.width, height: rect.height };
    };
    const header = root.querySelector('thead tr');
    const cells = [...row.querySelectorAll<HTMLElement>(narrow ? 'dd' : ':scope > td')];
    const labels = [...row.querySelectorAll('dt')];
    return {
      narrow,
      header: header ? box(header) : null,
      headerCells: [...root.querySelectorAll('[role="columnheader"]')].map(box),
      cells: cells.map(box),
      labels: labels.map((label) => label.textContent?.trim() ?? ''),
      labelDisplays: labels.map((label) => getComputedStyle(label).display),
      row: box(row),
      rowOverflow: row.scrollWidth - row.clientWidth,
      fieldOverflow: cells.map((cell) => cell.scrollWidth - cell.clientWidth),
      rowCount: root.querySelectorAll('[data-row-id]').length,
      documentWidth: document.documentElement.scrollWidth,
    };
  }, narrow);
}

for (const theme of THEMES) {
  for (const width of WIDTHS) {
    for (const variant of ['runs', '250-rows'] as const) {
      test(`run table column parity ${width}px ${theme} ${variant}`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 900 });
        await page.goto(listPath(variant));
        await expect(page.locator('app-run-list-page')).toHaveAttribute(
          'data-run-list-status',
          'ready',
        );
        const layout = await tableGeometry(page);
        if (width === 1440) expect(layout.narrow).toBe(false);
        expect(layout.cells).toHaveLength(7);
        expect(layout.documentWidth).toBeLessThanOrEqual(width);
        expect(layout.rowOverflow).toBeLessThanOrEqual(1);
        if (layout.narrow) {
          expect.soft(layout.header).toBeNull();
          expect.soft(layout.headerCells).toHaveLength(0);
          expect.soft(layout.labels).toHaveLength(7);
          expect.soft(layout.labels.every((label) => label.length > 0)).toBe(true);
          expect.soft(layout.labelDisplays.every((display) => display !== 'none')).toBe(true);
          expect.soft(new Set(layout.cells.map((cell) => Math.round(cell.x))).size).toBe(1);
          for (const overflow of layout.fieldOverflow) expect(overflow).toBeLessThanOrEqual(1);
        } else {
          expect.soft(layout.header?.width ?? 0, JSON.stringify(layout)).toBeGreaterThan(1);
          expect.soft(layout.headerCells).toHaveLength(7);
          expect.soft(new Set(layout.cells.map((cell) => Math.round(cell.x))).size).toBe(7);
          expect.soft(layout.row.height, JSON.stringify(layout)).toBeLessThanOrEqual(200);
          for (let column = 0; column < layout.headerCells.length; column++) {
            const header = layout.headerCells[column];
            const body = layout.cells[column];
            if (!header || !body) throw new Error('Seven paired cells are required');
            expect(Math.abs(header.x - body.x)).toBeLessThanOrEqual(1);
            expect(Math.abs(header.width - body.width)).toBeLessThanOrEqual(1);
          }
          expect(layout.labelDisplays).toHaveLength(0);
        }
        if (variant === '250-rows') {
          expect(layout.rowCount).toBe(200);
          await expect(page.locator('app-run-list-table cdk-virtual-scroll-viewport')).toHaveCount(
            0,
          );
          await expect(page.locator('app-run-list-table nav [role="status"]')).toContainText(
            '1–200',
          );
        }
      });
    }

    test(`shared table header and body align ${width}px ${theme}`, async ({ page }) => {
      await seedStoredTheme(page, theme);
      await page.setViewportSize({ width, height: 900 });
      await page.goto(localePath('en', '/dev/ui-gallery'));
      const selector = '[data-demo="table-small"]';
      const table = page.locator(selector);
      const layout = await tableGeometry(page, selector);
      if (layout.narrow) {
        expect(layout.headerCells).toHaveLength(0);
        const expectedLabels = ['Evidence identity', 'Context', 'Attempts'];
        if (width >= 768) expectedLabels.splice(1, 0, 'Mutation');
        expect(layout.cells).toHaveLength(expectedLabels.length);
        expect(layout.labels).toEqual(expectedLabels);
        for (const overflow of layout.fieldOverflow) expect(overflow).toBeLessThanOrEqual(1);
      } else {
        const geometry = () =>
          table.evaluate((root) => {
            const cells = (selector: string) =>
              [...root.querySelectorAll(selector)].map((cell) => {
                const rect = cell.getBoundingClientRect();
                return { x: rect.x, width: rect.width };
              });
            return {
              header: cells('.header-table thead tr:first-child > th'),
              body: cells('.body-table tbody tr:first-child > td'),
            };
          });
        for (const scrollTop of [0, 200]) {
          await table.locator('.scroll').evaluate((element, value) => {
            element.scrollTop = value;
          }, scrollTop);
          const metrics = await geometry();
          expect(metrics.header).toHaveLength(4);
          expect(metrics.body).toHaveLength(4);
          expect(metrics.header).toEqual(metrics.body);
        }
      }
      expect(layout.rowOverflow).toBeLessThanOrEqual(1);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
        width,
      );
    });
  }

  test(`bounded record pages preserve rows and focused controls across a layout resize ${theme}`, async ({
    page,
  }) => {
    await seedStoredTheme(page, theme);
    await page.setViewportSize({ width: STACKED_MAX, height: 900 });
    await page.goto(listPath('250-rows'));
    await expect(page.locator('app-run-list-page')).toHaveAttribute(
      'data-run-list-status',
      'ready',
    );
    const first = page.locator('app-run-list-table [data-row-id]').first();
    const layout = await tableGeometry(page);
    expect(layout.narrow).toBe(true);
    expect(layout.rowCount).toBe(200);
    const id = await first.getAttribute('data-row-id');
    await first.getByRole('link').first().focus();
    await page.setViewportSize({ width: 1440, height: 900 });
    expect((await tableGeometry(page)).narrow).toBe(false);
    await expect(first).toHaveAttribute('data-row-id', id ?? '');
    expect((await tableGeometry(page)).rowCount).toBe(200);
    await expect(first.getByRole('link').first()).toBeFocused();
    const next = page.locator('app-run-list-table nav button').last();
    await next.focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('app-run-list-table [data-row-id]')).toHaveCount(50);
    await expect(page.locator('app-run-list-table nav [role="status"]')).toContainText('201–250');
    const pageId = await first.getAttribute('data-row-id');
    const copy = first.locator('app-copy-value-button button').first();
    await copy.focus();
    await page.setViewportSize({ width: STACKED_MAX, height: 900 });
    expect((await tableGeometry(page)).narrow).toBe(true);
    expect((await tableGeometry(page)).rowCount).toBe(50);
    await expect(first).toHaveAttribute('data-row-id', pageId ?? '');
    expect((await tableGeometry(page)).rowOverflow).toBeLessThanOrEqual(1);
    await expect(first.locator('app-copy-value-button button').first()).toBeFocused();
    await page.setViewportSize({ width: 1280, height: 900 });
    await expect(first).toHaveAttribute('data-row-id', pageId ?? '');
    await expect(first.locator('app-copy-value-button button').first()).toBeFocused();
  });

  for (const revisions of ['empty', 'unavailable'] as const) {
    test(`no evidence announcement without a selected revision ${revisions} ${theme}`, async ({
      page,
    }) => {
      await seedStoredTheme(page, theme);
      const address = new URL(detailPath(3, revisions), 'http://fixtures.test');
      address.searchParams.delete('evidence_revision');
      address.searchParams.set('fixture.variant.getRunDecision', 'ok');
      await page.goto(`${address.pathname}${address.search}`);
      await expect(page.locator('app-run-detail-page')).toHaveAttribute(
        'data-run-detail-status',
        'ready',
      );
      if (revisions === 'empty') {
        await expect(page.locator('[data-evidence-empty]')).toBeVisible();
      } else {
        await expect(page.locator('app-evidence-panel app-error-pattern')).toBeVisible();
      }
      await expect(page.locator('[data-evidence-revision-select]')).toHaveCount(0);
      await expect(page.locator('[data-evidence-announcement]')).toHaveText('');
      await page.getByRole('tab', { name: 'Latest live decision' }).click();
      await expect(page.locator('[data-evidence-announcement]')).toHaveText('');
    });
  }

  test(`an invalid initial evidence pin is normalized without an update announcement ${theme}`, async ({
    page,
  }) => {
    await seedStoredTheme(page, theme);
    await page.goto(detailPath(0));
    await expect(page.locator('[data-evidence-revision-select]')).toContainText('Revision 2');
    await expect(page.locator('[data-evidence-announcement]')).toHaveText('');
  });

  test(`evidence announcements preserve order without duplicate mutations ${theme}`, async ({
    page,
  }) => {
    await seedStoredTheme(page, theme);
    await page.goto(detailPath());
    const region = page.locator('[data-evidence-announcement]');
    await expect(page.locator('[data-evidence-revision-select]')).toContainText('Revision 3');
    await expect(region).toHaveText('');
    await expect(region).toHaveCount(1);
    await region.evaluate((element) => {
      element.setAttribute('data-test-announcements', '');
      new MutationObserver(() => {
        const text = element.textContent?.trim() ?? '';
        if (text === '') return;
        const before = element.getAttribute('data-test-announcements') ?? '';
        element.setAttribute('data-test-announcements', before ? `${before}\n${text}` : text);
      }).observe(element, { childList: true, characterData: true, subtree: true });
    });
    const messages: string[] = [];
    for (const selected of [2, 1, 3]) {
      await page.getByRole('tab', { name: 'Latest live decision' }).click();
      await navigatePin(page, selected);
      const message =
        selected === 3
          ? 'Viewing evidence revision 3'
          : `Viewing evidence revision ${selected}. Newer revision 3 is available.`;
      messages.push(message);
      await expect(region).toHaveText(message);
      await expect(page.locator('app-evidence-panel').locator('..')).toHaveAttribute('hidden', '');
      await expect(region).toHaveAttribute('data-test-announcements', messages.join('\n'));
      await page.getByRole('tab', { name: 'Evidence', exact: true }).click();
      await expect(page.locator('[data-evidence-revision-select]')).toContainText(
        `Revision ${selected}`,
      );
      const duplicateMessages = await page
        .locator(
          'app-evidence-panel [role="status"], app-evidence-panel [role="alert"], app-evidence-panel [aria-live]:not([aria-live="off"])',
        )
        .evaluateAll((elements) =>
          elements.filter((element) =>
            /Viewing evidence revision|newest revision is|A newer revision exists/.test(
              element.textContent ?? '',
            ),
          ),
        );
      expect(duplicateMessages).toEqual([]);
      await navigatePin(page, selected);
      await expect(region).toHaveAttribute('data-test-announcements', messages.join('\n'));
    }
  });

  test(`Tab and Shift Tab visit only the selected panel and its controls ${theme}`, async ({
    page,
  }) => {
    await seedStoredTheme(page, theme);
    await page.goto(detailPath(3));
    const evidence = page.getByRole('tab', { name: 'Evidence', exact: true });
    const decision = page.getByRole('tab', { name: 'Latest live decision' });
    await expect(page.locator('[data-evidence-revision-select]')).toContainText('Revision 3');
    const evidencePanel = page.getByRole('tabpanel');
    await evidence.focus();
    await page.keyboard.press('Tab');
    await expect(evidencePanel).toBeFocused();
    await expect(evidencePanel).toHaveAttribute('tabindex', '0');
    await page.keyboard.press('Tab');
    await expect(page.locator('[data-evidence-revision-select]')).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(evidencePanel).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(evidence).toBeFocused();
    await page.keyboard.press('ArrowRight');
    await expect(decision).toBeFocused();
    await expect(decision).toHaveAttribute('aria-selected', 'true');
    const decisionPanel = page.getByRole('tabpanel');
    await expect(decisionPanel).toHaveAttribute('tabindex', '0');
    await expect(page.locator('[role="tabpanel"][hidden]')).toHaveAttribute('tabindex', '-1');
    const mismatch = page.locator('[data-decision-mismatch] button');
    await expect(mismatch).toBeVisible();
    await page.keyboard.press('Tab');
    await expect(decisionPanel).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(mismatch).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(decisionPanel).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(decision).toBeFocused();
    await page.keyboard.press('Home');
    await expect(evidence).toBeFocused();
    await expect(evidence).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('Tab');
    await expect(page.getByRole('tabpanel')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.locator('[data-evidence-revision-select]')).toBeFocused();
  });
}

test('a loading reference filter keeps the fitted run records mounted', async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 900 });
  await page.goto(listPath('250-rows'));
  const list = page.locator('app-run-list-page');
  await expect(list).toHaveAttribute('data-run-list-status', 'ready');
  const table = page.locator('app-run-list-table app-data-table');
  await page.evaluate(() => document.fonts.ready);
  await expect(table.locator('.eg-table-narrow')).toBeVisible();
  await expect(table.locator('.eg-table-record').first()).toBeVisible();
  await table.evaluate((host) => {
    host.setAttribute('data-test-native-insertions', '0');
    host.setAttribute('data-test-saw-loading', 'false');
    new MutationObserver((mutations) => {
      let count = Number(host.getAttribute('data-test-native-insertions'));
      for (const mutation of mutations) {
        for (const added of mutation.addedNodes) {
          if (!(added instanceof Element)) continue;
          count += added.matches('[role="table"]') ? 1 : 0;
          count += added.querySelectorAll('[role="table"]').length;
        }
      }
      host.setAttribute('data-test-native-insertions', String(count));
    }).observe(host, { childList: true, subtree: true });
    const page = host.closest('app-run-list-page');
    if (!page) throw new Error('Missing owning run list');
    new MutationObserver((mutations) => {
      if (
        page.getAttribute('data-run-list-status') === 'loading' ||
        mutations.some((mutation) => mutation.oldValue === 'loading')
      ) {
        host.setAttribute('data-test-saw-loading', 'true');
      }
    }).observe(page, {
      attributes: true,
      attributeFilter: ['data-run-list-status'],
      attributeOldValue: true,
    });
  });
  await openRunFilters(page);
  await page.locator('#run-ref').fill('feature/batch-000');
  await page.locator('[data-run-apply]').click();
  await expect.poll(() => new URL(page.url()).searchParams.get('ref')).toBe('feature/batch-000');
  await expect(list).toHaveAttribute('data-run-list-status', 'ready');
  await expect(table).toHaveAttribute('data-test-saw-loading', 'true');
  await expect(table.locator('.eg-table-narrow')).toBeVisible();
  await expect(table).toHaveAttribute('data-test-native-insertions', '0');
});
