import { expect, test, type Page } from '@playwright/test';
import { runDetailPath } from './run-detail.support';
import {
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
} from './support';

// 766 and 767 are the last stacked widths and 768 the first table width: a one-pixel gap between
// the two halves of the breakpoint is exactly what the boundary cases exist to catch.
const WIDTHS = [320, 766, 767, 768, 1280] as const;
/** Up to and including this width the run table stacks; from 768 px every part of it is a table. */
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

/** A measured rectangle, in CSS pixels, relative to the viewport. */
interface Box {
  x: number;
  width: number;
  height: number;
}

/** Everything the browser answers about the run table at the current viewport width. */
interface TableGeometry {
  header: Box;
  headerPosition: string;
  headerCells: Box[];
  cells: Box[];
  cellDisplays: string[];
  labels: string[];
  labelDisplays: string[];
  row: Box;
  /** The height the component measures its virtual row size from. */
  probeHeight: number;
  /** The whole-list size the virtual scroller asserts, or null without a virtual viewport. */
  spacerHeight: number | null;
  rowCount: number;
  documentWidth: number;
}

async function tableGeometry(page: Page): Promise<TableGeometry> {
  return page.locator('app-run-list-table').evaluate((table): TableGeometry => {
    const header = table.querySelector('table');
    const headerCells = [...table.querySelectorAll('[role="columnheader"]')];
    const row = table.querySelector('[data-run-row]');
    if (!(header instanceof HTMLElement) || !(row instanceof HTMLElement)) {
      throw new Error('Run table header and body are required');
    }
    const cells = [...row.querySelectorAll('[data-label]')];
    const box = (element: Element): Box => {
      const rect = element.getBoundingClientRect();
      return { x: rect.x, width: rect.width, height: rect.height };
    };
    const spacer = table.querySelector('cdk-virtual-scroll-viewport .cdk-virtual-scroll-spacer');
    return {
      header: box(header),
      headerPosition: getComputedStyle(header).position,
      headerCells: headerCells.map(box),
      cells: cells.map(box),
      cellDisplays: cells.map((cell) => getComputedStyle(cell).display),
      labels: cells.map((cell) => getComputedStyle(cell, '::before').content),
      labelDisplays: cells.map((cell) => getComputedStyle(cell, '::before').display),
      row: box(row),
      probeHeight: Number.parseFloat(
        getComputedStyle(table.querySelector('.row-probe') ?? row).height,
      ),
      // The spacer is the size the virtual scroller asserts the whole list has: itemSize × rows.
      spacerHeight:
        spacer instanceof HTMLElement ? Number.parseFloat(getComputedStyle(spacer).height) : null,
      rowCount:
        Number(table.querySelector('[role="table"]')?.getAttribute('aria-rowcount') ?? '1') - 1,
      documentWidth: document.documentElement.scrollWidth,
    };
  });
}

