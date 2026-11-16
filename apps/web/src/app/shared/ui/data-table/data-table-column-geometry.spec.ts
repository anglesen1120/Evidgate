// @vitest-environment node
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { chromium, type Browser, type Page } from '@playwright/test';
import postcss from 'postcss';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { HlmTableColumnKind, HlmTableColumnRole } from '@evidgate/ui/table';

import { middleTruncate } from '../values/middle-truncate';

interface SpecimenColumn {
  readonly role: HlmTableColumnRole;
  readonly kind?: HlmTableColumnKind;
  readonly primary?: boolean;
  readonly header: string;
  readonly value: string;
}

function productionRules(): string {
  const paths = [
    resolve('src/styles/helm/table.css'),
    resolve('apps/web/src/styles/helm/table.css'),
  ];
  const path = paths.find((candidate) => existsSync(candidate));
  if (!path) throw new Error('Cannot locate the shared table stylesheet');
  const rules: string[] = [];
  postcss.parse(readFileSync(path, 'utf8')).walkRules((rule) => {
    if (
      rule.parent?.type === 'atrule' &&
      rule.parent.name === 'layer' &&
      rule.selector.startsWith('.eg-table-layout')
    )
      rules.push(rule.toString());
  });
  return rules.join('\n');
}

function columns(locale: string): readonly SpecimenColumn[] {
  const vi = locale === 'vi';
  return [
    {
      role: 'identity',
      kind: 'identifier',
      primary: true,
      header: 'Run id',
      value: middleTruncate('018f0000-0000-7000-8000-000000000081'),
    },
    { role: 'status', primary: true, header: 'State', value: vi ? 'Hoàn tất' : 'Complete' },
    {
      role: 'status',
      primary: true,
      header: vi ? 'Bằng chứng' : 'Evidence',
      value: vi ? 'Không khả dụng' : 'Unavailable',
    },
    { role: 'status', primary: true, header: 'Decision', value: 'INCONCLUSIVE' },
    { role: 'identity', kind: 'branch', header: 'Ref', value: 'feature/source-branch-name' },
    { role: 'text', header: 'Scope', value: vi ? 'Lần chạy gốc' : 'Original run' },
    { role: 'date', header: 'Created (UTC)', value: 'Nov 14, 2026 12:34 UTC' },
    { role: 'number', header: 'Duration', value: '123,456,789 ms' },
    { role: 'actions', header: 'Actions', value: vi ? 'Chi tiết' : 'Details' },
  ];
}

function contents(column: SpecimenColumn, locale: string): string {
  if (column.role === 'identity' && column.primary) {
    return `<div class="identifier"><a href="#inspect">${column.value}</a></div>`;
  }
  if (column.role === 'status') {
    const hint =
      locale === 'vi'
        ? 'Mở lần chạy để xem tính đầy đủ của bằng chứng.'
        : 'Open the run for evidence completeness.';
    return `<div class="facts"><span class="chip"><i></i><span>${column.value}</span></span><span>${hint}</span></div>`;
  }
  if (column.role === 'actions') return `<button type="button">${column.value}</button>`;
  return column.value;
}

function specimen(locale: string, identifierKind: HlmTableColumnKind = 'identifier'): string {
  const cells = columns(locale).map((column) =>
    column.role === 'identity' && column.primary ? { ...column, kind: identifierKind } : column,
  );
  const attributes = (column: SpecimenColumn): string =>
    `data-column-role="${column.role}" data-column-kind="${column.kind ?? 'name'}" ` +
    `data-column-priority="${column.primary ? 'primary' : 'secondary'}" ` +
    `data-column-overflow="${column.role === 'identity' || column.role === 'status' ? 'truncate' : 'wrap'}"`;
  const headers = cells
    .map((column) => `<th class="spartan-table-head" ${attributes(column)}>${column.header}</th>`)
    .join('');
  const tracks = cells.map((column) => `<col ${attributes(column)} />`).join('');
  const values = cells
    .map(
      (column) =>
        `<td class="spartan-table-cell" ${attributes(column)}><app-data-table-cell class="eg-table-value">${contents(column, locale)}</app-data-table-cell></td>`,
    )
    .join('');
  return `<div class="scroll"><table class="eg-table-layout"><colgroup>${tracks}</colgroup><thead><tr>${headers}</tr></thead><tbody><tr>${values}</tr></tbody></table></div>`;
}

