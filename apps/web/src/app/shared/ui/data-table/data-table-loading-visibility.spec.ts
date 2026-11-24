// @vitest-environment node
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { chromium, type Browser, type Page } from '@playwright/test';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

function stylesheet(path: string): string {
  const file = [resolve(path), resolve('apps/web', path)].find((candidate) =>
    existsSync(candidate),
  );
  if (!file) throw new Error(`Cannot locate stylesheet: ${path}`);
  return readFileSync(file, 'utf8');
}

// The busy component spec covers the delayed insertion. Chromium measures the production bar
// stylesheet here because jsdom does not lay out boxes or resolve pseudo-element animations.
describe('Table header loading bar geometry', () => {
  let browser: Browser;
  let page: Page;

  beforeAll(async () => {
    browser = await chromium.launch({ channel: 'chrome' });
    page = await browser.newPage();
  });
  afterAll(async () => browser?.close());

  it.each(['reduce', 'no-preference'] as const)(
    'does not move served rows with motion preference %s',
    async (reducedMotion) => {
      await page.emulateMedia({ reducedMotion });
      await page.setContent(`<!doctype html><style>
        ${stylesheet('src/styles/tokens.css')}
        ${stylesheet('src/app/shared/ui/states/loading-pattern.component.css')}
        .block { display: block; } .relative { position: relative; }
        </style><app-data-table class="block"><p>Records</p>
        <app-loading-pattern class="relative block"></app-loading-pattern>
        <table><tbody><tr><td>Served row</td></tr></tbody></table></app-data-table>`);
      const table = page.locator('app-data-table');
      const row = page.getByRole('row');
      const idleHost = await table.boundingBox();
      const idleRow = await row.boundingBox();
      expect(idleHost?.height).toBeGreaterThan(0);
      expect(idleRow?.height).toBeGreaterThan(0);

      await page.locator('app-loading-pattern').evaluate((host) => {
        const bar = document.createElement('span');
        bar.setAttribute('data-table-progress', '');
        host.appendChild(bar);
      });
      const bar = page.locator('[data-table-progress]');
      const track = await bar.boundingBox();
      expect(track?.height).toBeGreaterThanOrEqual(4);
      expect(track?.width).toBe(idleHost?.width);
      expect(await table.boundingBox()).toEqual(idleHost);
      expect(await row.boundingBox()).toEqual(idleRow);

      const fill = await bar.evaluate((element) => {
        const style = getComputedStyle(element, '::after');
        return { width: parseFloat(style.width), animation: style.animationName };
      });
      if (reducedMotion === 'reduce') {
        expect(fill.animation).toBe('none');
        expect(fill.width).toBe(track?.width);
      } else {
        expect(fill.animation).not.toBe('none');
        expect(fill.width).toBeGreaterThan(0);
        expect(fill.width).toBeLessThan(track?.width ?? 0);
      }
    },
  );
});
