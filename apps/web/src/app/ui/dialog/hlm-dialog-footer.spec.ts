// @vitest-environment node
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { chromium, type Browser, type Page } from '@playwright/test';
import postcss from 'postcss';
import { compile } from 'tailwindcss';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

function source(path: string): string {
  const paths = [resolve(path), resolve('apps/web', path)];
  const file = paths.find((candidate) => existsSync(candidate));
  if (!file) throw new Error(`Cannot locate dialog source: ${path}`);
  return readFileSync(file, 'utf8');
}

const footerSource = source('src/app/ui/dialog/lib/hlm-dialog-footer.ts');
const footerClasses = footerSource.match(/classes\(\(\)\s*=>\s*'([^']+)'/u)?.[1];
if (!footerClasses) throw new Error('Expected the shared dialog footer class recipe');
const footerRecipe = postcss.parse(source('src/styles/helm/dialog.css'));
const footerRules: string[] = [];
footerRecipe.walkRules('.spartan-dialog-footer', (rule) => {
  footerRules.push(rule.toString());
});

const cases = [
  { locale: 'en', cancel: 'Cancel', commit: 'Save changes', variant: 'default' },
  {
    locale: 'vi',
    cancel: 'Huỷ và quay lại danh sách thành viên',
    commit: 'Xoá thành viên khỏi tổ chức đã chọn',
    variant: 'destructive',
  },
];
const widths = [320, 390, 639, 640, 1440];
const scenarios = ['light', 'dark'].flatMap((theme) =>
  cases.flatMap((copy) => widths.map((width) => ({ theme, ...copy, width }))),
);

// Compile the production recipe rather than approximating flex or media queries in jsdom.
// This isolated CSS specimen needs no application build, backend or shared fixture server.
describe('HlmDialogFooter responsive action order', () => {
  let browser: Browser;
  let page: Page;
  let css: string;

  beforeAll(async () => {
    expect(footerRules).toHaveLength(1);
    const require = createRequire(import.meta.url);
    const theme = readFileSync(require.resolve('tailwindcss/theme.css'), 'utf8');
    const compiler = await compile(`${theme}\n${footerRules.join('\n')}\n@tailwind utilities;`);
    css = compiler.build(footerClasses.split(/\s+/u));
    browser = await chromium.launch({ channel: 'chrome' });
    // One page for the file: a page per scenario cost ~100 ms each; each case still replaces the
    // whole document and sets its own viewport.
    page = await browser.newPage();
  });

  afterAll(async () => {
    await page?.close();
    await browser?.close();
  });

  it.each(scenarios)(
    'keeps $locale $variant DOM, visual and keyboard order at $width px in $theme',
    async ({ theme, locale, cancel, commit, variant, width }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.setContent(`
          <!doctype html><html lang="${locale}" data-theme="${theme}">
            <head><style>${css}</style></head>
            <body><footer class="${footerClasses}">
              <button type="button" data-variant="outline">${cancel}</button>
              <button type="button" data-variant="${variant}">${commit}</button>
            </footer></body>
          </html>
        `);
      const actions = page.getByRole('button');
      const domOrder = await actions.allTextContents();
      const geometry = await actions.evaluateAll((elements) =>
        elements.map((element) => {
          const { top, left } = element.getBoundingClientRect();
          return { label: element.textContent, top, left };
        }),
      );
      const visualOrder = [...geometry]
        .sort((a, b) => a.top - b.top || a.left - b.left)
        .map((action) => action.label);

      expect(domOrder).toEqual([cancel, commit]);
      expect(visualOrder).toEqual(domOrder);
      const direction = await page.locator('footer').evaluate((footer) => {
        return getComputedStyle(footer).flexDirection;
      });
      expect(direction).toBe(width < 640 ? 'column' : 'row');
      await page.keyboard.press('Tab');
      expect(await actions.nth(0).evaluate((action) => action === document.activeElement)).toBe(
        true,
      );
      await page.keyboard.press('Tab');
      expect(await actions.nth(1).evaluate((action) => action === document.activeElement)).toBe(
        true,
      );

      await actions.nth(1).evaluate((action) => action.setAttribute('disabled', ''));
      expect(await actions.nth(1).isDisabled()).toBe(true);
      expect(await actions.allTextContents()).toEqual(domOrder);
    },
  );

  it.each([
    { width: 390, size: 'lg', expectedWidth: 390 },
    { width: 599, size: 'md', expectedWidth: 599 },
    { width: 600, size: 'lg', expectedWidth: 568 },
    { width: 900, size: 'md', expectedWidth: 560 },
    { width: 900, size: 'lg', expectedWidth: 760 },
  ])('keeps $size chrome visible while only the dialog scrolls at $width px', async (scenario) => {
    const { width, size, expectedWidth } = scenario;
    const height = 700;
    await page.setViewportSize({ width, height });
    await page.setContent(`<!doctype html><html lang="vi"><head><style>
      ${source('src/styles/tokens.css')}
      ${source('src/styles/helm/dialog.css')}
      ${source('src/app/ui/dialog/lib/hlm-dialog-content.css')}
      body { margin: 0; min-height: 100dvh; display: grid; place-items: center; }
    </style></head><body>
      <hlm-dialog-content class="spartan-dialog-content spartan-dialog-${size}" data-size="${size}">
        <header class="spartan-dialog-header" data-slot="dialog-header"><span hlmDialogStatusIcon aria-hidden="true">!</span>
          <h2>Đổi vai trò</h2><p hlmDialogSubject>Yêu cầu đang chờ xử lý</p></header>
        <div>${'<p>Chi tiết yêu cầu</p>'.repeat(80)}</div>
        <footer class="spartan-dialog-footer" data-slot="dialog-footer"><button>Hủy</button></footer>
      </hlm-dialog-content>
    </body></html>`);
    const surface = page.locator('hlm-dialog-content');
    const header = surface.locator('header');
    const footer = surface.locator('footer');
    const rect = await surface.boundingBox();
    if (!rect) throw new Error('Dialog has no layout box');
    expect(rect.width).toBeCloseTo(expectedWidth, 0);
    expect(rect.height).toBeCloseTo(width < 600 ? height : height - 32, 0);
    const chrome = [header, footer];
    const before = await Promise.all(chrome.map((part) => part.boundingBox()));
    expect(
      await header.locator('[hlmDialogStatusIcon]').evaluate((icon) => {
        const subject = icon.parentElement?.querySelector('[hlmDialogSubject]');
        return (
          !!subject && icon.getBoundingClientRect().right < subject.getBoundingClientRect().left
        );
      }),
    ).toBe(true);
    const scrolled = await surface.evaluate((dialog) => {
      dialog.scrollTop = dialog.scrollHeight;
      return dialog.scrollTop;
    });
    expect(scrolled).toBeGreaterThan(rect.height);
    for (const [index, part] of chrome.entries()) {
      const start = before[index];
      const end = await part.boundingBox();
      if (!start || !end) throw new Error('Dialog chrome has no layout box');
      for (const key of ['x', 'y', 'width', 'height'] as const) {
        expect(end[key]).toBeCloseTo(start[key], 0);
      }
      expect(end.y).toBeGreaterThanOrEqual(rect.y - 1);
      expect(end.y + end.height).toBeLessThanOrEqual(rect.y + rect.height + 1);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
    expect(await page.evaluate(() => window.scrollY)).toBe(0);
  });
});
