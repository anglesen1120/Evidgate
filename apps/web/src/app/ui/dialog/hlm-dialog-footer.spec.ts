// @vitest-environment node
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { chromium, type Browser } from '@playwright/test';
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
  let css: string;

  beforeAll(async () => {
    expect(footerRules).toHaveLength(1);
    const require = createRequire(import.meta.url);
    const theme = readFileSync(require.resolve('tailwindcss/theme.css'), 'utf8');
    const compiler = await compile(`${theme}\n${footerRules.join('\n')}\n@tailwind utilities;`);
    css = compiler.build(footerClasses.split(/\s+/u));
    browser = await chromium.launch({ channel: 'chrome' });
  });

  afterAll(async () => {
    await browser?.close();
  });

  it.each(scenarios)(
    'keeps $locale $variant DOM, visual and keyboard order at $width px in $theme',
    async ({ theme, locale, cancel, commit, variant, width }) => {
      const page = await browser.newPage({ viewport: { width, height: 900 } });
      try {
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
      } finally {
        await page.close();
      }
    },
  );
});
