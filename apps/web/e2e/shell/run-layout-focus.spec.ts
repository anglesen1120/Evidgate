import { expect, test, type Page } from '@playwright/test';
import runDetail from '../../../../fixtures/web-api/run-detail/input.json';
import {
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
} from './support';

const WIDTHS = [320, 1280] as const;
const GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';

function listPath(variant: 'runs' | '250-rows'): string {
  return withVariants(
    localePath('en', `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    [GRANT, `?fixture.variant.listRuns=${variant}`],
  );
}

function detailPath(revision = 3, revisions: 'ok' | 'empty' | 'unavailable' = 'ok'): string {
  return withVariants(
    localePath(
      'en',
      `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${runDetail.body.run_id}`,
    ),
    [
      GRANT,
      '?fixture.variant.getRun=detail-ready',
      '?fixture.variant.listRunAttempts=ok',
      '?fixture.variant.getRunDecision=evaluated-fail-missing-shard',
      `?fixture.variant.listEvidenceRevisions=${revisions}`,
      `?evidence_revision=${revision}`,
    ],
  );
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
        const layout = await page.locator('app-run-list-table').evaluate((table) => {
          const header = table.querySelector('table');
          const headerCells = [...table.querySelectorAll('[role="columnheader"]')];
          const row = table.querySelector('[data-run-row]');
          if (!(header instanceof HTMLElement) || !(row instanceof HTMLElement)) {
            throw new Error('Run table header and body are required');
          }
          const cells = [...row.querySelectorAll('[data-label]')];
          const box = (element: Element) => {
            const rect = element.getBoundingClientRect();
            return { x: rect.x, width: rect.width, height: rect.height };
          };
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
            documentWidth: document.documentElement.scrollWidth,
          };
        });
        expect(layout.headerCells).toHaveLength(7);
        expect(layout.cells).toHaveLength(7);
        expect(layout.documentWidth).toBeLessThanOrEqual(width);
        if (width <= 767) {
          expect.soft(layout.header.width, JSON.stringify(layout)).toBeLessThanOrEqual(1);
          expect.soft(layout.headerPosition).toBe('absolute');
          expect.soft(layout.cellDisplays).toEqual(Array(7).fill('grid'));
          expect.soft(layout.labels.every((label) => label !== 'none')).toBe(true);
          expect.soft(new Set(layout.cells.map((cell) => Math.round(cell.x))).size).toBe(1);
          if (variant === '250-rows') {
            expect.soft(layout.row.height, JSON.stringify(layout)).toBeGreaterThanOrEqual(792);
            expect.soft(layout.probeHeight).toBeGreaterThanOrEqual(792);
          }
        } else {
          for (let column = 0; column < layout.headerCells.length; column++) {
            const headerCell = layout.headerCells[column];
            const bodyCell = layout.cells[column];
            if (!headerCell || !bodyCell) throw new Error('Seven paired cells are required');
            expect(Math.abs(headerCell.x - bodyCell.x)).toBeLessThanOrEqual(1);
            expect(Math.abs(headerCell.width - bodyCell.width)).toBeLessThanOrEqual(1);
          }
          expect(layout.labelDisplays.every((display) => display === 'none')).toBe(true);
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
