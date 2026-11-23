// @vitest-environment node
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { chromium, type Browser, type Page } from '@playwright/test';
import postcss from 'postcss';
import { compile } from 'tailwindcss';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

function source(path: string): string {
  const paths = [resolve(path), resolve('apps/web', path), resolve('../..', path)];
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
    { width: 390, size: 'xl', expectedWidth: 390, columns: 1 },
    { width: 599, size: 'md', expectedWidth: 599, columns: 1 },
    { width: 600, size: 'lg', expectedWidth: 568, columns: 1 },
    { width: 900, size: 'md', expectedWidth: 560, columns: 2 },
    { width: 900, size: 'lg', expectedWidth: 760, columns: 2 },
    { width: 1279, size: 'xl', expectedWidth: 760, columns: 2 },
    { width: 1280, size: 'lg', expectedWidth: 760, columns: 2 },
    { width: 1280, size: 'xl', expectedWidth: 1024, columns: 3 },
  ])('keeps $size chrome visible while only the dialog scrolls at $width px', async (scenario) => {
    const { width, size, expectedWidth } = scenario;
    const height = 700;
    await page.setViewportSize({ width, height });
    await page.setContent(`<!doctype html><html lang="vi"><head><style>
      ${source('src/styles/tokens.css')}
      ${source('src/styles/helm/dialog.css')}
      ${source('src/styles/helm/field-grid.css')}
      ${source('src/app/ui/dialog/lib/hlm-dialog-content.css')}
      .spartan-dialog-lg { font-size: 12px; }
      body { margin: 0; min-height: 100dvh; display: grid; place-items: center; }
    </style></head><body>
      <hlm-dialog-content class="spartan-dialog-content spartan-dialog-${size}" data-size="${size}">
        <header class="spartan-dialog-header" data-slot="dialog-header"><span hlmDialogStatusIcon aria-hidden="true">!</span>
          <h2>Đổi vai trò</h2><p hlmDialogSubject>Yêu cầu đang chờ xử lý</p></header>
        <dl class="hlm-field-grid"><div><dt>A</dt><dd>1</dd></div><div><dt>B</dt><dd>2</dd></div>
          <div class="hlm-field-wide"><dt>ID</dt><dd>${'x'.repeat(256)}</dd></div></dl>
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
    const grid = surface.locator('dl');
    expect(
      await grid.evaluate((el) => getComputedStyle(el).gridTemplateColumns.split(' ').length),
    ).toBe(scenario.columns);
    const wide = await grid.locator('.hlm-field-wide').boundingBox();
    expect(wide?.width).toBeCloseTo((await grid.boundingBox())!.width, 0);
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

  it.each(['no-preference', 'reduce'] as const)(
    'keeps entering dialog text opaque with %s motion',
    async (reducedMotion) => {
      const require = createRequire(import.meta.url);
      const theme = readFileSync(require.resolve('tailwindcss/theme.css'), 'utf8');
      const animations = source('node_modules/tw-animate-css/dist/tw-animate.css');
      const variants = source('src/styles/spartan-variants.css');
      const rules: string[] = [];
      footerRecipe.walkRules(/^\.spartan-dialog-(content|overlay)$/u, (rule) => {
        rules.push(rule.toString());
      });
      const compiler = await compile(`${theme}\n${animations}\n${variants}\n${rules.join('\n')}`);
      await page.emulateMedia({ reducedMotion });
      await page.setContent(`<!doctype html><html lang="en"><head><style>
        ${source('src/styles/tokens.css')}
        ${compiler.build([])}
      </style></head><body>
        <div class="spartan-dialog-overlay" data-state="open"></div>
        <section class="spartan-dialog-content" data-state="open"><p>Setup record</p></section>
      </body></html>`);
      const surface = page.locator('.spartan-dialog-content');
      const overlay = page.locator('.spartan-dialog-overlay');
      const count = await surface.evaluate((element) => element.getAnimations().length);
      expect(count).toBe(reducedMotion === 'reduce' ? 0 : 1);
      if (reducedMotion === 'reduce') {
        expect(await overlay.evaluate((element) => element.getAnimations().length)).toBe(0);
        expect(await surface.evaluate((element) => getComputedStyle(element).opacity)).toBe('1');
        return;
      }
      const samples = await surface.evaluate((element) => {
        const animation = element.getAnimations()[0];
        if (!animation) {
          throw new Error('Expected a dialog entry animation to sample.');
        }
        animation.pause();
        const duration = Number(animation.effect?.getComputedTiming().duration);
        return [0, 0.25, 0.5, 0.75, 1].map((progress) => {
          animation.currentTime = duration * progress;
          return {
            opacity: getComputedStyle(element).opacity,
            transform: getComputedStyle(element).transform,
          };
        });
      });
      expect(samples.map((sample) => sample.opacity)).toEqual(['1', '1', '1', '1', '1']);
      const [firstSample, , , , lastSample] = samples;
      if (!firstSample || !lastSample) {
        throw new Error('Expected dialog entry samples at both animation endpoints.');
      }
      expect(firstSample.transform).not.toBe(lastSample.transform);
      for (const layer of [overlay, surface]) {
        const opacity = await layer.evaluate((element) => {
          element.setAttribute('data-state', 'closed');
          const animation = element.getAnimations()[0];
          if (!animation) {
            throw new Error('Expected a dialog exit animation to sample.');
          }
          animation.pause();
          animation.currentTime = Number(animation.effect?.getComputedTiming().duration) / 2;
          return Number(getComputedStyle(element).opacity);
        });
        expect(opacity).toBeGreaterThan(0);
        expect(opacity).toBeLessThan(1);
      }
    },
  );
});
