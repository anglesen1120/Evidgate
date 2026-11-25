// @vitest-environment node
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { chromium, type Browser } from '@playwright/test';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

function stylesheet(file: string): string {
  const path = [resolve('src/styles', file), resolve('apps/web/src/styles', file)].find(existsSync);
  if (!path) throw new Error(`Cannot locate stylesheet ${file}`);
  return readFileSync(path, 'utf8');
}

const sizes = ['default', 'sm', 'icon-xs', 'icon-sm', 'icon', 'icon-lg'];
const documentHtml = `<!doctype html><style>@layer tokens, base, components;
  ${stylesheet('tokens.css')}${stylesheet('spartan-base.css')}${stylesheet('helm/button.css')}
  </style>${sizes
    .map(
      (size) =>
        `<button data-slot="button" hlmBtn aria-label="Action" class="spartan-button spartan-button-size-${size}">${size.startsWith('icon') ? '' : 'Action'}</button>`,
    )
    .join('')}`;

describe('Helm button target geometry', () => {
  let browser: Browser;
  beforeAll(async () => {
    browser = await chromium.launch({ channel: 'chrome' });
  });
  afterAll(async () => {
    await browser?.close();
  });

  it.each([
    { width: 768, hasTouch: false, heights: [32, 28, 24, 28, 32, 40] },
    { width: 767, hasTouch: false, heights: sizes.map(() => 44) },
    { width: 1280, hasTouch: true, heights: sizes.map(() => 44) },
  ])(
    'sizes text and icon targets at $width px (touch: $hasTouch)',
    async ({ width, hasTouch, heights }) => {
      const page = await browser.newPage({ viewport: { width, height: 720 }, hasTouch });
      try {
        await page.setContent(documentHtml);
        const geometry = await page.locator('button').evaluateAll((buttons) =>
          buttons.map((button) => {
            const { width, height } = button.getBoundingClientRect();
            const { fontSize, fontWeight } = getComputedStyle(button);
            return { width, height, fontSize, fontWeight };
          }),
        );
        expect(geometry.map((button) => button.height)).toEqual(heights);
        expect(geometry.slice(2).map((icon) => icon.width)).toEqual(heights.slice(2));
        expect(geometry[0]).toMatchObject({ fontSize: '13px', fontWeight: '500' });
      } finally {
        await page.close();
      }
    },
  );
});