function nativeScanSpecimen(): string {
  const roles = ['identity', 'text', 'status', 'number', 'number'];
  const headers = ['Execution', 'Raw status', 'Normalised status', 'Duration', 'Artifacts'];
  const attributes = roles.map(
    (role, index) =>
      `data-column-role="${role}" data-column-kind="${index === 0 ? 'identifier' : 'name'}" ` +
      `data-column-priority="${index === 0 ? 'primary' : 'secondary'}" ` +
      `data-column-overflow="${index === 0 ? 'truncate' : 'wrap'}"`,
  );
  const head = headers
    .map((label, index) => `<th class="spartan-table-head" ${attributes[index]}>${label}</th>`)
    .join('');
  const fullValue = 'execution-0123456789abcdef-0123456789abcdef-0123456789abcdef';
  const values = [
    `<div class="scan-identity"><bdi><app-data-table-value class="eg-table-value"><span class="value">${fullValue}</span></app-data-table-value></bdi><button type="button">Copy</button><span>Attempt 1</span></div>`,
    'PASSED',
    '<span class="chip">Passed</span>',
    '120 ms',
    '2',
  ];
  const body = values
    .map((value, index) => {
      const tag = index === 0 ? 'th' : 'td';
      return `<${tag} class="spartan-table-cell" ${index === 0 ? 'scope="row"' : ''} ${attributes[index]}>${value}</${tag}>`;
    })
    .join('');
  return `<div class="scan-scroll"><table class="eg-table-layout scan-table"><thead><tr>${head}</tr></thead><tbody><tr>${body}</tr></tbody></table></div>`;
}

async function geometry(page: Page) {
  return page.locator('.scroll').evaluate((scroll) => {
    const headings = [...scroll.querySelectorAll('th')].map((cell) => cell.getBoundingClientRect());
    const rootStyle = getComputedStyle(document.documentElement);
    const inset = Number.parseFloat(rootStyle.getPropertyValue('--eg-space-4'));
    const touch = Number.parseFloat(rootStyle.getPropertyValue('--eg-control-touch'));
    const chBudgets: Record<string, number> = {
      identity: 12,
      text: 12,
      status: 20,
      number: 8,
      date: 18,
    };
    const cells = [...scroll.querySelectorAll('td')].map((cell, index) => {
      const style = getComputedStyle(cell);
      const probe = document.createElement('span');
      probe.style.cssText = `position:absolute;visibility:hidden;font:${style.font}`;
      probe.textContent = '0';
      cell.append(probe);
      const ch = probe.getBoundingClientRect().width;
      probe.remove();
      const role = cell.dataset['columnRole'] ?? '';
      const primaryId =
        cell.dataset['columnKind'] === 'identifier' && cell.dataset['columnPriority'] === 'primary';
      const content = primaryId ? 32 : (chBudgets[role] ?? 0);
      const padding =
        role === 'status' ? inset * 3 : role === 'text' || role === 'number' ? 0 : inset * 2;
      const minimum = role === 'actions' ? touch + inset * 2 : ch * content + padding;
      const bounds = cell.getBoundingClientRect();
      const heading = headings[index]!;
      return {
        role,
        width: bounds.width,
        minimum,
        alignment: Math.max(Math.abs(heading.x - bounds.x), Math.abs(heading.width - bounds.width)),
      };
    });
    const identifier = scroll.querySelector<HTMLElement>('.identifier')!;
    const link = identifier.querySelector('a')!;
    const range = document.createRange();
    range.selectNodeContents(link);
    const text = range.getBoundingClientRect();
    const bounds = identifier.getBoundingClientRect();
    const cellBounds = identifier.closest('td')!.getBoundingClientRect();
    return {
      cells,
      identifierFits:
        text.left >= bounds.left - 1 &&
        text.right <= bounds.right + 1 &&
        text.left >= cellBounds.left - 1 &&
        text.right <= cellBounds.right + 1,
      rowHeight: scroll.querySelector('tbody tr')!.getBoundingClientRect().height,
      headerHeight: scroll.querySelector('thead tr')!.getBoundingClientRect().height,
      localScroll: scroll.scrollWidth > scroll.clientWidth,
      documentFits: document.documentElement.scrollWidth <= document.documentElement.clientWidth,
    };
  });
}