/** How far the virtual scroller's total size is from rows × the row height the browser renders. */
function virtualSizeDrift(layout: TableGeometry): number {
  return Math.abs((layout.spacerHeight ?? 0) - layout.rowCount * layout.probeHeight);
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
        const row = page.locator('app-run-list-table [data-run-row]').first();
        await expect(row).toBeVisible();
        const layout = await tableGeometry(page);
        expect(layout.headerCells).toHaveLength(7);
        expect(layout.cells).toHaveLength(7);
        expect(layout.documentWidth).toBeLessThanOrEqual(width);
        if (width <= STACKED_MAX) {
          // One layout per width: the hidden header, the stacked cells, the collapsed row grid and
          // the row height the virtual viewport measures all have to agree on the same answer.
          expect.soft(layout.header.width, JSON.stringify(layout)).toBeLessThanOrEqual(1);
          expect.soft(layout.headerPosition).toBe('absolute');
          expect.soft(layout.cellDisplays).toEqual(Array(7).fill('grid'));
          expect.soft(layout.labels.every((label) => label !== 'none')).toBe(true);
          expect.soft(new Set(layout.cells.map((cell) => Math.round(cell.x))).size).toBe(1);
          if (variant === '250-rows') {
            expect.soft(layout.row.height, JSON.stringify(layout)).toBeGreaterThanOrEqual(792);
            expect.soft(layout.probeHeight).toBeGreaterThanOrEqual(792);
            expect.soft(Math.abs(layout.row.height - layout.probeHeight)).toBeLessThanOrEqual(1);
          }
        } else {
          expect.soft(layout.header.width, JSON.stringify(layout)).toBeGreaterThan(1);
          expect.soft(layout.cellDisplays.every((display) => display !== 'grid')).toBe(true);
          expect.soft(new Set(layout.cells.map((cell) => Math.round(cell.x))).size).toBe(7);
          if (variant === '250-rows') {
            // The measured row size is the fixed table row, not the stacked card height.
            expect.soft(layout.row.height, JSON.stringify(layout)).toBeLessThanOrEqual(200);
            expect.soft(Math.abs(layout.row.height - layout.probeHeight)).toBeLessThanOrEqual(1);
          }
          for (let column = 0; column < layout.headerCells.length; column++) {
            const headerCell = layout.headerCells[column];
            const bodyCell = layout.cells[column];
            if (!headerCell || !bodyCell) throw new Error('Seven paired cells are required');
            expect(Math.abs(headerCell.x - bodyCell.x)).toBeLessThanOrEqual(1);
            expect(Math.abs(headerCell.width - bodyCell.width)).toBeLessThanOrEqual(1);
          }
          expect(layout.labelDisplays.every((display) => display === 'none')).toBe(true);
        }
        if (variant === '250-rows') {
          // The viewport sizes the whole list from the row it measured: a hardcoded or stale item
          // size leaves this spacer wrong even while the rendered row looks correct.
          expect.soft(layout.spacerHeight, JSON.stringify(layout)).not.toBeNull();
          expect.soft(virtualSizeDrift(layout)).toBeLessThanOrEqual(2);
        }
      });
    }

    test(`shared table header and body align ${width}px ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(localePath('en', '/dev/ui-gallery'));
      await page.evaluate((value) => {
        document.documentElement.dataset['theme'] = value;
      }, theme);
      const table = page.locator('[data-demo="table-small"]');
      await expect(table.locator('.body-table tr').first()).toBeVisible();
      const geometry = async () =>
        table.evaluate((root) => {
          const cells = (selector: string) =>
            [...root.querySelectorAll(`${selector} tr:first-child > .cell`)].map((cell) => {
              const rect = cell.getBoundingClientRect();
              return { x: rect.x, width: rect.width };
            });
          return { header: cells('.header-table'), body: cells('.body-table') };
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
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
        width,
      );
    });
  }

  test(`the virtual row size follows the breakpoint across a resize ${theme}`, async ({ page }) => {
    await seedStoredTheme(page, theme);
    await page.setViewportSize({ width: STACKED_MAX, height: 900 });
    await page.goto(listPath('250-rows'));
    await expect(page.locator('app-run-list-page')).toHaveAttribute(
      'data-run-list-status',
      'ready',
    );
    await expect(page.locator('app-run-list-table [data-run-row]').first()).toBeVisible();
    // The whole-list size is the only observable of the item size the viewport actually uses, and
    // it must be re-measured when the row changes height, not only read once at startup.
    await expect
      .poll(async () => virtualSizeDrift(await tableGeometry(page)))
      .toBeLessThanOrEqual(2);
    expect((await tableGeometry(page)).probeHeight).toBeGreaterThanOrEqual(792);
    await page.setViewportSize({ width: STACKED_MAX + 1, height: 900 });
    await expect.poll(async () => (await tableGeometry(page)).probeHeight).toBeLessThanOrEqual(200);
    await expect
      .poll(async () => virtualSizeDrift(await tableGeometry(page)))
      .toBeLessThanOrEqual(2);
    await page.setViewportSize({ width: STACKED_MAX, height: 900 });
    await expect
      .poll(async () => (await tableGeometry(page)).probeHeight)
      .toBeGreaterThanOrEqual(792);
    await expect
      .poll(async () => virtualSizeDrift(await tableGeometry(page)))
      .toBeLessThanOrEqual(2);
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