// Measure the production role rules in Chrome: jsdom cannot detect a 34px status column.
// This follows the existing isolated CSS geometry tests and needs no fixture build/server.
describe('shared table content-class minimum widths', () => {
  let browser: Browser;
  let css: string;

  beforeAll(async () => {
    css = productionRules();
    browser = await chromium.launch({ channel: 'chrome' });
  });
  afterAll(async () => {
    await browser?.close();
  });

  const scenarios = ['en', 'vi'].flatMap((locale) =>
    [900, 1280].flatMap((width) => [1, 2].map((scale) => ({ locale, width, scale }))),
  );
  it.each(scenarios)(
    'keeps $locale role content intact at $width px and $scale× text',
    async ({ locale, width, scale }) => {
      const page = await browser.newPage({ viewport: { width, height: 900 } });
      try {
        await page.setContent(`<!doctype html><html lang="${locale}"><head><style>
        :root { --eg-space-3:8px; --eg-space-4:12px; --eg-control-touch:44px;
          --eg-row-default:44px; --eg-font-mono:ui-monospace,monospace; }
        body { margin:0; font:${14 * scale}px/${20 * scale}px Arial,sans-serif; }
        .scroll { inline-size:calc(100vw - 16rem); max-inline-size:100%; overflow:auto; }
        table { inline-size:100%; border-collapse:collapse; font:inherit; }
        th,td { box-sizing:border-box; }
        .eg-table-value { display:block; min-inline-size:0; }
        .identifier,.facts { display:grid; min-inline-size:0; gap:8px; }
        .identifier a { display:inline-flex; white-space:nowrap; }
        .chip { display:inline-flex; min-inline-size:0; max-inline-size:100%;
          box-sizing:border-box; gap:4px; padding:2px 8px; }
        .chip i { inline-size:12px; flex-shrink:0; }
        .chip span { min-inline-size:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
        button { min-inline-size:var(--eg-control-touch); font:inherit; }
        ${css}
      </style></head><body>${specimen(locale)}</body></html>`);
        const measured = await geometry(page);
        for (const cell of measured.cells) {
          expect(cell.width, JSON.stringify(cell)).toBeGreaterThanOrEqual(cell.minimum - 1);
          expect(cell.alignment, JSON.stringify(cell)).toBeLessThanOrEqual(1);
        }
        expect(measured.identifierFits).toBe(true);
        expect(measured.headerHeight).toBeLessThanOrEqual(88);
        expect(measured.rowHeight).toBeLessThanOrEqual(200);
        expect(measured.localScroll).toBe(true);
        expect(measured.documentFits).toBe(true);
      } finally {
        await page.close();
      }
    },
  );

  it.each([14, 16])(
    'fits a five-column recoverable native scan table at %ipx text without weakening status floors',
    async (fontSize) => {
      const page = await browser.newPage({ viewport: { width: 768, height: 900 } });
      try {
        await page.setContent(`<!doctype html><style>
        :root { --eg-space-3:8px; --eg-space-4:12px; --eg-control-touch:44px;
          --eg-row-default:44px; --eg-font-mono:ui-monospace,monospace; }
        body { margin:0; font:${fontSize}px/1.5 Arial,sans-serif; }
        table { inline-size:100%; border-collapse:collapse; font:inherit; }
        th,td { box-sizing:border-box; }
        .scan-scroll { inline-size:624px; max-inline-size:100%; overflow:auto; }
        .scan-identity { display:grid; min-inline-size:0; gap:8px; }
        bdi,.eg-table-value,.value { display:block; min-inline-size:0; }
        .value { overflow:hidden; white-space:nowrap; text-overflow:ellipsis; }
        .chip { display:inline-flex; white-space:nowrap; padding:2px 8px; }
        button { justify-self:start; }
        ${css}
        .scan-table.eg-table-layout { min-inline-size:0; }
        .scan-table [data-column-role] { white-space:normal; overflow-wrap:anywhere; }
      </style>${nativeScanSpecimen()}`);
        const measured = await page.locator('.scan-scroll').evaluate((scroll) => {
          const table = scroll.querySelector('table')!;
          const status = scroll.querySelector<HTMLElement>('td[data-column-role="status"]')!;
          const preview = scroll.querySelector<HTMLElement>('.value')!;
          const recoverable = scroll.querySelector<HTMLElement>('.eg-table-value')!;
          const cells = [...scroll.querySelectorAll('tbody th,tbody td')];
          const headings = [...scroll.querySelectorAll('thead th')];
          return {
            overflow: scroll.scrollWidth - scroll.clientWidth,
            tableWidth: table.getBoundingClientRect().width,
            statusWidth: status.getBoundingClientRect().width,
            statusFloor: Number.parseFloat(getComputedStyle(status).minInlineSize),
            displays: cells.map((cell) => getComputedStyle(cell).display),
            alignment: cells.map((cell, index) =>
              Math.abs(cell.getBoundingClientRect().x - headings[index]!.getBoundingClientRect().x),
            ),
            completeValue: preview.textContent,
            reallyClipped: preview.scrollWidth > preview.clientWidth,
            intrinsicContainment: getComputedStyle(recoverable).contain,
          };
        });
        expect(measured.overflow, JSON.stringify(measured)).toBeLessThanOrEqual(1);
        expect(measured.tableWidth).toBeLessThanOrEqual(625);
        expect(measured.statusWidth).toBeGreaterThanOrEqual(measured.statusFloor - 1);
        expect(measured.displays).toEqual(Array(5).fill('table-cell'));
        expect(Math.max(...measured.alignment)).toBeLessThanOrEqual(1);
        expect(measured.completeValue).toBe(
          'execution-0123456789abcdef-0123456789abcdef-0123456789abcdef',
        );
        expect(measured.reallyClipped).toBe(true);
        expect(measured.intrinsicContainment).toBe('inline-size');
      } finally {
        await page.close();
      }
    },
  );

  it.each([14, 28])(
    'does not size-contain a custom identity renderer at %ipx text',
    async (fontSize) => {
      const page = await browser.newPage({ viewport: { width: 900, height: 900 } });
      try {
        await page.setContent(`<!doctype html><style>
        :root { --eg-space-3:8px; --eg-space-4:12px; --eg-control-touch:44px;
          --eg-row-default:44px; --eg-font-mono:ui-monospace,monospace; }
        body { margin:0; font:${fontSize}px/1.5 Arial,sans-serif; }
        table { inline-size:100%; border-collapse:collapse; font:inherit; }
        th,td { box-sizing:border-box; }
        .scroll { inline-size:768px; max-inline-size:100%; overflow:auto; }
        .eg-table-value { display:block; min-inline-size:0; }
        .identifier { display:grid; min-inline-size:max-content; }
        .identifier a { display:inline-flex; font-family:var(--eg-font-mono); white-space:nowrap; }
        ${css}
      </style>${specimen('en', 'name')}`);
        const measured = await geometry(page);
        const containment = await page
          .locator('app-data-table-cell')
          .first()
          .evaluate((element) => getComputedStyle(element).contain);
        expect(containment).toBe('none');
        expect(measured.identifierFits).toBe(true);
        expect(Math.max(...measured.cells.map((cell) => cell.alignment))).toBeLessThanOrEqual(1);
        for (const status of measured.cells.filter((cell) => cell.role === 'status')) {
          expect(status.width).toBeGreaterThanOrEqual(status.minimum - 1);
        }
        expect(measured.documentFits).toBe(true);
      } finally {
        await page.close();
      }
    },
  );
});
